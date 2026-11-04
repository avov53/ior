use crate::{
    ipc_contract::WireDns,
    native_router::{emit_decision, NativeRouter},
    tun_names::{self, NameCache, Question},
    udp_relay::DatagramRelay,
};
use std::{
    io,
    sync::{
        atomic::{AtomicUsize, Ordering},
        Arc,
    },
    time::Duration,
};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    sync::Mutex,
};

/// DNS sockets are pooled by route target, never returned through the TUN adapter.
pub struct TunDns {
    virtual_dns_address: Option<std::net::IpAddr>,
    family_probes: std::sync::Mutex<FamilyProbes>,
    router: Arc<NativeRouter>,
    pub names: Arc<NameCache>,
    upstreams: Vec<String>,
    bootstrap: String,
    ipv4_only: bool,
    ipv6_only: bool,
    proxy: std::collections::HashMap<String, Vec<Mutex<Option<Box<dyn DatagramRelay>>>>>,
    direct: Vec<Mutex<Option<Box<dyn DatagramRelay>>>>,
    next: AtomicUsize,
    cache: Arc<crate::dns_response_cache::DnsResponseCache>,
    inflight: std::sync::Mutex<
        std::collections::HashMap<(Vec<u8>, String, bool), std::sync::Weak<Mutex<()>>>,
    >,
}
#[derive(Default)]
struct FamilyProbes {
    tasks: tokio::task::JoinSet<()>,
    recent: std::collections::HashMap<String,std::time::Instant>,
}

