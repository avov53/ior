use ior_core::{ipc_contract::StartRequest, native_router::NativeRouter, proxy_inbound::OutboundConnector, tun::PacketDevice};
use jni::{JNIEnv, JavaVM, objects::{GlobalRef,JObject,JString,JValue}, sys::{jint,jstring}};
use std::{io, sync::{Arc,Mutex,OnceLock,atomic::{AtomicU64,Ordering}}, os::fd::{AsRawFd,FromRawFd,OwnedFd}, time::Duration};
use tokio::{io::{unix::AsyncFd,AsyncReadExt,AsyncWriteExt},task::JoinHandle};

static RUNTIME:OnceLock<tokio::runtime::Runtime>=OnceLock::new();
static SESSION:Mutex<Option<Session>>=Mutex::new(None);
static CANCEL:OnceLock<tokio::sync::Notify>=OnceLock::new();
static EVENTS:Mutex<std::collections::VecDeque<serde_json::Value>>=Mutex::new(std::collections::VecDeque::new());
static PROBE_LOCK:tokio::sync::Mutex<()>=tokio::sync::Mutex::const_new(());
static PROBE_CANCEL:OnceLock<tokio::sync::Notify>=OnceLock::new();
static PROBE_GENERATION:AtomicU64=AtomicU64::new(0);
static EVENT_DROPS:AtomicU64=AtomicU64::new(0);
fn runtime()->&'static tokio::runtime::Runtime {RUNTIME.get_or_init(||tokio::runtime::Builder::new_multi_thread().worker_threads(2).enable_all().build().unwrap())}
struct Session { task:JoinHandle<io::Result<()>>, device:Arc<Device>, outbound:Arc<dyn OutboundConnector>, profile:ior_core::ipc_contract::WireProfile, router:Arc<NativeRouter>, lan:Option<ior_core::lan_proxy::LanListener> }
struct Device { fd:AsyncFd<OwnedFd>, upload:AtomicU64, download:AtomicU64 }
impl Device {
    fn duplicate(fd:i32)->io::Result<Self>{
        let fd=unsafe{libc::fcntl(fd,libc::F_DUPFD_CLOEXEC,0)};
        if fd<0{return Err(io::Error::last_os_error());}
        let owned=unsafe{OwnedFd::from_raw_fd(fd)};
        let flags=unsafe{libc::fcntl(fd,libc::F_GETFL)};
        if flags<0 || unsafe{libc::fcntl(fd,libc::F_SETFL,flags|libc::O_NONBLOCK)}<0{return Err(io::Error::last_os_error());}
        Ok(Self{fd:AsyncFd::new(owned)?,upload:AtomicU64::new(0),download:AtomicU64::new(0)})
    }
}
impl PacketDevice for Device {
    fn recv<'a>(&'a self,buffer:&'a mut[u8])->impl std::future::Future<Output=io::Result<usize>>+Send+'a{async move{loop{
        let mut ready=self.fd.readable().await?;
        match ready.try_io(|fd|{let n=unsafe{libc::read(fd.get_ref().as_raw_fd(),buffer.as_mut_ptr().cast(),buffer.len())};if n<0{Err(io::Error::last_os_error())}else if n==0{Err(io::ErrorKind::UnexpectedEof.into())}else{Ok(n as usize)}}){
            Ok(result)=>{let n=result?;self.upload.fetch_add(n as u64,Ordering::Relaxed);return Ok(n)},Err(_)=>continue
        }
    }}}
    fn send<'a>(&'a self,buffer:&'a[u8])->impl std::future::Future<Output=io::Result<usize>>+Send+'a{async move{loop{
        let mut ready=self.fd.writable().await?;
        match ready.try_io(|fd|{let n=unsafe{libc::write(fd.get_ref().as_raw_fd(),buffer.as_ptr().cast(),buffer.len())};if n<0{Err(io::Error::last_os_error())}else if n as usize!=buffer.len(){Err(io::ErrorKind::WriteZero.into())}else{Ok(n as usize)}}){
            Ok(result)=>{let n=result?;self.download.fetch_add(n as u64,Ordering::Relaxed);return Ok(n)},Err(_)=>continue
        }
    }}}
}
fn payload(env:&mut JNIEnv,input:JString)->io::Result<StartRequest>{
    let text:String=env.get_string(&input).map_err(|_|io::ErrorKind::InvalidInput)?.into();
    if text.len()>8*1024*1024{return Err(io::ErrorKind::InvalidInput.into());}
    let mut request:StartRequest=serde_json::from_str(&text).map_err(|_|io::ErrorKind::InvalidInput)?;
    request.validate_shape().map_err(|_|io::ErrorKind::InvalidInput)?;
    if !matches!(request.profile.protocol.as_str(),"amneziawg"|"hysteria2"|"vless_reality"|"vless_tls"){return Err(io::ErrorKind::Unsupported.into());}
    if request.profile.protocol=="amneziawg" {request.profile.options.awg_engine=Some("rust".into());ior_core::tunnel_outbound::TunnelConnector::validate_profile(&request.profile)?;}
    // Windows interface identifiers and Wintun settings never enter Android.
    request.network=None;request.tun=None;
    Ok(request)
}
fn protect(vm:Arc<JavaVM>,callback:GlobalRef)->Arc<dyn Fn(i32)->io::Result<()>+Send+Sync>{protect_method(vm,callback,"protectSocket")}
fn protect_method(vm:Arc<JavaVM>,callback:GlobalRef,method:&'static str)->Arc<dyn Fn(i32)->io::Result<()>+Send+Sync>{Arc::new(move|fd|{
    let mut env=vm.attach_current_thread().map_err(|_|io::ErrorKind::Other)?;
    let protected=env.call_method(callback.as_obj(),method,"(I)Z",&[JValue::Int(fd)]).and_then(|v|v.z());
    if env.exception_check().unwrap_or(true){let _=env.exception_clear();return Err(io::Error::other("ANDROID_SOCKET_PROTECTION_FAILED"));}
    if protected.unwrap_or(false){Ok(())}else{Err(io::Error::other("ANDROID_SOCKET_PROTECTION_FAILED"))}
})}
fn answer(env:&JNIEnv,result:io::Result<()>)->jstring{
    let value=match result{Ok(())=>serde_json::json!({"ok":true}),Err(e)=>{
        let raw=e.to_string();let code=if raw.len()<=80&&raw.bytes().all(|b|b.is_ascii_uppercase()||b.is_ascii_digit()||b==b'_'){raw}else{format!("ANDROID_CORE_{:?}",e.kind())};
        serde_json::json!({"ok":false,"code":code})
    }};env.new_string(value.to_string()).map(|s|s.into_raw()).unwrap_or(std::ptr::null_mut())
}
#[no_mangle]
pub extern "system" fn Java_app_ior_android_NativeCore_validateRoutes(mut env:JNIEnv,_:JObject,input:JString)->jstring {
    let result=(||{
        let text:String=env.get_string(&input).map_err(|_|io::ErrorKind::InvalidInput)?.into();
        if text.len()>8*1024*1024{return Err(io::ErrorKind::InvalidInput.into());}
        let rules=serde_json::from_str(&text).map_err(|_|io::ErrorKind::InvalidInput)?;
        struct Unavailable;impl OutboundConnector for Unavailable{fn connect(&self,_:String,_:u16)->ior_core::proxy_inbound::ConnectFuture{Box::pin(async{Err(io::ErrorKind::Unsupported.into())})}}
        NativeRouter::new(Arc::new(Unavailable),rules,"1.1.1.1:53".parse().unwrap(),None)?;Ok(())
    })();answer(&env,result)
}
async fn health(connector:&Arc<dyn OutboundConnector>)->io::Result<()> {
    let stream=connector.connect("www.gstatic.com".into(),443).await?;
    let _=tokio_rustls::rustls::crypto::ring::default_provider().install_default();
    let roots=tokio_rustls::rustls::RootCertStore::from_iter(webpki_roots::TLS_SERVER_ROOTS.iter().cloned());
    let config=tokio_rustls::rustls::ClientConfig::builder().with_root_certificates(roots).with_no_client_auth();
    let mut tls=tokio_rustls::TlsConnector::from(Arc::new(config)).connect("www.gstatic.com".try_into().unwrap(),stream).await?;
    tls.write_all(b"GET /generate_204 HTTP/1.1\r\nHost: www.gstatic.com\r\nConnection: close\r\n\r\n").await?;
    let mut reply=[0;12];tls.read_exact(&mut reply).await?;
    if reply.starts_with(b"HTTP/1.1 204")||reply.starts_with(b"HTTP/1.1 200"){Ok(())}else{Err(io::Error::other("ANDROID_HTTPS_PROBE_FAILED"))}
}
#[no_mangle]
pub extern "system" fn Java_app_ior_android_NativeCore_validate(mut env:JNIEnv,_:JObject,input:JString)->jstring{
    let result=payload(&mut env,input).and_then(|request|{
        let bootstrap=std::net::SocketAddr::new(request.dns.bootstrap.parse().map_err(|_|io::ErrorKind::InvalidInput)?,53);
        if request.profile.protocol=="vless_reality"{ior_core::vless_reality::RealityConnector::new(&request.profile,bootstrap)?;}
        if request.profile.protocol=="vless_tls"{ior_core::vless_tls::VlessTlsConnector::from_profile(&request.profile,bootstrap)?;}
        // Route compilation is independent from network initialization.
        struct Unavailable;impl OutboundConnector for Unavailable{fn connect(&self,_:String,_:u16)->ior_core::proxy_inbound::ConnectFuture{Box::pin(async{Err(io::ErrorKind::Unsupported.into())})}}
        NativeRouter::new(Arc::new(Unavailable),request.routes,bootstrap,None)?;Ok(())
    });answer(&env,result)
}
#[no_mangle]
pub extern "system" fn Java_app_ior_android_NativeCore_start(mut env:JNIEnv,_:JObject,input:JString,fd:jint,mtu:jint,callback:JObject)->jstring{
    let result=(||{
        let _probe_guard=runtime().block_on(PROBE_LOCK.lock());
        ior_core::trace::set_sink(Some(Arc::new(|event|{
            let mut safe=serde_json::Map::new();for key in ["code","stage","flowId","sessionId","protocol","profileTransport","classification","direction","phase","severity","elapsedMs","durationMs","sentBytes","receivedBytes","queueWaitUs","readWaitUs","sendWaitUs","waitCount","queryType","errorType","transport","side","operation","readEof","writeClosed","kind","reason","rttEstimateMs","proxyQueueDrops","tunQueueDrops","queuedDatagrams","queuedBytes","receiveQueueDrops","cwndBytes","lostPackets","sentPackets","count","lastReceivedAgoMs","lastSentAgoMs","afterFirstSendMs","active","fallback","maxPacketBytes","mtu","icmpType","icmpCode","rxWindowBytes","peerWindowBytes","txQueuedBytes","rxZeroWindowUs","peerZeroWindowUs","applicationWaitUs","reassemblyDroppedPackets"]{if let Some(value)=event.get(key){if value.is_number()||value.is_boolean()||value.as_str().is_some_and(|s|s.len()<=80&&s.bytes().all(|b|b.is_ascii_alphanumeric()||b==b'_'||b==b'-')){safe.insert(key.into(),value.clone());}}}
            for key in ["destinationName","host","domain"]{if let Some(value)=event.get(key).and_then(|v|v.as_str()){if value.len()<=253&&value.bytes().all(|b|b.is_ascii_alphanumeric()||matches!(b,b'.'|b'-'|b'_')){safe.insert(key.into(),serde_json::Value::String(value.into()));}}}
            for key in ["source","destination","resolver"]{if let Some(value)=event.get(key).and_then(|v|v.as_str()){if value.parse::<std::net::SocketAddr>().is_ok(){safe.insert(key.into(),value.into());}}}
            for key in ["path","target","addressFamily","nameSource"]{if let Some(value)=event.get(key).and_then(|v|v.as_str()){if value.len()<=80&&value.bytes().all(|b|b.is_ascii_alphanumeric()||b==b'_'){safe.insert(key.into(),value.into());}}}
            for key in ["receiveWaitUs","dnsNameCount","ttlSeconds","tunDeliveredBytes","forwardedPackets","receivedPackets","droppedPackets","rxQueuedBytes","parentFlowId","firstResponseUs","udpSessionId","rxQueuedDatagrams","rxQueueCapacity","rxQueueHighWater","newReceiveQueueDrops"]{if let Some(value)=event.get(key).filter(|v|v.is_number()){safe.insert(key.into(),value.clone());}}
            safe.insert("eventUnixMs".into(),(std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default().as_millis().min(u64::MAX as u128) as u64).into());
            // Only hold the lock for one bounded push; concurrent workers must not lose
            // events simply because another worker is pushing at the same instant.
            if let Ok(mut queue)=EVENTS.lock(){if queue.len()<4096{queue.push_back(safe.into());}else{EVENT_DROPS.fetch_add(1,Ordering::Relaxed);}}else{EVENT_DROPS.fetch_add(1,Ordering::Relaxed);}
        })));
        let request=payload(&mut env,input)?;
        ior_core::trace::set_context(&request.request_id,&request.profile.protocol,&request.profile.options.transport.clone().unwrap_or_else(||match request.profile.protocol.as_str(){"amneziawg"=>"packet".into(),"hysteria2"=>"quic".into(),_=>"tcp".into()}));
        ior_core::trace::code("profile","ANDROID_PROFILE_VALIDATED");
        let mut session=SESSION.lock().map_err(|_|io::ErrorKind::Other)?;
        if session.is_some(){return Err(io::Error::other("ANDROID_SESSION_ALREADY_ACTIVE"));}
        let vm=Arc::new(env.get_java_vm().map_err(|_|io::ErrorKind::Other)?);
        let callback=env.new_global_ref(callback).map_err(|_|io::ErrorKind::Other)?;
        ior_core::android_network::set_lan_protector(Some(protect_method(vm.clone(),callback.clone(),"protectLanSocket")));
        let protector=protect(vm,callback);
        ior_core::android_network::set_protector(Some(protector.clone()));rsteria2::android_socket::set(Some(protector));
        ior_core::trace::code("egress","ANDROID_SOCKET_PROTECTOR_READY");
        let operation=runtime().block_on(async{tokio::select! {
          _=CANCEL.get_or_init(tokio::sync::Notify::new).notified()=>Err(io::ErrorKind::Interrupted.into()),
          result=async{
            let bootstrap=std::net::SocketAddr::new(request.dns.bootstrap.parse().map_err(|_|io::ErrorKind::InvalidInput)?,53);
            let connector:Arc<dyn OutboundConnector>=match request.profile.protocol.as_str(){
                "amneziawg"=>Arc::new(ior_core::tunnel_outbound::TunnelConnector::new(&request.profile,bootstrap,None).await?.with_upstreams(request.dns.upstreams.iter().chain(std::iter::once(&request.dns.upstream)).map(|ip|ip.parse().map(|ip|std::net::SocketAddr::new(ip,53)).map_err(|_|io::ErrorKind::InvalidInput)).collect::<Result<Vec<_>,_>>()?)?),
                "hysteria2"=>Arc::new(ior_core::hysteria2::Hysteria2Connector::from_profile_bound(&request.profile,bootstrap,None).await?),
                "vless_tls"=>Arc::new(ior_core::vless_tls::VlessTlsConnector::from_profile(&request.profile,bootstrap)?),
                _=>Arc::new(ior_core::vless_reality::RealityConnector::new(&request.profile,bootstrap)?)
            };
            tokio::time::timeout(Duration::from_secs(25),health(&connector)).await.map_err(|_|io::ErrorKind::TimedOut)??;
            ior_core::trace::code("probe","ANDROID_HTTPS_PROBE_PASSED");
            let outbound=connector.clone();
            let router=Arc::new(NativeRouter::new(connector,request.routes,bootstrap,None)?.with_full_awg_family_policy(request.profile.protocol=="amneziawg").with_route_dns(&request.dns));
            let dns=Arc::new(ior_core::tun_dns::TunDns::new(router.clone(),&request.dns)?.with_virtual_dns_address("172.19.0.2".parse().map_err(|_|io::ErrorKind::InvalidInput)?));
            let device=Arc::new(Device::duplicate(fd)?);
            let task=ior_core::tun::start_device(device.clone(),u16::try_from(mtu).map_err(|_|io::ErrorKind::InvalidInput)?,router.clone(),dns).await?;
            Ok::<_,io::Error>(Session{task,device,outbound,profile:request.profile,router,lan:None})
          }=>result
        }});
        match operation{Ok(active)=>{*session=Some(active);Ok(())},Err(error)=>{ior_core::android_network::set_lan_protector(None);ior_core::android_network::set_protector(None);rsteria2::android_socket::set(None);Err(error)}}
    })();answer(&env,result)
}
#[no_mangle]
pub extern "system" fn Java_app_ior_android_NativeCore_stop(env:JNIEnv,_:JObject)->jstring{
    CANCEL.get_or_init(tokio::sync::Notify::new).notify_waiters();
    let result=(||{if let Some(session)=SESSION.lock().map_err(|_|io::ErrorKind::Other)?.take(){session.task.abort();let _=runtime().block_on(session.task);}ior_core::android_network::set_lan_protector(None);ior_core::android_network::set_protector(None);rsteria2::android_socket::set(None);Ok(())})();answer(&env,result)
}
#[no_mangle]
pub extern "system" fn Java_app_ior_android_NativeCore_stats(env:JNIEnv,_:JObject)->jstring{
    let value=SESSION.lock().ok().and_then(|s|s.as_ref().map(|s|serde_json::json!({"running":!s.task.is_finished(),"sentBytes":s.device.upload.load(Ordering::Relaxed),"receivedBytes":s.device.download.load(Ordering::Relaxed)}))).unwrap_or_else(||serde_json::json!({"running":false}));
    env.new_string(value.to_string()).map(|s|s.into_raw()).unwrap_or(std::ptr::null_mut())
}
#[no_mangle]
pub extern "system" fn Java_app_ior_android_NativeCore_events(env:JNIEnv,_:JObject)->jstring{
    let mut rows:Vec<_>=EVENTS.lock().map(|mut q|q.drain(..).collect()).unwrap_or_default();
    let dropped=EVENT_DROPS.swap(0,Ordering::Relaxed);if dropped>0{rows.push(serde_json::json!({"code":"ANDROID_LOG_EVENTS_DROPPED","droppedEvents":dropped}));}
    env.new_string(serde_json::to_string(&rows).unwrap()).map(|s|s.into_raw()).unwrap_or(std::ptr::null_mut())
}
#[no_mangle]
pub extern "system" fn Java_app_ior_android_NativeCore_cancelProbes(_:JNIEnv,_:JObject){PROBE_GENERATION.fetch_add(1,Ordering::SeqCst);PROBE_CANCEL.get_or_init(tokio::sync::Notify::new).notify_waiters();}
async fn connector_for(request:&StartRequest)->io::Result<Arc<dyn OutboundConnector>>{
    let bootstrap=std::net::SocketAddr::new(request.dns.bootstrap.parse().map_err(|_|io::ErrorKind::InvalidInput)?,53);
    Ok(match request.profile.protocol.as_str(){
        "amneziawg"=>Arc::new(ior_core::tunnel_outbound::TunnelConnector::new(&request.profile,bootstrap,None).await?.with_upstreams(request.dns.upstreams.iter().chain(std::iter::once(&request.dns.upstream)).map(|ip|ip.parse().map(|ip|std::net::SocketAddr::new(ip,53)).map_err(|_|io::ErrorKind::InvalidInput)).collect::<Result<Vec<_>,_>>()?)?),
        "hysteria2"=>Arc::new(ior_core::hysteria2::Hysteria2Connector::from_profile_bound(&request.profile,bootstrap,None).await?),
                "vless_tls"=>Arc::new(ior_core::vless_tls::VlessTlsConnector::from_profile(&request.profile,bootstrap)?),
        _=>Arc::new(ior_core::vless_reality::RealityConnector::new(&request.profile,bootstrap)?)
    })
}
async fn http_probe(connector:&Arc<dyn OutboundConnector>,method:&str,url:&str)->io::Result<()> {
    let uri:http::Uri=url.parse().map_err(|_|io::ErrorKind::InvalidInput)?;
    if uri.scheme_str()!=Some("https")||uri.authority().is_none()||uri.authority().unwrap().as_str().contains('@')||!matches!(method,"HEAD"|"GET"){return Err(io::ErrorKind::InvalidInput.into());}
    let host=uri.host().ok_or(io::ErrorKind::InvalidInput)?.trim_matches(['[',']']).to_owned();
    let transport=connector.connect(host.clone(),uri.port_u16().unwrap_or(443)).await?;
    let _=tokio_rustls::rustls::crypto::ring::default_provider().install_default();
    let roots=tokio_rustls::rustls::RootCertStore::from_iter(webpki_roots::TLS_SERVER_ROOTS.iter().cloned());
    let config=tokio_rustls::rustls::ClientConfig::builder().with_root_certificates(roots).with_no_client_auth();
    let mut tls=tokio_rustls::TlsConnector::from(Arc::new(config)).connect(host.try_into().map_err(|_|io::ErrorKind::InvalidInput)?,transport).await?;
    let request=format!("{method} {} HTTP/1.1\r\nHost: {}\r\nConnection: close\r\n\r\n",uri.path_and_query().map(|p|p.as_str()).unwrap_or("/"),uri.authority().unwrap());
    tls.write_all(request.as_bytes()).await?;tls.flush().await?;
    let mut bytes=Vec::new();let mut buffer=[0;4096];
    loop{let limit=bytes.windows(4).position(|p|p==b"\r\n\r\n").map(|end|(end+4+65536).saturating_sub(bytes.len()).min(buffer.len())).unwrap_or(buffer.len());if limit==0{return Ok(());}let n=tls.read(&mut buffer[..limit]).await?;if n==0{break;}bytes.extend_from_slice(&buffer[..n]);if let Some(end)=bytes.windows(4).position(|p|p==b"\r\n\r\n"){
        let code=std::str::from_utf8(&bytes[..end]).ok().and_then(|s|s.lines().next()).and_then(|s|s.split_whitespace().nth(1)).and_then(|s|s.parse::<u16>().ok());
        if !code.is_some_and(|n|(200..400).contains(&n)){return Err(io::Error::other("PROBE_HTTP_STATUS"));}
        if method=="HEAD"||bytes.len()>=end+4+65536||code==Some(204){return Ok(());}
    }else if bytes.len()>16384{return Err(io::ErrorKind::InvalidData.into());}}
    if bytes.windows(4).any(|p|p==b"\r\n\r\n"){Ok(())}else{Err(io::ErrorKind::UnexpectedEof.into())}
}
#[no_mangle]
pub extern "system" fn Java_app_ior_android_NativeCore_probe(mut env:JNIEnv,_:JObject,input:JString,method:JString,url:JString,timeout:jint,callback:JObject)->jstring{
    let operation=(||{
        let generation=PROBE_GENERATION.load(Ordering::SeqCst);
        let request=payload(&mut env,input)?;
        let method:String=env.get_string(&method).map_err(|_|io::ErrorKind::InvalidInput)?.into();
        let url:String=env.get_string(&url).map_err(|_|io::ErrorKind::InvalidInput)?.into();
        if !(100..=30000).contains(&timeout){return Err(io::ErrorKind::InvalidInput.into());}
        let vm=Arc::new(env.get_java_vm().map_err(|_|io::ErrorKind::Other)?);let callback=env.new_global_ref(callback).map_err(|_|io::ErrorKind::Other)?;
        runtime().block_on(async{
            let started=std::time::Instant::now();let deadline=tokio::time::Instant::now()+Duration::from_millis(timeout as u64);
            let cancellation=PROBE_CANCEL.get_or_init(tokio::sync::Notify::new).notified();tokio::pin!(cancellation);cancellation.as_mut().enable();
            let _guard=tokio::select!{_=&mut cancellation=>return Err(io::ErrorKind::Interrupted.into()),guard=tokio::time::timeout_at(deadline,PROBE_LOCK.lock())=>guard.map_err(|_|io::ErrorKind::TimedOut)?};
            if generation!=PROBE_GENERATION.load(Ordering::SeqCst){return Err(io::ErrorKind::Interrupted.into());}
            let existing=SESSION.lock().map_err(|_|io::ErrorKind::Other)?.as_ref().map(|s|(s.outbound.clone(),s.profile.clone()));
            let temporary=existing.is_none();
            if temporary{let p=protect(vm,callback);ior_core::android_network::set_protector(Some(p.clone()));rsteria2::android_socket::set(Some(p));}
            let result=tokio::select!{
                _=&mut cancellation=>Err(io::ErrorKind::Interrupted.into()),
            result=tokio::time::timeout_at(deadline,async{
                    let connector=if let Some((connector,profile))=existing{
                        if serde_json::to_string(&profile).unwrap()==serde_json::to_string(&request.profile).unwrap(){connector}
                        else{if profile.protocol=="amneziawg"&&request.profile.protocol=="amneziawg"&&profile.credential==request.profile.credential&&profile.options.peer_public_key==request.profile.options.peer_public_key{return Err(io::Error::other("AWG_PEER_ALREADY_ACTIVE"));}connector_for(&request).await?}
                    }else{connector_for(&request).await?};
                    http_probe(&connector,&method,&url).await
                })=>result.map_err(|_|io::Error::from(io::ErrorKind::TimedOut)).and_then(|r|r)
            };
            if temporary{ior_core::android_network::set_lan_protector(None);ior_core::android_network::set_protector(None);rsteria2::android_socket::set(None);}
            result.map(|_|started.elapsed().as_millis() as u64)
        })
    })();
    let value=match operation{Ok(ms)=>serde_json::json!({"ok":true,"durationMs":ms}),Err(error)=>{let raw=error.to_string();let code=if raw.len()<=80&&raw.bytes().all(|b|b.is_ascii_uppercase()||b.is_ascii_digit()||b==b'_'){raw}else if error.kind()==io::ErrorKind::TimedOut{"PING_TIMEOUT".into()}else{"PING_HTTPS_UNAVAILABLE".into()};serde_json::json!({"ok":false,"code":code})}};
    env.new_string(value.to_string()).map(|s|s.into_raw()).unwrap_or(std::ptr::null_mut())
}

#[no_mangle]
pub extern "system" fn Java_app_ior_android_NativeCore_configureLan(mut env:JNIEnv,_:JObject,input:JString)->jstring{let result=(||{let text:String=env.get_string(&input).map_err(|_|io::ErrorKind::InvalidInput)?.into();if text.len()>4096{return Err(io::ErrorKind::InvalidInput.into())}let settings=serde_json::from_str(&text).map_err(|_|io::ErrorKind::InvalidInput)?;let mut session=SESSION.lock().map_err(|_|io::ErrorKind::Other)?;let active=session.as_mut().ok_or_else(||io::Error::other("LAN_VPN_REQUIRED"))?;runtime().block_on(ior_core::lan_proxy::configure(&mut active.lan,settings,active.router.clone()))})();answer(&env,result)}
