//! Native VLESS over certificate-verified TLS. This transport only covers
//! ordinary VLESS/TLS/TCP; Reality, Vision and UDP require separate codecs.

use std::{io, net::SocketAddr, sync::Arc};
use tokio::{
    io::{self as tokio_io, AsyncWriteExt},
    net::TcpStream,
};
use tokio_rustls::{
    rustls::{pki_types::ServerName, ClientConfig, RootCertStore},
    TlsConnector,
};

use crate::{
    dns_engine,
    ipc_contract::WireProfile,
    proxy_inbound::{ConnectFuture, OutboundConnector, RelayStream},
    vless_codec,
};

#[derive(Clone)]
pub struct VlessTlsConnector {
    server: String,
    port: u16,
    server_name: ServerName<'static>,
    user_id: String,
    bootstrap: SocketAddr,
    tls: TlsConnector,
    profile:WireProfile,
    network:Option<crate::ipc_contract::WireNetwork>,
    transports:Arc<crate::vless_transport::Runtime>,
    roots:RootCertStore,
}

impl VlessTlsConnector {
    fn open(&self,host:String,port:u16,mux:bool)->ConnectFuture{
        let connector=self.clone();Box::pin(async move{
            let mut request=vless_codec::encode_tcp_request(&connector.user_id,&host,port)?;
            if mux{request[18]=3;request.truncate(19);}
            let template=connector.clone();let bootstrap=connector.bootstrap;let network=connector.network.clone();
            let dial:crate::vless_transport::Dial=Arc::new(move|p|{
                if p.protocol=="vless_reality" {match crate::vless_reality::RealityConnector::new(&p,bootstrap){Ok(c)=>c.with_network(network.clone()).secure_transport(),Err(e)=>Box::pin(async{Err(e)})}}
                else if p.server==template.profile.server&&p.port==template.profile.port&&p.server_name==template.profile.server_name{template.secure()}
                else{match Self::with_trust_roots(&p,bootstrap,template.roots.clone()){Ok(connector)=>connector.with_network(network.clone()).secure(),Err(error)=>Box::pin(async{Err(error)})}}
            });
            let remote=connector.transports.connect(connector.profile,dial).await?;
            Ok(crate::vless_transport::vless_stream(remote,request))
        })
    }
    pub fn from_profile(profile: &WireProfile, bootstrap: SocketAddr) -> io::Result<Self> {
        let mut roots = RootCertStore::empty();
        roots.extend(webpki_roots::TLS_SERVER_ROOTS.iter().cloned());
        Self::with_trust_roots(profile, bootstrap, roots)
    }