#[cfg(test)]
mod tests {
    // Opt-in public DNS checks through an already running local proxy. No profile,
    // new VPN session, interface or route is created by this fixture.
    struct ExistingProxyDns(u16);
    struct ExistingProxyDnsRelay(tokio::net::TcpStream);
    impl DatagramRelay for ExistingProxyDnsRelay {
        fn send<'a>(&'a mut self,query:&'a [u8])->SendFuture<'a>{Box::pin(async move{self.0.write_u16(query.len() as u16).await?;self.0.write_all(query).await?;self.0.flush().await})}
        fn receive(&mut self)->ReceiveFuture<'_>{Box::pin(async move{let length=self.0.read_u16().await? as usize;if length<12{return Err(io::ErrorKind::InvalidData.into());}let mut reply=vec![0;length];self.0.read_exact(&mut reply).await?;Ok(reply)})}
    }
    impl OutboundConnector for ExistingProxyDns {
        fn connect(&self,_:String,_:u16)->ConnectFuture{Box::pin(async{Err(io::ErrorKind::Unsupported.into())})}
        fn udp(&self,host:String,port:u16)->UdpFuture{let proxy_port=self.0;Box::pin(async move{
            let mut stream=tokio::net::TcpStream::connect((std::net::Ipv4Addr::LOCALHOST,proxy_port)).await?;
            stream.write_all(format!("CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n").as_bytes()).await?;
            let mut header=Vec::new();while header.len()<8192 {header.push(stream.read_u8().await?);if header.ends_with(b"\r\n\r\n"){break;}}
            if !header.starts_with(b"HTTP/1.1 200") && !header.starts_with(b"HTTP/1.0 200"){return Err(io::ErrorKind::ConnectionRefused.into());}
            Ok(Box::new(ExistingProxyDnsRelay(stream)) as Box<dyn DatagramRelay>)
        })}
    }
    #[tokio::test]
    #[ignore="public DNS via existing local proxy; IOR_EXISTING_PROXY_DNS, no VPN/network mutation"]
    async fn live_cname_soa_uses_positive_backup_through_existing_proxy(){
        let port=std::env::var("IOR_EXISTING_PROXY_DNS").unwrap().parse().unwrap();
        let proxy=ExistingProxyDns(port);
        let query=crate::dns_engine::encode_address_question("tr.rbxcdn.com",[0x35,0x42],1).unwrap();
        let question=tun_names::question(&query).unwrap();
        let mut primary=proxy.udp("77.88.8.8".into(),53).await.unwrap();primary.send(&query).await.unwrap();
        let primary_reply=tokio::time::timeout(Duration::from_secs(8),primary.receive()).await.unwrap().unwrap();
        validate_answer(&primary_reply,&question,true).unwrap();
        println!("primaryNeedsBackup={}",address_answer_needs_backup(&primary_reply,&question).unwrap());
        let router=NativeRouter::new(Arc::new(proxy),vec![WireRule{kind:"MATCH".into(),value:"".into(),target:"PROXY".into(),no_resolve:true,source:"test".into()}],"127.0.0.1:9".parse().unwrap(),None).unwrap();
        let dns=TunDns::new(Arc::new(router),&WireDns{bootstrap:"127.0.0.1".into(),upstream:"77.88.8.8".into(),upstreams:vec!["77.88.8.8".into(),"8.8.8.8".into()],ipv4_only:false}).unwrap();
        for kind in [1,28,65]{let query=crate::dns_engine::encode_address_question("tr.rbxcdn.com",[0x35,0x42],kind).unwrap();for _ in 0..2 {
            let answer=dns.query(&query,false).await.unwrap();assert_eq!(answer[3]&15,0);
            // CDN AAAA availability may differ between resolver exits. An actual
            // NODATA response must remain valid; only A is required for this asset.
            if kind==1 {assert!(!crate::dns_engine::decode_address_answers(&answer,&query).unwrap().is_empty());}
        }}
    }
    struct AliasDns(bool,u8,bool);
    struct AliasRelay(bool,u8,Option<Vec<u8>>);
    impl DatagramRelay for AliasRelay {
        fn send<'a>(&'a mut self,query:&'a [u8])->SendFuture<'a>{Box::pin(async move{
            let mut reply=query.to_vec();reply[2]=0x81;reply[3]=0x80;reply[7]=1;
            if self.0 {
                let kind=tun_names::question(query)?.kind;
                let data=if kind==1{vec![203,0,113,7]}else{"2001:db8::7".parse::<std::net::Ipv6Addr>().unwrap().octets().to_vec()};
                reply.extend([0xc0,0x0c]);reply.extend(kind.to_be_bytes());reply.extend([0,1,0,0,0,30]);reply.extend((data.len() as u16).to_be_bytes());reply.extend(data);
            }else {
                if self.1<2 {reply.extend([0xc0,0x0c,0,5,0,1,0,0,0,30,0,8,5,b'a',b'l',b'i',b'a',b's',0xc0,0x10]);}else{reply[7]=0;}
                if self.1>0 {reply[9]=1;reply.extend([0xc0,0x0c,0,6,0,1,0,0,0,30,0,24,0xc0,0x0c,0xc0,0x0c]);reply.extend([0;20]);}
                if self.1==3 {reply[3]=0x83;}
            }
            self.2=Some(reply);Ok(())
        })}
        fn receive(&mut self)->ReceiveFuture<'_>{Box::pin(async move{self.2.take().ok_or_else(||io::ErrorKind::UnexpectedEof.into())})}
    }
    impl OutboundConnector for AliasDns {
        fn connect(&self,_:String,_:u16)->ConnectFuture{Box::pin(async{Err(io::ErrorKind::Unsupported.into())})}
        fn udp(&self,host:String,_:u16)->UdpFuture{let backup=host=="8.8.8.8";let complete=backup && !self.2;let fail=self.0 && backup;let mode=self.1;Box::pin(async move{if fail{return Err(io::ErrorKind::ConnectionRefused.into());}Ok(Box::new(AliasRelay(complete,mode,None)) as Box<dyn DatagramRelay>)})}
    }
    #[tokio::test]
    async fn nodata_including_cname_soa_tries_backup_and_preserves_real_negative_replies(){
        for mode in 0..=3 {for kind in [1,28] {for (failed_backup,negative_backup) in [(false,false),(true,false),(false,true)]{
            let router=NativeRouter::new(Arc::new(AliasDns(failed_backup,mode,negative_backup)),vec![WireRule{kind:"MATCH".into(),value:"".into(),target:"PROXY".into(),no_resolve:true,source:"test".into()}],"127.0.0.1:9".parse().unwrap(),None).unwrap();
            let dns=TunDns::new(Arc::new(router),&WireDns{bootstrap:"127.0.0.1".into(),upstream:"77.88.8.8".into(),upstreams:vec!["77.88.8.8".into(),"8.8.8.8".into()],ipv4_only:false}).unwrap();
            let query=packet(kind);let question=tun_names::question(&query).unwrap();
            for _ in 0..2 {let answer=dns.query(&query,false).await.unwrap();assert_eq!(answer[3]&15,if mode==3{3}else{0});
                let original=failed_backup||negative_backup||mode==3;
                assert_eq!(address_answer_needs_backup(&answer,&question).unwrap(),original && mode!=3);
                if !original {assert_eq!(crate::dns_engine::decode_address_answers(&answer,&query).unwrap().len(),1);}else{let mut reference=AliasRelay(false,mode,None);reference.send(&query).await.unwrap();assert_eq!(answer,reference.receive().await.unwrap());}
            }
        }}}
    }
    #[test]
    fn soa_nodata_can_try_backup_but_nxdomain_and_service_records_are_preserved(){
        let mut reply=packet(1);reply[2]=0x81;reply[3]=0x80;
        let question=tun_names::question(&packet(1)).unwrap();assert!(address_answer_needs_backup(&reply,&question).unwrap());
        reply[7]=1;reply.extend([0xc0,0x0c,0,5,0,1,0,0,0,30,0,2,0xc0,0x0c]);
        assert!(address_answer_needs_backup(&reply,&question).unwrap());
        reply[9]=1;reply.extend([0xc0,0x0c,0,6,0,1,0,0,0,30,0,24,0xc0,0x0c,0xc0,0x0c]);reply.extend([0;20]);assert!(address_answer_needs_backup(&reply,&question).unwrap());
        let mut service=question.clone();service.kind=65;assert!(!address_answer_needs_backup(&reply,&service).unwrap());
        reply[3]=0x83;assert!(!address_answer_needs_backup(&reply,&question).unwrap());
    }
    struct ProbeConnector(std::sync::Arc<super::NameCache>,std::sync::Arc<std::sync::atomic::AtomicUsize>,std::sync::Arc<std::sync::atomic::AtomicUsize>,std::sync::Arc<crate::dns_response_cache::DnsResponseCache>);
    struct ProbeRelay(Option<Vec<u8>>,std::sync::Arc<std::sync::atomic::AtomicUsize>);
    impl Drop for ProbeRelay {fn drop(&mut self){self.1.fetch_sub(1,std::sync::atomic::Ordering::Relaxed);}}
    impl crate::udp_relay::DatagramRelay for ProbeRelay {
        fn send<'a>(&'a mut self,packet:&'a [u8])->crate::udp_relay::SendFuture<'a>{Box::pin(async move{
            let mut p=packet.to_vec();p[2]=0x81;p[3]=0x80;p[7]=1;p.extend([0xc0,0x0c,0,28,0,1,0,0,0,30,0,16]);
            p.extend("2001:db8::5".parse::<std::net::Ipv6Addr>().unwrap().octets());self.0=Some(p);Ok(())
        })}
        fn receive(&mut self)->crate::udp_relay::ReceiveFuture<'_>{Box::pin(async move{tokio::time::sleep(std::time::Duration::from_millis(40)).await;Ok(self.0.take().unwrap())})}
    }
    impl crate::proxy_inbound::OutboundConnector for ProbeConnector {
        fn connect(&self,_:String,_:u16)->crate::proxy_inbound::ConnectFuture{Box::pin(async{Err(std::io::ErrorKind::Unsupported.into())})}
        fn ipv4_only(&self)->bool{true}
        fn packet_path_enabled(&self)->bool{true}
        fn dns_observations(&self)->Option<std::sync::Arc<super::NameCache>>{Some(self.0.clone())}
        fn dns_response_cache(&self)->Option<std::sync::Arc<crate::dns_response_cache::DnsResponseCache>>{Some(self.3.clone())}
        fn udp(&self,_:String,_:u16)->crate::udp_relay::UdpFuture{
            self.1.fetch_add(1,std::sync::atomic::Ordering::Relaxed);self.2.fetch_add(1,std::sync::atomic::Ordering::Relaxed);
            let active=self.2.clone();Box::pin(async move{Ok(Box::new(ProbeRelay(None,active)) as Box<dyn crate::udp_relay::DatagramRelay>)})
        }
    }
    #[tokio::test]
    async fn awg_cached_ipv6_probe_is_shared_nonblocking_bounded_and_cancelled() {
        use std::sync::{Arc,atomic::{AtomicUsize,Ordering}};
        let names=Arc::new(super::NameCache::default());let requests=Arc::new(AtomicUsize::new(0));let active=Arc::new(AtomicUsize::new(0));
        let cache=Arc::new(crate::dns_response_cache::DnsResponseCache::default());
        let router=Arc::new(super::NativeRouter::new(Arc::new(ProbeConnector(names.clone(),requests.clone(),active.clone(),cache.clone())),vec![
            crate::ipc_contract::WireRule{kind:"DOMAIN".into(),value:"private.test".into(),target:"DIRECT".into(),source:"test".into(),no_resolve:true},
            crate::ipc_contract::WireRule{kind:"MATCH".into(),value:String::new(),target:"PROXY".into(),source:"test".into(),no_resolve:true}],"1.1.1.1:53".parse().unwrap(),None).unwrap().with_full_awg_family_policy(true));
        let dns=super::TunDns::new(router.clone(),&crate::ipc_contract::WireDns{bootstrap:"1.1.1.1".into(),upstream:"8.8.8.8".into(),upstreams:vec![],ipv4_only:true}).unwrap();
        assert!(Arc::ptr_eq(&dns.names,&names));let ip="2001:db8::5".parse().unwrap();
        dns.observe_family_for_flow("private.test",ip).await;assert_eq!(dns.family_probes.lock().unwrap().tasks.len(),0);
        let query=crate::dns_engine::encode_address_question("video.test",[1,2],28).unwrap();
        let answer=dns.query(&query,false).await.unwrap();assert_eq!(answer[7],0);assert_eq!(requests.load(Ordering::Relaxed),0);
        assert!(Arc::ptr_eq(&cache,&dns.cache));
        let a=crate::dns_engine::encode_address_question("video.test",[3,4],1).unwrap();let mut reply=a.clone();reply[2]=0x81;reply[3]=0x80;reply[7]=1;
        reply.extend([0xc0,0x0c,0,1,0,1,0,0,0,30,0,4,198,51,100,11]);cache.insert(&a,"PROXY|8.8.8.8:53",false,&reply);
        assert_eq!(dns.query(&a,false).await.unwrap(),reply);assert_eq!(requests.load(Ordering::Relaxed),0);
        for _ in 0..20 {dns.observe_family_for_flow("video.test",ip).await;}
        assert_eq!(dns.family_probes.lock().unwrap().tasks.len(),1);
        tokio::time::timeout(std::time::Duration::from_secs(1),async{while names.lookup(ip).is_none(){tokio::time::sleep(std::time::Duration::from_millis(5)).await;}}).await.unwrap();
        assert_eq!(names.lookup(ip),Some("video.test".into()));assert_eq!(requests.load(Ordering::Relaxed),1);
        let(tx,mut rx)=tokio::sync::mpsc::channel(8);let mut packets=crate::awg_packets::Manager::new(router,tx).with_names(names.clone());
        let udp=crate::awg_packets::udp_packet("[fdfe::1]:4000".parse().unwrap(),"[2001:db8::5]:443".parse().unwrap(),b"probe");
        assert!(packets.process(&udp).await.unwrap().is_none());assert_eq!(rx.try_recv().unwrap()[6],58);
        // A DIRECT observation sharing that address must prevent this early refusal.
        let direct=crate::dns_engine::encode_address_question("private.test",[1,2],28).unwrap();
        let q=crate::tun_names::question(&direct).unwrap();let mut reply=direct;reply[2]=0x81;reply[3]=0x80;reply[7]=1;
        reply.extend([0xc0,0x0c,0,28,0,1,0,0,0,30,0,16]);reply.extend("2001:db8::5".parse::<std::net::Ipv6Addr>().unwrap().octets());names.observe(&reply,&q).unwrap();
        assert!(packets.process(&udp).await.unwrap().is_some());assert!(rx.try_recv().is_err());
        for n in 0..10 {dns.observe_family_for_flow(&format!("other{n}.test"),ip).await;}
        assert_eq!(dns.family_probes.lock().unwrap().tasks.len(),4);
        tokio::task::yield_now().await;drop(dns);
        tokio::time::timeout(std::time::Duration::from_secs(1),async{while active.load(Ordering::Relaxed)!=0{tokio::task::yield_now().await;}}).await.unwrap();
    }
    use super::*;
    use crate::{
        ipc_contract::WireRule,
        proxy_inbound::{ConnectFuture, OutboundConnector},
        udp_relay::{ReceiveFuture, SendFuture, UdpFuture},
    };
    struct ReferenceDns;
    struct TruncatedDns(Arc<AtomicUsize>, bool);
    struct TruncatedRelay(Option<Vec<u8>>);
    impl DatagramRelay for TruncatedRelay {
        fn send<'a>(&'a mut self, packet: &'a [u8]) -> SendFuture<'a> {
            Box::pin(async move { let mut answer=packet.to_vec(); answer[2]=0x83;answer[3]=0x80;self.0=Some(answer);Ok(()) })
        }
        fn receive(&mut self) -> ReceiveFuture<'_> { Box::pin(async {self.0.take().ok_or_else(||io::ErrorKind::UnexpectedEof.into())}) }
    }
    impl OutboundConnector for TruncatedDns {
        fn connect(&self, _: String, port: u16) -> ConnectFuture {
            assert_eq!(port,53);
            self.0.fetch_add(1,Ordering::SeqCst);
            let large=self.1;
            Box::pin(async move {
                let (client,mut server)=tokio::io::duplex(16);
                tokio::spawn(async move {
                    let length=server.read_u16().await.unwrap() as usize;
                    let mut answer=vec![0;length];server.read_exact(&mut answer).await.unwrap();
                    let question_end=tun_names::question(&answer).unwrap().end;
                    answer[2]=0x81;answer[3]=0x80;
                    if large {
                        let extra=answer.split_off(question_end);
                        answer[7]=1;answer.extend([0xc0,0x0c,0,16,0,1,0,0,0,10,2,91]);
                        for _ in 0..3 {answer.push(200);answer.extend([b'x';200]);}
                        answer.extend(extra);
                    }
                    server.write_u16(answer.len() as u16).await.unwrap();
                    server.write_all(&answer).await.unwrap();server.shutdown().await.unwrap();
                });
                Ok(Box::new(client) as Box<dyn crate::proxy_inbound::RelayStream>)
            })
        }
        fn udp(&self, _: String, _: u16) -> UdpFuture {Box::pin(async {Ok(Box::new(TruncatedRelay(None)) as Box<dyn DatagramRelay>)})}
    }
    #[tokio::test]
    async fn truncated_udp_retries_tcp_for_address_and_service_records() {
        let attempts=Arc::new(AtomicUsize::new(0));
        let router=NativeRouter::new(Arc::new(TruncatedDns(attempts.clone(),false)),vec![WireRule {kind:"MATCH".into(),value:"".into(),target:"PROXY".into(),no_resolve:true,source:"ior".into()}],"127.0.0.1:9".parse().unwrap(),None).unwrap();
        let dns=TunDns::new(Arc::new(router),&WireDns {bootstrap:"127.0.0.1".into(),upstream:"8.8.8.8".into(),ipv4_only:false,upstreams:vec![]}).unwrap();
        for kind in [1,28,64,65] {
            let query=packet(kind);
            let answer=tokio::time::timeout(Duration::from_secs(2),dns.query(&query,false)).await.unwrap().unwrap();
            let mut expected=query;expected[2]=0x81;expected[3]=0x80;
            assert_eq!(answer,expected);
        }
        assert_eq!(attempts.load(Ordering::SeqCst),4);
    }
    #[tokio::test]
    async fn tcp_fallback_respects_client_udp_limit_and_edns() {
        let router=NativeRouter::new(Arc::new(TruncatedDns(Arc::new(AtomicUsize::new(0)),true)),vec![WireRule {kind:"MATCH".into(),value:"".into(),target:"PROXY".into(),no_resolve:true,source:"ior".into()}],"127.0.0.1:9".parse().unwrap(),None).unwrap();
        let dns=TunDns::new(Arc::new(router),&WireDns {bootstrap:"127.0.0.1".into(),upstream:"8.8.8.8".into(),ipv4_only:false,upstreams:vec![]}).unwrap();
        let query=packet(16);
        let truncated=dns.query(&query,false).await.unwrap();assert_eq!(truncated[2]&2,2);assert!(truncated.len()<=512);
        let complete=dns.query(&query,true).await.unwrap();assert_eq!(complete[2]&2,0);assert!(complete.len()>512);
        let mut edns=query;edns[11]=1;edns.extend([0,0,41,4,208,0,0,0,0,0,0]);
        let question=tun_names::question(&edns).unwrap();assert_eq!(udp_payload_limit(&edns,&question),1232);
        let complete=dns.query(&edns,false).await.unwrap();assert_eq!(complete[2]&2,0);assert!(complete.len()>512);
    }
    #[test]
    fn dns_failures_are_distinct_and_never_successful_empty_answers() {
        let query=packet(65);let question=tun_names::question(&query).unwrap();
        let mut answer=query;answer[2]=0x81;answer[3]=0x82;
        assert_eq!(validate_answer(&answer,&question,false).unwrap_err().to_string(),"TUN_DNS_SERVER_FAILURE");
        answer[3]=0x80;answer[2]|=2;
        assert!(validate_answer(&answer,&question,false).is_ok());
        assert_eq!(validate_answer(&answer,&question,true).unwrap_err().to_string(),"TUN_DNS_TCP_TRUNCATED");
        answer[0]^=1;
        assert_eq!(validate_answer(&answer,&question,false).unwrap_err().to_string(),"TUN_DNS_RESPONSE_MISMATCH");
    }
    struct HedgedDns;
    struct StalledRelay;
    impl DatagramRelay for StalledRelay {
        fn send<'a>(&'a mut self, _: &'a [u8]) -> SendFuture<'a> {
            Box::pin(async { Ok(()) })
        }
        fn receive(&mut self) -> ReceiveFuture<'_> {
            Box::pin(std::future::pending())
        }
    }
    impl OutboundConnector for HedgedDns {
        fn dns_hedge_delay(&self) -> Duration {
            Duration::from_millis(150)
        }
        fn connect(&self, _: String, _: u16) -> ConnectFuture {
            Box::pin(async { Err(io::ErrorKind::Unsupported.into()) })
        }
        fn udp(&self, host: String, _: u16) -> UdpFuture {
            Box::pin(async move {
                Ok(if host == "8.8.8.8" {
                    Box::new(StalledRelay) as Box<dyn DatagramRelay>
                } else {
                    Box::new(ReferenceRelay(None))
                })
            })
        }
    }
    struct ReferenceRelay(Option<Vec<u8>>);
    #[tokio::test]
    async fn dns_proxy_is_not_blocked_by_bootstrap_nodata_for_an_ip_rule(){
        let bootstrap=tokio::net::UdpSocket::bind("127.0.0.1:0").await.unwrap();let address=bootstrap.local_addr().unwrap();
        let server=tokio::spawn(async move{let mut bytes=[0;4096];let(n,peer)=bootstrap.recv_from(&mut bytes).await.unwrap();let mut reply=bytes[..n].to_vec();reply[2]=0x81;reply[3]=0x80;reply[6..12].fill(0);bootstrap.send_to(&reply,peer).await.unwrap();});
        let router=Arc::new(NativeRouter::new(Arc::new(ReferenceDns),vec![
            WireRule{kind:"IP-SET".into(),value:"203.0.113.0/24".into(),target:"REJECT".into(),no_resolve:false,source:"test".into()},
            WireRule{kind:"DOMAIN".into(),value:"blocked.example".into(),target:"REJECT".into(),no_resolve:false,source:"test".into()},
            WireRule{kind:"MATCH".into(),value:String::new(),target:"PROXY".into(),no_resolve:false,source:"test".into()}
        ],address,None).unwrap());
        // Reproduce the old decision path's premature failure.
        assert_eq!(router.decide("cdn.example",None).await.err().unwrap().kind(),io::ErrorKind::NotFound);server.await.unwrap();
        let dns=TunDns::new(router.clone(),&WireDns{bootstrap:"127.0.0.1".into(),upstream:"8.8.8.8".into(),upstreams:vec![],ipv4_only:false}).unwrap();
        let answer=tokio::time::timeout(Duration::from_secs(1),dns.query(&packet(1),false)).await.unwrap().unwrap();assert_eq!(answer[7],1);
        assert_eq!(router.decide("cdn.example",Some("203.0.113.7".parse().unwrap())).await.unwrap().target,"REJECT");
        assert_eq!(router.decide_dns("blocked.example").unwrap().target,"REJECT");
    }
    impl DatagramRelay for ReferenceRelay {
        fn send<'a>(&'a mut self, packet: &'a [u8]) -> SendFuture<'a> {
            Box::pin(async move {
                let mut answer = packet.to_vec();
                answer[2] = 0x81;
                answer[3] = 0x80;
                answer[7] = 1;
                answer.extend([0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 10, 0, 4, 203, 0, 113, 7]);
                self.0 = Some(answer);
                Ok(())
            })
        }
        fn receive(&mut self) -> ReceiveFuture<'_> {
            Box::pin(async {
                self.0
                    .take()
                    .ok_or_else(|| io::ErrorKind::UnexpectedEof.into())
            })
        }
    }
    impl OutboundConnector for ReferenceDns {
        fn connect(&self, _: String, _: u16) -> ConnectFuture {
            Box::pin(async { Err(io::ErrorKind::Unsupported.into()) })
        }
        fn udp(&self, host: String, port: u16) -> UdpFuture {
            assert_eq!(host, "8.8.8.8");
            assert_eq!(port, 53);
            Box::pin(async { Ok(Box::new(ReferenceRelay(None)) as Box<dyn DatagramRelay>) })
        }
    }
    fn packet(kind: u16) -> Vec<u8> {
        let mut packet = vec![1, 2, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0];
        packet.extend([
            3, b'c', b'd', b'n', 7, b'e', b'x', b'a', b'm', b'p', b'l', b'e', 0,
        ]);
        packet.extend(kind.to_be_bytes());
        packet.extend([0, 1]);
        packet
    }
    fn resolver(target: &str) -> TunDns {
        let router = NativeRouter::new(
            Arc::new(ReferenceDns),
            vec![WireRule {
                kind: "MATCH".into(),
                value: "".into(),
                target: target.into(),
                no_resolve: true,
                source: "ior".into(),
            }],
            "127.0.0.1:9".parse().unwrap(),
            None,
        )
        .unwrap();
        TunDns::new(
            Arc::new(router),
            &WireDns {
                bootstrap: "127.0.0.1".into(),
                upstream: "8.8.8.8".into(),
                ipv4_only: true,
                upstreams: vec![],
            },
        )
        .unwrap()
    }
    #[tokio::test]
    async fn tun_dns_hedges_cancels_and_separates_resolver_cache() {
        let router = NativeRouter::new(
            Arc::new(HedgedDns),
            vec![WireRule {
                kind: "MATCH".into(),
                value: String::new(),
                target: "PROXY".into(),
                no_resolve: true,
                source: "ior".into(),
            }],
            "127.0.0.1:9".parse().unwrap(),
            None,
        )
        .unwrap();
        let dns = TunDns::new(
            Arc::new(router),
            &WireDns {
                bootstrap: "127.0.0.1".into(),
                upstream: "8.8.8.8".into(),
                upstreams: vec!["8.8.8.8".into(), "1.1.1.1".into()],
                ipv4_only: true,
            },
        )
        .unwrap();
        let start = tokio::time::Instant::now();
        let query = packet(1);
        let reply = dns.query(&query, false).await.unwrap();
        assert_eq!(reply[7], 1);
        assert!(start.elapsed() >= Duration::from_millis(150));
        assert!(start.elapsed() < Duration::from_millis(400));
        assert!(dns.proxy["8.8.8.8"]
            .iter()
            .all(|slot| slot.try_lock().unwrap().is_none()));
        assert!(dns.cache.get(&query, "PROXY|8.8.8.8", false).is_none());
        assert!(dns.cache.get(&query, "PROXY|1.1.1.1", false).is_some());
        assert!(dns.cache.get(&query, "DIRECT|1.1.1.1", false).is_none());
        assert!(dns.cache.get(&query, "PROXY|1.1.1.1", true).is_none());
        let start = tokio::time::Instant::now();
        assert_eq!(dns.query(&query, false).await.unwrap(), reply);
        assert!(start.elapsed() < Duration::from_millis(100));
    }
    #[tokio::test]
    async fn tun_dns_routes_proxy_answers_and_caches_the_name_before_reply() {
        let dns = resolver("PROXY");
        let answer = dns.query(&packet(1), false).await.unwrap();
        assert_eq!(answer[7], 1);
        assert_eq!(
            dns.names.lookup("203.0.113.7".parse().unwrap()),
            Some("cdn.example".into())
        );
        let mut query = packet(1);
        query[0] = 9;
        let answer = dns.query(&query, false).await.unwrap();
        assert_eq!(answer[0], 9);
    }
    #[tokio::test]
    async fn tun_dns_reject_and_ipv4_only_return_explicit_replies() {
        let denied = resolver("REJECT").query(&packet(1), false).await.unwrap();
        assert_eq!(denied[3] & 15, 5);
        assert_eq!(denied[7], 0);
        let nodata = resolver("PROXY").query(&packet(28), false).await.unwrap();
        assert_eq!(nodata[3] & 15, 0);
        assert_eq!(nodata[7], 0);
    }
    #[tokio::test]
    async fn dns_uses_free_carrier_instead_of_waiting_for_a_busy_one() {
        let dns = resolver("PROXY");
        let _busy = dns.proxy["8.8.8.8"][0].lock().await;
        let answer = tokio::time::timeout(Duration::from_millis(200), dns.query(&packet(1), false))
            .await
            .unwrap()
            .unwrap();
        assert_eq!(answer[7], 1);
    }
    struct CountRelay {
        count: Arc<std::sync::atomic::AtomicUsize>,
        inner: ReferenceRelay,
    }
    impl DatagramRelay for CountRelay {
        fn send<'a>(&'a mut self, packet: &'a [u8]) -> SendFuture<'a> {
            Box::pin(async move {
                self.count.fetch_add(1, Ordering::SeqCst);
                tokio::time::sleep(Duration::from_millis(20)).await;
                self.inner.send(packet).await
            })
        }
        fn receive(&mut self) -> ReceiveFuture<'_> {
            self.inner.receive()
        }
    }
    #[tokio::test]
    async fn duplicate_dns_requests_share_one_exchange_and_keep_their_transaction_ids() {
        let dns = Arc::new(resolver("PROXY"));
        let count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        *dns.proxy["8.8.8.8"][0].lock().await = Some(Box::new(CountRelay {
            count: count.clone(),
            inner: ReferenceRelay(None),
        }));
        let mut tasks = tokio::task::JoinSet::new();
        for id in 0..16u8 {
            let dns = dns.clone();
            tasks.spawn(async move {
                let mut query = packet(1);
                query[0] = id;
                let answer = dns.query(&query, false).await.unwrap();
                assert_eq!(answer[0], id);
            });
        }
        while let Some(result) = tasks.join_next().await {
            result.unwrap();
        }
        assert_eq!(count.load(Ordering::SeqCst), 1);
    }
    #[tokio::test]
    async fn ipv4_only_proxy_does_not_suppress_direct_aaaa() {
        let dns = resolver("DIRECT");
        *dns.direct[0].lock().await = Some(Box::new(ReferenceRelay(None)));
        let answer = dns.query(&packet(28), false).await.unwrap();
        assert_eq!(answer[7], 1);
    }
    #[test]
    fn ipv6_bootstrap_is_valid_without_global_ipv4_requirement() {
        let source = resolver("DIRECT");
        let dns = TunDns::new(
            source.router.clone(),
            &WireDns {
                bootstrap: "::1".into(),
                upstream: "2001:4860:4860::8888".into(),
                upstreams: vec![],
                ipv4_only: false,
            },
        )
        .unwrap();
        assert_eq!(dns.bootstrap, "::1");
        assert!(TunDns::new(
            source.router,
            &WireDns {
                bootstrap: "not-an-address".into(),
                upstream: "8.8.8.8".into(),
                upstreams: vec![],
                ipv4_only: false,
            }
        )
        .is_err());
    }
    struct Ipv4Reference;
    impl OutboundConnector for Ipv4Reference {
        fn ipv4_only(&self) -> bool {
            true
        }
        fn connect(&self, host: String, port: u16) -> ConnectFuture {
            ReferenceDns.connect(host, port)
        }
        fn udp(&self, host: String, port: u16) -> UdpFuture {
            ReferenceDns.udp(host, port)
        }
    }
    #[tokio::test]
    async fn transport_capability_prevents_unusable_aaaa_even_if_contract_flag_is_false() {
        let router = NativeRouter::new(
            Arc::new(Ipv4Reference),
            vec![WireRule {
                kind: "MATCH".into(),
                value: String::new(),
                target: "PROXY".into(),
                no_resolve: true,
                source: "ior".into(),
            }],
            "127.0.0.1:9".parse().unwrap(),
            None,
        )
        .unwrap();
        let dns = TunDns::new(
            Arc::new(router),
            &WireDns {
                bootstrap: "127.0.0.1".into(),
                upstream: "8.8.8.8".into(),
                upstreams: vec![],
                ipv4_only: false,
            },
        )
        .unwrap();
        let response = dns.query(&packet(28), false).await.unwrap();
        assert_eq!(response[7], 0);
        assert_eq!(response[3] & 15, 0);
        assert_eq!(dns.query(&packet(1), false).await.unwrap()[7], 1);
    }
}
impl TunDns {
    /// The host's synthetic DNS endpoint is not an upstream resolver. Only the
    /// host that owns this address enables the policy; external DoT is untouched.
    pub fn with_virtual_dns_address(mut self, address: std::net::IpAddr) -> Self {
        self.virtual_dns_address=Some(address);self
    }
    pub(crate) fn rejects_virtual_dot(&self,address:std::net::SocketAddr)->bool {
        address.port()==853 && self.virtual_dns_address==Some(address.ip())
    }
    pub fn new(router: Arc<NativeRouter>, dns: &WireDns) -> io::Result<Self> {
        if dns.upstream.parse::<std::net::IpAddr>().is_err()
            || dns.bootstrap.parse::<std::net::IpAddr>().is_err()
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "TUN_DNS_IP_REQUIRED",
            ));
        }
        let upstreams = if dns.upstreams.is_empty() {
            vec![dns.upstream.clone()]
        } else {
            dns.upstreams.clone()
        };
        if upstreams.len() > 4
            || upstreams
                .iter()
                .any(|server| server.parse::<std::net::IpAddr>().is_err())
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "DNS_RESOLVER_LIST_INVALID",
            ));
        }
        Ok(Self {
            virtual_dns_address: None,
            family_probes: Default::default(),
            router: router.clone(),
            names: router.proxy_dns_observations().unwrap_or_else(||Arc::new(NameCache::default())),
            proxy: upstreams
                .iter()
                .map(|server| (server.clone(), (0..4).map(|_| Mutex::new(None)).collect()))
                .collect(),
            upstreams,
            bootstrap: dns.bootstrap.clone(),
            ipv4_only: dns.ipv4_only || router.proxy_ipv4_only(),
            ipv6_only: router.proxy_ipv6_only(),
            direct: (0..4).map(|_| Mutex::new(None)).collect(),
            next: AtomicUsize::new(0),
            cache: router.proxy_dns_response_cache().unwrap_or_else(||Arc::new(crate::dns_response_cache::DnsResponseCache::default())),
            inflight: std::sync::Mutex::new(std::collections::HashMap::new()),
        })
    }
    pub async fn query(&self, packet: &[u8], tcp: bool) -> io::Result<Vec<u8>> {
        let question = tun_names::question(packet).map_err(|_| {
            crate::trace::code("dns", "TUN_DNS_QUERY_INVALID");
            io::Error::new(io::ErrorKind::InvalidData, "TUN_DNS_QUERY_INVALID")
        })?;
        crate::trace::emit(serde_json::json!({"stage":"dns","code":"DNS_QUERY_PARSED","queryType":question.kind}));
        let decision = self.router.decide_dns(&question.name)?;
        if crate::resource_diagnostics::enabled(&question.name){crate::resource_diagnostics::emit(&question.name,"RESOURCE_DNS_QUERY",serde_json::json!({"stage":"dns","queryType":question.kind,"target":decision.target}));}
        emit_decision(
            &question.name,
            53,
            &decision,
            if tcp { "dns-tcp" } else { "dns-udp" },
        );
        if decision.target == "REJECT" {
            crate::trace::code("dns", "DNS_RULE_REJECTED");
            return Ok(tun_names::empty_response(packet, &question, 5));
        }
        if decision.target=="PROXY" && matches!(question.kind,1|28|64|65) {self.schedule_family_observation(&question.name);}
        if self.ipv4_only && decision.target == "PROXY" && question.kind == 28 {
            crate::trace::code("dns", "DNS_IPV6_NODATA");
            return Ok(tun_names::empty_response(packet, &question, 0));
        }
        if self.ipv6_only && decision.target == "PROXY" && question.kind == 1 {
            crate::trace::code("dns", "DNS_IPV4_NODATA");
            return Ok(tun_names::empty_response(packet, &question, 0));
        }
        let flight = {
            let mut flights = self.inflight.lock().map_err(|_| io::ErrorKind::Other)?;
            flights.retain(|_, flight| flight.strong_count() > 0);
            let key = (packet[2..].to_vec(), decision.target.clone(), tcp);
            if let Some(flight) = flights.get(&key).and_then(std::sync::Weak::upgrade) {
                flight
            } else {
                if flights.len() >= 256 {
                    return Err(io::Error::new(
                        io::ErrorKind::WouldBlock,
                        "TUN_DNS_INFLIGHT_LIMIT",
                    ));
                }
                let flight = Arc::new(Mutex::new(()));
                flights.insert(key, Arc::downgrade(&flight));
                flight
            }
        };
        let _flight = flight.lock().await;
        let endpoints = if decision.target == "DIRECT" {
            vec![self.bootstrap.clone()]
        } else {
            self.upstreams.clone()
        };
        for endpoint in &endpoints {
            if let Some(response) = self.cache.get(packet,&format!("{}|{}",decision.target,endpoint),tcp) {
                if endpoints.len()>1 && address_answer_needs_backup(&response,&question)? {continue;}
                self.observe_names(&response, &question);
                crate::trace::emit(
                    serde_json::json!({"stage":"dns","code":"DNS_CACHE_HIT","resolver":endpoint}),
                );
                return Ok(response);
            }
        }
        let target = &decision.target;
        let question_ref = &question;
        // Preserve valid NODATA (including CNAME + SOA) if no resolver has an address.
        // A configured backup may have a positive answer despite the first resolver's
        // negative view. Never synthesize addresses or rewrite either DNS response.
        let partial=std::sync::Mutex::new(None);
        let partial_ref=&partial;
        let prefer_complete=endpoints.len()>1;
        let response = tokio::time::timeout(
            Duration::from_secs(8),
            crate::dns_exchange::hedged_with_delay(
                &endpoints,
                if target == "PROXY" {
                    self.router.proxy_dns_hedge_delay()
                } else {
                    Duration::from_millis(500)
                },
                |endpoint| async move {
                    let response = if tcp {
                        self.query_tcp(packet, question_ref, target, &endpoint).await
                    } else {
                        let response = self.query_udp(packet, question_ref, target, &endpoint).await?;
                        if response[2] & 2 != 0 {
                            crate::trace::emit(serde_json::json!({"stage":"dns","code":"DNS_TRUNCATED_TCP_RETRY","resolver":endpoint,"queryType":question_ref.kind}));
                            let full=self.query_tcp(packet, question_ref, target, &endpoint).await?;
                            if full.len()>udp_payload_limit(packet, question_ref) {
                                // Preserve the resolver's TC reply: the local client must
                                // use DNS/TCP rather than receive an oversized UDP reply.
                                crate::trace::code("dns","DNS_TCP_CLIENT_RETRY_REQUIRED");
                                Ok(response)
                            } else { Ok(full) }
                        } else { Ok(response) }
                    }?;
                    if prefer_complete && address_answer_needs_backup(&response,question_ref)? {
                        let mut saved=partial_ref.lock().map_err(|_|io::ErrorKind::Other)?;
                        if saved.is_none(){*saved=Some(response);}
                        return Err(io::Error::new(io::ErrorKind::NotFound,"TUN_DNS_ADDRESS_NODATA"));
                    }
                    self.cache
                        .insert(packet, &format!("{target}|{endpoint}"), tcp, &response);
                    Ok(response)
                },
            ),
        )
        .await
        .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "TUN_DNS_TIMEOUT"))
        .and_then(|result|result);
        let response=match response {Ok(answer)=>answer,Err(error)=>partial.into_inner().map_err(|_|io::ErrorKind::Other)?.ok_or(error)?};
        self.observe_names(&response, &question);
        crate::trace::emit(
            serde_json::json!({"stage":"dns","code":"DNS_RESPONSE_READY","target":decision.target,"sentBytes":packet.len(),"receivedBytes":response.len()}),
        );
        Ok(response)
    }
    fn observe_names(&self, response:&[u8], question:&Question) {
        if crate::resource_diagnostics::enabled(&question.name){crate::resource_diagnostics::emit(&question.name,"RESOURCE_DNS_RESPONSE",serde_json::json!({"stage":"dns","queryType":question.kind,"rcode":response[3]&15,"answerCount":u16::from_be_bytes([response[6],response[7]]),"truncated":response[2]&2!=0}));}
        if self.names.observe(response,question).is_err() && response[2]&2==0 && response[3]&15==0 {
            crate::trace::emit(serde_json::json!({"stage":"dns","code":"DNS_NAME_OBSERVATION_REJECTED","queryType":question.kind}));
        }
    }
    /// Obtain real resolver evidence for IPv6 cached before this TUN session.
    /// The application keeps its existing DNS response; DIRECT never starts this probe.
    pub(crate) async fn observe_family_for_flow(&self,host:&str,destination:std::net::IpAddr) {
        if !self.router.packet_path_enabled() || !self.ipv4_only || !destination.is_ipv6() {return;}
        if self.router.decide(host,Some(destination)).await.is_ok_and(|route|route.target=="PROXY") {
            self.schedule_family_observation(host);
        }
    }
    fn schedule_family_observation(&self,host:&str) {
        if !self.router.packet_path_enabled() || !self.ipv4_only || host.parse::<std::net::IpAddr>().is_ok(){return;}
        let Ok(mut state)=self.family_probes.lock() else{return;};
        while state.tasks.try_join_next().is_some() {}
        state.recent.retain(|_,at|at.elapsed()<Duration::from_secs(60));
        if state.tasks.len()>=4 || state.recent.len()>=128 || state.recent.contains_key(host){return;}
        state.recent.insert(host.to_owned(),std::time::Instant::now());
        let (router,names,servers,host)=(self.router.clone(),self.names.clone(),self.upstreams.clone(),host.to_owned());
        state.tasks.spawn(async move {
            let started=std::time::Instant::now();
            let result=tokio::time::timeout(Duration::from_secs(4),async {
                let mut id=[0;2];getrandom::getrandom(&mut id).map_err(io::Error::other)?;
                let query=crate::dns_engine::encode_address_question(&host,id,28)?;
                let question=tun_names::question(&query)?;
                let response=crate::dns_exchange::hedged_with_delay(&servers,Duration::from_millis(150),|server|{
                    let (router,query,question)=(&router,&query,&question);
                    async move {
                        let mut relay=router.udp_target("PROXY",&server,53).await?;
                        relay.send(query).await?;
                        let mut reply=relay.receive().await?;
                        if !tun_names::matches_response(&reply,question){return Err(io::ErrorKind::InvalidData.into());}
                        if reply[2]&2!=0 {
                            let mut tcp=router.connect_target("PROXY",&server,53).await?;
                            tcp.write_u16(query.len() as u16).await?;tcp.write_all(query).await?;tcp.flush().await?;
                            let size=tcp.read_u16().await? as usize;if size<12{return Err(io::ErrorKind::InvalidData.into());}
                            reply.resize(size,0);tcp.read_exact(&mut reply).await?;
                        }
                        // Validate the full response before accepting a winner.
                        crate::dns_engine::decode_address_answers(&reply,query)?;Ok(reply)
                    }
                }).await?;
                let count=crate::dns_engine::decode_address_answers(&response,&query)?.len();
                names.observe(&response,&question)?;Ok::<usize,io::Error>(count)
            }).await;
            let (reason,count)=match result{Ok(Ok(0))=>("no_addresses",0),Ok(Ok(count))=>("recorded",count),Ok(Err(_))=>("resolver_response_rejected",0),Err(_)=>("timeout",0)};
            crate::trace::emit(serde_json::json!({"stage":"dns","code":"AWG_FAMILY_DNS_OBSERVATION","queryType":28,"destinationName":host,"durationMs":started.elapsed().as_millis() as u64,"observedAddresses":count,"reason":reason}));
        });
    }
    async fn query_tcp(&self, packet: &[u8], question: &Question, target: &str, endpoint: &str) -> io::Result<Vec<u8>> {
        let mut stream = self.router.connect_target(target, endpoint, 53).await?;
        stream.write_u16(u16::try_from(packet.len()).map_err(|_| io::ErrorKind::InvalidData)?).await?;
        stream.write_all(packet).await?;
        stream.flush().await?;
        let length = stream.read_u16().await? as usize;
        if length < 12 { return Err(io::Error::new(io::ErrorKind::InvalidData,"TUN_DNS_RESPONSE_INVALID")); }
        let mut response = vec![0; length];
        stream.read_exact(&mut response).await?;
        validate_answer(&response, question, true)?;
        Ok(response)
    }
    async fn query_udp(
        &self,
        packet: &[u8],
        question: &Question,
        target: &str,
        endpoint: &str,
    ) -> io::Result<Vec<u8>> {
        let pool = if target == "DIRECT" {
            &self.direct
        } else {
            self.proxy
                .get(endpoint)
                .ok_or(io::ErrorKind::InvalidInput)?
        };
        let index = self.next.fetch_add(1, Ordering::Relaxed) % pool.len();
        // A slow exchange must not queue an unrelated DNS name behind it when
        // another carrier is idle. Keep the pool bounded and retain fair rotation.
        let available =
            (0..pool.len()).find_map(|offset| pool[(index + offset) % pool.len()].try_lock().ok());
        let mut slot = match available {
            Some(slot) => slot,
            None => pool[index].lock().await,
        };
        let mut relay = match slot.take() {
            Some(relay) => relay,
            None => self.router.udp_target(target, endpoint, 53).await?,
        };
        // Keep the deadline inside the lock so cancellation always discards a relay
        // which could still contain an answer belonging to the preceding request.
        let result = tokio::time::timeout(Duration::from_secs(6), async {
            relay.send(packet).await?;
            for _ in 0..16 {
                let response = relay.receive().await?;
                if tun_names::matches_response(&response, question) {
                    validate_answer(&response, question, false)?;
                    return Ok(response);
                }
            }
            Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "TUN_DNS_RESPONSE_MISMATCH",
            ))
        })
        .await
        .unwrap_or_else(|_| Err(io::Error::new(io::ErrorKind::TimedOut, "TUN_DNS_TIMEOUT")));
        if result.is_ok() {
            *slot = Some(relay);
        }
        result
    }
}

