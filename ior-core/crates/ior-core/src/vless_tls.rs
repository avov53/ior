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

pub struct VlessTlsConnector {
    server: String,
    port: u16,
    server_name: ServerName<'static>,
    user_id: String,
    bootstrap: SocketAddr,
    tls: TlsConnector,
}

impl VlessTlsConnector {
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
        let tls = TlsConnector::from(Arc::new(
            ClientConfig::builder()
                .with_root_certificates(roots)
                .with_no_client_auth(),
        ));
        Ok(Self {
            server: profile.server.clone(),
            port: profile.port,
            server_name,
            user_id: profile.credential.clone(),
            bootstrap,
            tls,
        })
    }
}

impl OutboundConnector for VlessTlsConnector {
    fn connect(&self, host: String, port: u16) -> ConnectFuture {
        let server = self.server.clone();
        let server_port = self.port;
        let server_name = self.server_name.clone();
        let user_id = self.user_id.clone();
        let bootstrap = self.bootstrap;
        let tls = self.tls.clone();
        Box::pin(async move {
            // The destination is framed inside TLS. No plaintext VLESS request is sent.
            let request = vless_codec::encode_tcp_request(&user_id, &host, port)?;
            let address = dns_engine::resolve_server_ipv4(&server, bootstrap).await?;
            let socket = TcpStream::connect((address, server_port)).await?;
            socket.set_nodelay(true)?;
            let mut remote = tls.connect(server_name, socket).await.map_err(|_| {
                io::Error::new(io::ErrorKind::ConnectionAborted, "TLS handshake failed")
            })?;
            remote.write_all(&request).await?;
            let (client, bridge) = tokio_io::duplex(64 * 1024);
            tokio::spawn(async move {
                let (mut bridge_read, mut bridge_write) = tokio_io::split(bridge);
                let (mut remote_read, mut remote_write) = tokio_io::split(remote);
                let upstream = async {
                    tokio_io::copy(&mut bridge_read, &mut remote_write).await?;
                    remote_write.shutdown().await
                };
                let downstream = async {
                    vless_codec::read_response_header(&mut remote_read).await?;
                    tokio_io::copy(&mut remote_read, &mut bridge_write).await?;
                    bridge_write.shutdown().await
                };
                let _: io::Result<((), ())> = tokio::try_join!(upstream, downstream);
            });
            Ok(Box::new(client) as Box<dyn RelayStream>)
        })
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