    fn with_trust_roots(
        profile: &WireProfile,
        bootstrap: SocketAddr,
        roots: RootCertStore,
    ) -> io::Result<Self> {
        crate::vless_transport::validate(profile)?;
        if profile.protocol != "vless_tls"
            || profile.public_key.is_some()
            || profile.short_id.is_some()
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "only ordinary VLESS/TLS is supported by this connector",
            ));
        }
        if profile.server.trim().is_empty() || profile.port == 0 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "invalid VLESS server endpoint",
            ));
        }
        uuid::Uuid::parse_str(&profile.credential)
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "invalid VLESS UUID"))?;
        let server_name = ServerName::try_from(profile.server_name.clone())
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "invalid TLS server name"))?;
        let mut config = ClientConfig::builder()
                .with_root_certificates(roots.clone())
                .with_no_client_auth();
        config.alpn_protocols=profile.options.alpn.as_ref().map(|values|values.iter().map(|v|v.as_bytes().to_vec()).collect()).unwrap_or_else(||match crate::vless_transport::kind(profile){"grpc"|"xhttp"=>vec![b"h2".to_vec()],"ws"|"httpupgrade"=>vec![b"http/1.1".to_vec()],_=>vec![]});
        let tls = TlsConnector::from(Arc::new(config));
        Ok(Self {
            server: profile.server.clone(),
            port: profile.port,
            server_name,
            user_id: profile.credential.clone(),
            bootstrap,
            tls,
            profile:profile.clone(),network:None,transports:Arc::new(Default::default()),roots,
        })
    }
    pub fn with_network(mut self,network:Option<crate::ipc_contract::WireNetwork>)->Self{self.network=network;self}
    pub fn secure_profile(profile:WireProfile,bootstrap:SocketAddr,network:Option<crate::ipc_contract::WireNetwork>)->ConnectFuture{
        match Self::from_profile(&profile,bootstrap){Ok(connector)=>connector.with_network(network).secure(),Err(error)=>Box::pin(async{Err(error)})}
    }
    fn secure(&self)->ConnectFuture{
        let connector=self.clone();Box::pin(async move{
            let started=std::time::Instant::now();let ip=dns_engine::resolve_server_ipv4(&connector.server,connector.bootstrap).await?;
            crate::trace::emit(serde_json::json!({"stage":"dns","code":"VLESS_SERVER_RESOLVED","durationMs":started.elapsed().as_millis() as u64}));
            let bound=crate::native_router::bound_socket(false,&connector.network)?;let socket=tokio::net::TcpSocket::from_std_stream(bound.into());
            let endpoint=SocketAddr::new(ip.into(),connector.port);let guard=crate::egress_registry::register(socket.local_addr()?,endpoint,false);
            let socket=socket.connect(endpoint).await?;socket.set_nodelay(true)?;
            let started=std::time::Instant::now();let remote=connector.tls.connect(connector.server_name,socket).await.map_err(|_|io::Error::new(io::ErrorKind::ConnectionAborted,"VLESS_TLS_HANDSHAKE_FAILED"))?;
            if matches!(crate::vless_transport::kind(&connector.profile),"grpc"|"xhttp")&&remote.get_ref().1.alpn_protocol()!=Some(b"h2"){return Err(io::Error::new(io::ErrorKind::Unsupported,"VLESS_HTTP2_ALPN_REQUIRED"));}
            crate::trace::emit(serde_json::json!({"stage":"handshake","code":"VLESS_TLS_COMPLETE","durationMs":started.elapsed().as_millis() as u64}));
            Ok(Box::new(crate::egress_registry::RegisteredStream::new(remote,guard))as Box<dyn RelayStream>)
        })
    }
}

