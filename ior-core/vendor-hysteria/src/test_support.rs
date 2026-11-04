use std::sync::Arc;

pub async fn pair() -> (quinn::Endpoint, quinn::Endpoint, quinn::Connection, quinn::Connection) {
    pair_with_datagram_buffer(None).await
}
pub async fn pair_with_datagram_buffer(send_buffer:Option<usize>) -> (quinn::Endpoint, quinn::Endpoint, quinn::Connection, quinn::Connection) {
    let _=rustls::crypto::ring::default_provider().install_default();
    let cert=rcgen::generate_simple_self_signed(vec!["localhost".into()]).unwrap();
    let der=cert.cert.der().clone();
    let key=rustls::pki_types::PrivatePkcs8KeyDer::from(cert.signing_key.serialize_der());
    let config=quinn::ServerConfig::with_single_cert(vec![der.clone()],key.into()).unwrap();
    let server=quinn::Endpoint::server(config,"127.0.0.1:0".parse().unwrap()).unwrap();
    let mut roots=rustls::RootCertStore::empty();roots.add(der).unwrap();
    let mut client=quinn::Endpoint::client("127.0.0.1:0".parse().unwrap()).unwrap();
    let mut client_config=quinn::ClientConfig::with_root_certificates(Arc::new(roots)).unwrap();
    if let Some(size)=send_buffer{let mut transport=quinn::TransportConfig::default();transport.datagram_send_buffer_size(size);client_config.transport_config(Arc::new(transport));}
    client.set_default_client_config(client_config);
    let connect=client.connect(server.local_addr().unwrap(),"localhost").unwrap();
    let (local,remote)=tokio::time::timeout(std::time::Duration::from_secs(5),async {
        tokio::join!(connect,async {server.accept().await.unwrap().await.unwrap()})
    }).await.expect("local QUIC handshake timed out");
    (client,server,local.unwrap(),remote)
}
