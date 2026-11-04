use std::{io,sync::Arc,time::Duration};
use bytes::{Buf,Bytes};
use crate::udp::UdpSessionManager;

#[tokio::test(flavor="multi_thread",worker_threads=4)]
async fn http3_upload_and_small_request_through_waiting_udp(){
    let(_outer_client,_outer_server,local,remote)=crate::test_support::pair_with_datagram_buffer(Some(8192)).await;
    let cert=rcgen::generate_simple_self_signed(vec!["localhost".into()]).unwrap();let der=cert.cert.der().clone();
    let key=rustls::pki_types::PrivatePkcs8KeyDer::from(cert.signing_key.serialize_der());
    let mut tls=rustls::ServerConfig::builder().with_no_client_auth().with_single_cert(vec![der.clone()],key.into()).unwrap();tls.alpn_protocols=vec![b"h3".to_vec()];
    let server=quinn::Endpoint::server(quinn::ServerConfig::with_crypto(Arc::new(quinn::crypto::rustls::QuicServerConfig::try_from(tls).unwrap())),"127.0.0.1:0".parse().unwrap()).unwrap();
    let target=server.local_addr().unwrap();
    let mut roots=rustls::RootCertStore::empty();roots.add(der).unwrap();
    let mut tls=rustls::ClientConfig::builder().with_root_certificates(roots).with_no_client_auth();tls.alpn_protocols=vec![b"h3".to_vec()];
    let mut client=quinn::Endpoint::client("127.0.0.1:0".parse().unwrap()).unwrap();client.set_default_client_config(quinn::ClientConfig::new(Arc::new(quinn::crypto::rustls::QuicClientConfig::try_from(tls).unwrap())));
    let manager=UdpSessionManager::new(local);let mut session=manager.new_session();let outgoing=session.sender();
    let peer_manager=UdpSessionManager::new(remote);let mut peer_session=peer_manager.new_session();let replies=peer_session.sender();assert_eq!(session.id(),peer_session.id());
    let front=Arc::new(tokio::net::UdpSocket::bind("127.0.0.1:0").await.unwrap());let peer=Arc::new(std::sync::Mutex::new(None));
    let backend=Arc::new(tokio::net::UdpSocket::bind("127.0.0.1:0").await.unwrap());
    let mut tasks=tokio::task::JoinSet::new();
    let(f,p)=(front.clone(),peer.clone());tasks.spawn(async move{let mut data=vec![0;65535];loop{let(n,address)=f.recv_from(&mut data).await?;*p.lock().unwrap()=Some(address);outgoing.send(&data[..n],&target.to_string()).await.map_err(io::Error::other)?;}#[allow(unreachable_code)]Ok::<(),io::Error>(())});
    let(f,p)=(front.clone(),peer.clone());tasks.spawn(async move{loop{let(data,_)=session.recv().await.map_err(io::Error::other)?;let destination=*p.lock().unwrap();if let Some(destination)=destination{f.send_to(&data,destination).await?;}}#[allow(unreachable_code)]Ok::<(),io::Error>(())});
    let b=backend.clone();tasks.spawn(async move{loop{let(data,destination)=peer_session.recv().await.map_err(io::Error::other)?;b.send_to(&data,&destination).await?;}#[allow(unreachable_code)]Ok::<(),io::Error>(())});
    let b=backend.clone();tasks.spawn(async move{let mut data=vec![0;65535];loop{let(n,address)=b.recv_from(&mut data).await?;replies.send(&data[..n],&address.to_string()).await.map_err(io::Error::other)?;}#[allow(unreachable_code)]Ok::<(),io::Error>(())});
    let server_task=tokio::spawn(async move{
        let connection=server.accept().await.unwrap().await.unwrap();let mut h3=h3::server::Connection::new(h3_quinn::Connection::new(connection)).await.unwrap();let mut requests=tokio::task::JoinSet::new();
        for _ in 0..2{let request=h3.accept().await.unwrap().unwrap();let(request,mut stream)=request.resolve_request().await.unwrap();requests.spawn(async move{
            if request.uri().path()=="/upload"{let mut count=0;while let Some(mut data)=stream.recv_data().await.unwrap(){count+=data.remaining();while data.has_remaining(){let n=data.chunk().len();assert!(data.chunk().iter().all(|b|*b==0x5a));data.advance(n);}}assert_eq!(count,8*1024*1024);}
            stream.send_response(http::Response::builder().status(200).body(()).unwrap()).await.unwrap();stream.send_data(Bytes::from_static(b"ok")).await.unwrap();stream.finish().await.unwrap();
        });}
        while let Some(result)=requests.join_next().await{result.unwrap();}tokio::time::sleep(Duration::from_millis(200)).await;
    });
    tokio::time::timeout(Duration::from_secs(20),async{
        let connection=client.connect(front.local_addr().unwrap(),"localhost").unwrap().await.unwrap();let(mut driver,sender)=h3::client::new(h3_quinn::Connection::new(connection)).await.unwrap();
        let driver_task=tokio::spawn(async move{let _=std::future::poll_fn(|cx|driver.poll_close(cx)).await;});
        let(mut bulk,mut small)=(sender.clone(),sender.clone());
        let upload=async move{let mut request=bulk.send_request(http::Request::builder().method("POST").uri("https://localhost/upload").body(()).unwrap()).await.unwrap();let chunk=Bytes::from(vec![0x5a;32768]);for _ in 0..256{request.send_data(chunk.clone()).await.unwrap();}request.finish().await.unwrap();assert_eq!(request.recv_response().await.unwrap().status(),200);while let Some(data)=request.recv_data().await.unwrap(){assert_eq!(data.chunk(),b"ok");}};
        let little=async move{let mut request=small.send_request(http::Request::builder().uri("https://localhost/small").body(()).unwrap()).await.unwrap();request.finish().await.unwrap();assert_eq!(request.recv_response().await.unwrap().status(),200);while let Some(data)=request.recv_data().await.unwrap(){assert_eq!(data.chunk(),b"ok");}};
        tokio::join!(upload,little);server_task.await.unwrap();driver_task.abort();
    }).await.expect("HTTP/3 over Hysteria UDP stalled");
    client.close(0u32.into(),b"done");tasks.abort_all();
    assert_eq!(manager.metrics().3,0);assert_eq!(peer_manager.metrics().3,0);
}