impl OutboundConnector for VlessTlsConnector {
    fn named_tun_destination(&self)->bool {true}
    fn connect(&self,host:String,port:u16)->ConnectFuture {
        self.open(host,port,false)
    }
    fn udp(&self,host:String,port:u16)->crate::udp_relay::UdpFuture{
        let connection=self.open("v1.mux.cool".into(),666,true);
        Box::pin(async move{Ok(Box::new(crate::xudp::XudpDatagram::new(connection.await?,host,port)?) as Box<dyn crate::udp_relay::DatagramRelay>)})
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    use rcgen::{generate_simple_self_signed, CertifiedKey};
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::TcpListener;
    use tokio_rustls::{
        rustls::{
            pki_types::{PrivateKeyDer, PrivatePkcs8KeyDer},
            ServerConfig,
        },
        TlsAcceptor,
    };

    fn profile() -> WireProfile {
        WireProfile {
            protocol: "vless_tls".into(),
            server: "127.0.0.1".into(),
            port: 443,
            credential: "11111111-1111-1111-1111-111111111111".into(),
            server_name: "example.com".into(),
            public_key: None,
            short_id: None,
            flow: None,
            fingerprint: None,
            options: Default::default(),
        }
    }

    fn bootstrap() -> SocketAddr {
        "127.0.0.1:53".parse().unwrap()
    }

    #[tokio::test(flavor="multi_thread",worker_threads=2)]
    #[ignore = "starts an explicitly supplied local Xray reference, never the user's VPN"]
    async fn xray_tls_transport_matrix() {
        reference_matrix(false).await;
    }
    #[tokio::test(flavor="multi_thread",worker_threads=2)]
    #[ignore = "isolated REALITY reference with temporary keys"]
    async fn xray_reality_transport_matrix(){reference_matrix(true).await;}
    async fn reference_matrix(reality:bool){
        use std::process::{Command,Stdio};
        use std::time::Duration;
        struct Reference{child:std::process::Child,directory:std::path::PathBuf}
        impl Drop for Reference{fn drop(&mut self){let _=self.child.kill();let _=self.child.wait();let _=std::fs::remove_dir_all(&self.directory);}}
        let executable=std::env::var("IOR_XRAY_REFERENCE").expect("explicit IOR_XRAY_REFERENCE required");
        let keys=if reality{let output=Command::new(&executable).arg("x25519").output().unwrap();assert!(output.status.success());let keys=String::from_utf8(output.stdout).unwrap();let field=|names:&[&str]|keys.lines().find_map(|line|{let(name,value)=line.split_once(':')?;names.contains(&name.trim()).then(||value.trim().to_owned())}).expect("reference key field missing");Some((field(&["PrivateKey","Private key"]),field(&["Password","Password (PublicKey)","PublicKey","Public key"])))}else{None};
        // Xray's REALITY target parser treats a combined encrypted handshake as
        // such only above 512 bytes. A tiny localhost ECDSA certificate lands
        // on that boundary depending on its randomized signature length, leaving
        // Xray waiting for a separate Certificate record that rustls never sends.
        // Use a larger, still local-only certificate rather than retrying or
        // weakening the production client's authenticated handshake.
        let CertifiedKey{cert:cover_cert,signing_key:cover_key}=generate_simple_self_signed(vec![
            "localhost".into(),
            format!("{}.cover.ior.invalid", "a".repeat(63)),
            format!("{}.cover.ior.invalid", "b".repeat(63)),
            format!("{}.cover.ior.invalid", "c".repeat(63)),
        ]).unwrap();
        assert!(cover_cert.der().len()>512,"REALITY reference cover certificate must exceed the combined-flight boundary");
        let mut cover_config=ServerConfig::builder().with_no_client_auth().with_single_cert(vec![cover_cert.der().clone()],PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(cover_key.serialize_der()))).unwrap();cover_config.alpn_protocols=vec![b"h2".to_vec(),b"http/1.1".to_vec()];let acceptor=TlsAcceptor::from(Arc::new(cover_config));
        let cover=TcpListener::bind("127.0.0.1:0").await.unwrap();let cover_port=cover.local_addr().unwrap().port();let cover_task=tokio::spawn(async move{loop{let(socket,_)=cover.accept().await.unwrap();let acceptor=acceptor.clone();tokio::spawn(async move{if let Ok(mut socket)=acceptor.accept(socket).await{let _=tokio::io::copy(&mut socket,&mut tokio::io::sink()).await;}});}});
        let echo=TcpListener::bind("127.0.0.1:0").await.unwrap();let target=echo.local_addr().unwrap().port();
        let udp_echo=tokio::net::UdpSocket::bind(("127.0.0.1",target)).await.unwrap();
        let udp_echo_task=tokio::spawn(async move{let mut buffer=vec![0;65536];loop{let(n,peer)=udp_echo.recv_from(&mut buffer).await.unwrap();udp_echo.send_to(&buffer[..n],peer).await.unwrap();}});
        let echo_task=tokio::spawn(async move{loop{let(mut socket,_)=echo.accept().await.unwrap();println!("ECHO_ACCEPTED");tokio::spawn(async move{let(mut read,mut write)=socket.split();let _=tokio::io::copy(&mut read,&mut write).await;});}});
        let mut matrix=vec![("tcp","",false),("ws","",false),("httpupgrade","",false),("grpc","",false),("grpc","",true),("xhttp","stream-one",false),("xhttp","stream-up",false),("xhttp","packet-up",false),("xhttp","auto",false),("xhttp","download",false)];if reality{matrix.retain(|(transport,_,_)|!matches!(*transport,"ws"|"httpupgrade"));matrix.push(("tcp","vision",false));}
        for (transport,mode,multi) in matrix{
            let directory=std::env::temp_dir().join(format!("ior-vless-lab-{}",uuid::Uuid::new_v4()));std::fs::create_dir(&directory).unwrap();
            let CertifiedKey{cert,signing_key}=generate_simple_self_signed(vec!["localhost".into()]).unwrap();
            std::fs::write(directory.join("cert.pem"),cert.pem()).unwrap();std::fs::write(directory.join("key.pem"),signing_key.serialize_pem()).unwrap();
            let reserve=TcpListener::bind("127.0.0.1:0").await.unwrap();let port=reserve.local_addr().unwrap().port();drop(reserve);
            let mut stream=serde_json::json!({"network":transport,"security":"tls","tlsSettings":{"alpn":["h2","http/1.1"],"certificates":[{"certificateFile":directory.join("cert.pem"),"keyFile":directory.join("key.pem")} ]}});
            if let Some((private,_))=&keys{stream.as_object_mut().unwrap().remove("tlsSettings");stream["security"]="reality".into();stream["realitySettings"]=serde_json::json!({"target":format!("127.0.0.1:{cover_port}"),"serverNames":["localhost"],"privateKey":private,"shortIds":["0123456789abcdef"],"show":std::env::var_os("IOR_REFERENCE_DEBUG").is_some()});}
            match transport{"grpc"=>stream["grpcSettings"]=serde_json::json!({"serviceName":"iorlab","multiMode":multi}),"xhttp"=>stream["xhttpSettings"]=serde_json::json!({"path":"/iorlab","mode":"auto"}),"ws"=>stream["wsSettings"]=serde_json::json!({"path":"/iorlab"}),"httpupgrade"=>stream["httpupgradeSettings"]=serde_json::json!({"path":"/iorlab"}),_=>()};
            let config=serde_json::json!({"log":{"loglevel":"warning"},"inbounds":[{"listen":"127.0.0.1","port":port,"protocol":"vless","settings":{"clients":[{"id":"11111111-1111-1111-1111-111111111111","flow":if mode=="vision"{"xtls-rprx-vision"}else{""}}],"decryption":"none"},"streamSettings":stream}],"outbounds":[{"protocol":"freedom","settings":{"finalRules":[{"action":"allow","ip":["127.0.0.1/32"],"port":target.to_string()}]}}]});
            std::fs::write(directory.join("config.json"),config.to_string()).unwrap();let mut command=Command::new(&executable);command.args(["run","-c"]).arg(directory.join("config.json")).stdout(Stdio::inherit()).stderr(Stdio::inherit());
            #[cfg(windows)]{use std::os::windows::process::CommandExt;command.creation_flags(0x08000000);}
            let mut reference=Reference{child:command.spawn().unwrap(),directory};
            for _ in 0..100{assert!(reference.child.try_wait().unwrap().is_none(),"reference exited {transport}");if TcpStream::connect(("127.0.0.1",port)).await.is_ok(){break;}tokio::time::sleep(Duration::from_millis(20)).await;}
            let mut proxy_task=None;let mut client_port=port;
            if let Some((private,_))=keys.as_ref().filter(|_|std::env::var_os("IOR_VERIFY_REALITY_HELLO").is_some()){
                use base64::Engine;let private=base64::engine::general_purpose::URL_SAFE_NO_PAD.decode(private).unwrap();let proxy=TcpListener::bind("127.0.0.1:0").await.unwrap();client_port=proxy.local_addr().unwrap().port();
                proxy_task=Some(tokio::spawn(async move{loop{let(mut client,_)=proxy.accept().await.unwrap();let private=private.clone();tokio::spawn(async move{let mut header=[0;5];client.read_exact(&mut header).await.unwrap();let mut hello=vec![0;u16::from_be_bytes([header[3],header[4]]) as usize];client.read_exact(&mut hello).await.unwrap();assert!(verify_wire_hello(&hello,&private),"REALITY wire ClientHello authentication mismatch");let mut server=TcpStream::connect(("127.0.0.1",port)).await.unwrap();server.write_all(&header).await.unwrap();server.write_all(&hello).await.unwrap();let _=tokio::io::copy_bidirectional(&mut client,&mut server).await;});}}));
            }
            let mut p=profile();p.server_name="localhost".into();p.port=client_port;p.options.transport=Some(transport.into());p.options.path=Some("/iorlab".into());p.options.service_name=Some("iorlab".into());p.options.vless_transport=Some(crate::ipc_contract::VlessTransportOptions{mode:match mode{""|"vision"=>None,"download"=>Some("stream-up".into()),value=>Some(value.into())},multi_mode:multi,..Default::default()});
            let connector:Arc<dyn OutboundConnector>=if let Some((_,public))=&keys{p.protocol="vless_reality".into();p.public_key=Some(public.clone());p.short_id=Some("0123456789abcdef".into());p.fingerprint=Some("chrome".into());p.flow=(mode=="vision").then(||"xtls-rprx-vision".into());if mode=="download"{apply_download(&mut p);}Arc::new(crate::vless_reality::RealityConnector::new(&p,bootstrap()).unwrap())}else{if mode=="download"{apply_download(&mut p);}let mut roots=RootCertStore::empty();roots.add(cert.der().clone()).unwrap();Arc::new(VlessTlsConnector::with_trust_roots(&p,bootstrap(),roots).unwrap())};
            for repetition in 0..3{
                println!("START {transport} {mode} {multi}");let started=std::time::Instant::now();let remote=tokio::time::timeout(Duration::from_secs(10),connector.connect("127.0.0.1".into(),target)).await.unwrap().unwrap();
                let(mut read,mut write)=tokio::io::split(remote);let expected=vec![0x35+repetition;2*1024*1024];let mut received=vec![0;expected.len()];
                tokio::time::timeout(Duration::from_secs(30),async{tokio::try_join!(async{write.write_all(&expected).await?;write.flush().await},async{let mut quick=connector.connect("127.0.0.1".into(),target).await?;quick.write_all(b"concurrent").await?;quick.flush().await?;let mut reply=[0;10];quick.read_exact(&mut reply).await?;assert_eq!(&reply,b"concurrent");Ok::<_,io::Error>(())},async{let mut offset=0;while offset<received.len(){let n=read.read(&mut received[offset..]).await?;if n==0{return Err(io::ErrorKind::UnexpectedEof.into());}offset+=n;if offset==n{println!("FIRST_ECHO {transport} {n}");}}Ok::<_,io::Error>(())})}).await.unwrap().unwrap();
                assert_eq!(received,expected,"{transport} {mode} {multi}");drop(read);drop(write);
                let mut cancelled=connector.connect("127.0.0.1".into(),target).await.unwrap();cancelled.write_all(&vec![0x77;32768]).await.unwrap();drop(cancelled);
                let mut small=connector.connect("127.0.0.1".into(),target).await.unwrap();small.write_all(b"parallel-small").await.unwrap();let mut reply=[0;14];tokio::time::timeout(Duration::from_secs(5),small.read_exact(&mut reply)).await.unwrap().unwrap();assert_eq!(&reply,b"parallel-small");
                tokio::time::timeout(Duration::from_secs(5),async{
                    let mut udp=connector.udp("127.0.0.1".into(),target).await?;
                    for size in [32,1200,4096]{let payload=vec![0x61+repetition;size];udp.send(&payload).await?;let reply=udp.receive().await?;assert_eq!(reply,payload,"XUDP {transport} {mode} {multi}");}
                    Ok::<_,io::Error>(())
                }).await.expect("reference XUDP timeout").expect("reference XUDP exchange failed");
                if mode=="vision"{assert!(connector.udp("127.0.0.1".into(),443).await.is_err(),"Vision must retain UDP/443 policy");}
                println!("REFERENCE reality={reality} transport={transport} mode={mode} multi={multi} repetition={repetition} bytes={} durationMs={}",expected.len(),started.elapsed().as_millis());
            }
            if let Some(task)=proxy_task{task.abort();}
        }
        echo_task.abort();udp_echo_task.abort();cover_task.abort();
    }
    fn apply_download(profile:&mut WireProfile){let mut download=profile.clone();download.options.vless_transport.as_mut().unwrap().mode=None;download.options.vless_transport.as_mut().unwrap().download=None;profile.options.vless_transport.as_mut().unwrap().download=Some(Box::new(download));}
    fn verify_wire_hello(hello:&[u8],private:&[u8])->bool{
        use aws_lc_rs::{agreement,aead};
        let mut offset=71;let cipher=u16::from_be_bytes(hello[offset..offset+2].try_into().unwrap())as usize;offset+=2+cipher;offset+=1+hello[offset]as usize;offset+=2;
        let mut share=None;
        while offset+4<=hello.len(){let kind=u16::from_be_bytes(hello[offset..offset+2].try_into().unwrap());let length=u16::from_be_bytes(hello[offset+2..offset+4].try_into().unwrap())as usize;let data=&hello[offset+4..offset+4+length];if kind==51{let mut at=2;while at+4<=data.len(){let group=u16::from_be_bytes(data[at..at+2].try_into().unwrap());let n=u16::from_be_bytes(data[at+2..at+4].try_into().unwrap())as usize;let key=&data[at+4..at+4+n];if group==29{share=Some(key.to_vec());}else if group==0x11ec&&share.is_none(){share=Some(key[key.len()-32..].to_vec());}at+=4+n;}}offset+=4+length;}
        let shared=agreement::agree(&agreement::PrivateKey::from_private_key(&agreement::X25519,private).unwrap(),agreement::UnparsedPublicKey::new(&agreement::X25519,share.unwrap()),(),|secret|Ok::<_,()>(secret.to_vec())).unwrap();let random:[u8;32]=hello[6..38].try_into().unwrap();let key=reality_provider::reality::derive_reality_auth_key(&shared,&random).unwrap();let mut sid=hello[39..71].to_vec();let mut aad=hello.to_vec();aad[39..71].fill(0);
        aead::LessSafeKey::new(aead::UnboundKey::new(&aead::AES_256_GCM,&key).unwrap()).open_in_place(aead::Nonce::assume_unique_for_key(random[20..32].try_into().unwrap()),aead::Aad::from(&aad),&mut sid).is_ok()
    }