fn address_answer_needs_backup(response:&[u8],question:&Question)->io::Result<bool>{
    if !matches!(question.kind,1|28) || response[2]&2!=0 || response[3]&15!=0 {return Ok(false);}
    let mut offset=tun_names::name(response,12)?.1+4;
    let answers=u16::from_be_bytes([response[6],response[7]]) as usize;
    let authority=u16::from_be_bytes([response[8],response[9]]) as usize;
    if answers+authority>512{return Err(io::ErrorKind::InvalidData.into());}
    let mut address=false;
    for index in 0..answers+authority {
        offset=tun_names::name(response,offset)?.1;
        let header=response.get(offset..offset+10).ok_or(io::ErrorKind::InvalidData)?;
        let kind=u16::from_be_bytes([header[0],header[1]]);
        let size=u16::from_be_bytes([header[8],header[9]]) as usize;
        response.get(offset+10..offset+10+size).ok_or(io::ErrorKind::InvalidData)?;
        if header[2..4]==[0,1] && index<answers {address|=kind==question.kind && size==if question.kind==1{4}else{16};}
        offset+=10+size;
    }
    Ok(!address)
}
fn validate_answer(response: &[u8], question: &Question, tcp: bool) -> io::Result<()> {
    if !tun_names::matches_response(response, question) {
        return Err(io::Error::new(io::ErrorKind::InvalidData,"TUN_DNS_RESPONSE_MISMATCH"));
    }
    if tcp && response[2] & 2 != 0 {
        return Err(io::Error::new(io::ErrorKind::InvalidData,"TUN_DNS_TCP_TRUNCATED"));
    }
    if matches!(response[3] & 15, 2 | 5) {
        return Err(io::Error::new(io::ErrorKind::InvalidData,"TUN_DNS_SERVER_FAILURE"));
    }
    Ok(())
}

fn udp_payload_limit(packet: &[u8], question: &Question) -> usize {
    let word=|offset|packet.get(offset..offset+2).map(|v|u16::from_be_bytes([v[0],v[1]]) as usize);
    let count=word(6).unwrap_or(0)+word(8).unwrap_or(0)+word(10).unwrap_or(0);
    if count>64 { return 512; }
    let mut offset=question.end;
    for _ in 0..count {
        let Ok((_,end))=tun_names::name(packet,offset) else {return 512;};
        let Some(kind)=word(end) else {return 512;};
        let Some(size)=word(end+8) else {return 512;};
        if packet.get(end+10..end+10+size).is_none() {return 512;}
        if kind==41 {return word(end+2).unwrap_or(512).clamp(512,65507);}
        offset=end+10+size;
    }
    512
}
