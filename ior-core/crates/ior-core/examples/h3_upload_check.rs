//! Isolated public-file upload through an existing SOCKS5 listener. No TUN,
//! profile credentials, cookies, system routes, or user browser access.
use std::{io,net::{IpAddr,SocketAddr},sync::{Arc,atomic::{AtomicU64,AtomicUsize,Ordering}},time::{Duration,Instant}};
use tokio::{io::{AsyncReadExt,AsyncWriteExt},net::{TcpStream,UdpSocket}};
use bytes::{Buf,Bytes};
use tokio_rustls::rustls;
const SIZE:usize=8*1024*1024;
async fn run(port:u16,target:IpAddr,stage:Arc<AtomicUsize>,sent:Arc<AtomicU64>,received:Arc<AtomicU64>)->Result<serde_json::Value,Box<dyn std::error::Error>>{
    stage.store(1,Ordering::Relaxed);
    let mut control=TcpStream::connect(("127.0.0.1",port)).await?;
    control.write_all(&[5,1,0]).await?;let mut greeting=[0;2];control.read_exact(&mut greeting).await?;if greeting!=[5,0]{return Err(io::Error::from(io::ErrorKind::PermissionDenied).into());}
    let back=Arc::new(UdpSocket::bind("127.0.0.1:0").await?);
    let address=back.local_addr()?;let mut request=vec![5,3,0];request.extend(ior_core::udp_relay::encode_address("127.0.0.1",address.port())?);control.write_all(&request).await?;
    let mut header=[0;4];control.read_exact(&mut header).await?;if header[0]!=5||header[1]!=0{return Err(io::Error::from(io::ErrorKind::ConnectionRefused).into());}
    let relay_ip=match header[3]{1=>{let mut ip=[0;4];control.read_exact(&mut ip).await?;IpAddr::from(ip)},4=>{let mut ip=[0;16];control.read_exact(&mut ip).await?;IpAddr::from(ip)},_=>return Err(io::Error::from(io::ErrorKind::InvalidData).into())};
    let relay_port=control.read_u16().await?;let relay=SocketAddr::new(if relay_ip.is_unspecified(){"127.0.0.1".parse()?}else{relay_ip},relay_port);
    back.connect(relay).await?;
    let front=Arc::new(UdpSocket::bind("127.0.0.1:0").await?);let front_address=front.local_addr()?;
    let peer=Arc::new(std::sync::Mutex::new(None));let mut tasks=tokio::task::JoinSet::new();
    let(mut prefix,f,b,p,s)=(vec![0,0,0],front.clone(),back.clone(),peer.clone(),sent.clone());prefix.extend(ior_core::udp_relay::encode_address(&target.to_string(),443)?);
    tasks.spawn(async move{let mut buffer=vec![0;65535];loop{let(n,source)=f.recv_from(&mut buffer).await?;*p.lock().unwrap()=Some(source);let mut packet=prefix.clone();packet.extend_from_slice(&buffer[..n]);b.send(&packet).await?;s.fetch_add(n as u64,Ordering::Relaxed);}#[allow(unreachable_code)]Ok::<(),io::Error>(())});
    let(f,b,p,r)=(front.clone(),back.clone(),peer.clone(),received.clone());
    tasks.spawn(async move{let mut buffer=vec![0;65535];loop{let n=b.recv(&mut buffer).await?;let(_,_,offset)=ior_core::udp_relay::decode_packet(&buffer[..n])?;let destination=*p.lock().unwrap();if let Some(destination)=destination{f.send_to(&buffer[offset..n],destination).await?;r.fetch_add((n-offset)as u64,Ordering::Relaxed);}}#[allow(unreachable_code)]Ok::<(),io::Error>(())});
    let _=rustls::crypto::ring::default_provider().install_default();
    let roots=rustls::RootCertStore::from_iter(webpki_roots::TLS_SERVER_ROOTS.iter().cloned());
    let mut tls=rustls::ClientConfig::builder().with_root_certificates(roots).with_no_client_auth();tls.alpn_protocols=vec![b"h3".to_vec()];
    let mut endpoint=quinn::Endpoint::client("127.0.0.1:0".parse()?)?;
    endpoint.set_default_client_config(quinn::ClientConfig::new(Arc::new(quinn::crypto::rustls::QuicClientConfig::try_from(tls)?)));
    stage.store(2,Ordering::Relaxed);let connection=endpoint.connect(front_address,"speed.cloudflare.com")?.await?;
    let(mut driver,mut sender)=h3::client::new(h3_quinn::Connection::new(connection.clone())).await?;
    let driver_task=tokio::spawn(async move{let _=futures::future::poll_fn(|cx|driver.poll_close(cx)).await;});
    stage.store(3,Ordering::Relaxed);
    let mut warm=sender.send_request(http::Request::builder().uri("https://speed.cloudflare.com/__down?bytes=1024").body(())?).await?;warm.finish().await?;if warm.recv_response().await?.status()!=200{return Err(io::Error::from(io::ErrorKind::Other).into());}while warm.recv_data().await?.is_some(){}
    let mut samples=Vec::new();
    for repetition in 1..=3{
        stage.store(4,Ordering::Relaxed);let(mut bulk,mut small)=(sender.clone(),sender.clone());
        let upload=async move{
            let timer=Instant::now();let mut request=bulk.send_request(http::Request::builder().method("POST").uri("https://speed.cloudflare.com/__up").header("content-length",SIZE).body(())?).await?;
            for _ in 0..SIZE/32768{request.send_data(Bytes::from(vec![0x5a;32768])).await?;}request.finish().await?;
            let status=request.recv_response().await?.status().as_u16();let mut response_bytes=0;while let Some(data)=request.recv_data().await?{response_bytes+=data.remaining();if response_bytes>65536{break;}}
            Ok::<_,Box<dyn std::error::Error>>((timer.elapsed().as_millis()as u64,status))
        };
        let little=async move{tokio::time::sleep(Duration::from_millis(200)).await;let timer=Instant::now();let mut request=small.send_request(http::Request::builder().uri("https://speed.cloudflare.com/__down?bytes=1024").body(())?).await?;request.finish().await?;let status=request.recv_response().await?.status();if status!=200{return Err(io::Error::from(io::ErrorKind::Other).into());}while request.recv_data().await?.is_some(){}Ok::<_,Box<dyn std::error::Error>>(timer.elapsed().as_millis()as u64)};
        let(upload,little)=tokio::join!(upload,little);let(elapsed,status)=upload?;if status!=200{return Err(io::Error::from(io::ErrorKind::Other).into());}
        samples.push(serde_json::json!({"repetition":repetition,"bytes":SIZE,"elapsedMs":elapsed,"smallRequestMs":little?,"status":status,"protocol":"HTTP/3"}));
    }
    let stats=connection.stats();endpoint.close(0u32.into(),b"done");driver_task.abort();tasks.abort_all();drop(control);
    Ok(serde_json::json!({"ok":true,"samples":samples,"lostPackets":stats.path.lost_packets,"sentPackets":stats.path.sent_packets,"forwardedBytes":sent.load(Ordering::Relaxed),"receivedBytes":received.load(Ordering::Relaxed)}))
}
#[tokio::main]
async fn main(){
    let stage=Arc::new(AtomicUsize::new(0));let sent=Arc::new(AtomicU64::new(0));let received=Arc::new(AtomicU64::new(0));
    let args:Vec<_>=std::env::args().collect();let values=args.get(1).and_then(|s|s.parse::<u16>().ok()).zip(args.get(2).and_then(|s|s.parse::<IpAddr>().ok()));
    let mut code="HTTP3_UPLOAD_CHECK_FAILED".to_owned();
    let result=if let Some((port,ip))=values{match tokio::time::timeout(Duration::from_secs(90),run(port,ip,stage.clone(),sent.clone(),received.clone())).await{
        Ok(Ok(result))=>Some(result),Ok(Err(error))=>{if let Some(error)=error.downcast_ref::<quinn::ConnectionError>(){code=match error{quinn::ConnectionError::TransportError(e)=>format!("QUIC_TRANSPORT_{}",e.code),quinn::ConnectionError::ConnectionClosed(e)=>format!("QUIC_CLOSED_{}",e.error_code),quinn::ConnectionError::ApplicationClosed(e)=>format!("QUIC_APPLICATION_{}",e.error_code),quinn::ConnectionError::TimedOut=>"QUIC_TIMEOUT".into(),_=>"QUIC_CONNECTION_FAILED".into()};}None},Err(_)=>{code="HTTP3_CHECK_TIMEOUT".into();None}
    }}else{None};
    println!("{}",result.unwrap_or_else(||serde_json::json!({"ok":false,"code":code,"stage":stage.load(Ordering::Relaxed),"forwardedBytes":sent.load(Ordering::Relaxed),"receivedBytes":received.load(Ordering::Relaxed)})));
}