    #[test]
    fn rejects_reality_and_invalid_identity_before_opening_socket() {
        let mut profile = profile();
        assert!(VlessTlsConnector::from_profile(&profile, bootstrap()).is_ok());
        profile.protocol = "vless_reality".into();
        assert!(VlessTlsConnector::from_profile(&profile, bootstrap()).is_err());
        profile.protocol = "vless_tls".into();
        profile.credential = "not-a-uuid".into();
        assert!(VlessTlsConnector::from_profile(&profile, bootstrap()).is_err());
    }

    #[tokio::test]
    async fn refuses_plaintext_server_instead_of_downgrading_tls() {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let mut profile = profile();
        profile.port = listener.local_addr().unwrap().port();
        let server = tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            socket.write_all(b"not TLS").await.unwrap();
        });
        let connector = VlessTlsConnector::from_profile(&profile, bootstrap()).unwrap();
        let result = connector.connect("example.org".into(), 443).await;
        assert!(result.is_err());
        server.await.unwrap();
    }

    #[tokio::test]
    async fn relays_vless_tcp_over_verified_tls_to_reference_server() {
        let CertifiedKey { cert, signing_key } =
            generate_simple_self_signed(vec!["example.com".to_string()]).unwrap();
        let mut roots = RootCertStore::empty();
        roots.add(cert.der().clone()).unwrap();
        let key = PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(signing_key.serialize_der()));
        let server_config = ServerConfig::builder()
            .with_no_client_auth()
            .with_single_cert(vec![cert.der().clone()], key)
            .unwrap();
        let acceptor = TlsAcceptor::from(Arc::new(server_config));
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let mut profile = profile();
        profile.port = listener.local_addr().unwrap().port();
        let server = tokio::spawn(async move {
            let (socket, _) = listener.accept().await.unwrap();
            let mut secure = acceptor.accept(socket).await.unwrap();
            let mut request = vec![0; 34];
            secure.read_exact(&mut request).await.unwrap();
            assert_eq!(
                &request[..22],
                &[
                    0, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11,
                    0x11, 0x11, 0x11, 0x11, 0, 1, 1, 0xbb, 2
                ]
            );
            assert_eq!(&request[23..], b"example.org");
            let mut message = [0; 4];
            secure.read_exact(&mut message).await.unwrap();
            assert_eq!(&message, b"ping");
            secure.write_all(&[0, 0]).await.unwrap();
            secure.write_all(b"pong").await.unwrap();
        });
        let connector = VlessTlsConnector::with_trust_roots(&profile, bootstrap(), roots).unwrap();
        let mut stream = connector.connect("example.org".into(), 443).await.unwrap();
        stream.write_all(b"ping").await.unwrap();
        let mut answer = [0; 4];
        stream.read_exact(&mut answer).await.unwrap();
        assert_eq!(&answer, b"pong");
        server.await.unwrap();
    }
}
