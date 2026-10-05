//! Native QUIC transport. The local ingress and Windows TUN must be verified
//! independently before this connector is exposed as an available runtime.
use crate::{
    dns_engine,
    ipc_contract::WireProfile,
    proxy_inbound::{ConnectFuture, OutboundConnector, RelayStream},
};
use std::{io, net::SocketAddr, sync::Arc, time::Duration};

pub struct Hysteria2Connector {
    client: Arc<rsteria2::ReconnectableClient>,
}

impl Hysteria2Connector {
    pub async fn from_profile(profile: &WireProfile, bootstrap: SocketAddr) -> io::Result<Self> {
        Self::from_profile_bound(profile, bootstrap, None).await
    }
    pub async fn from_profile_bound(
        profile: &WireProfile,
        bootstrap: SocketAddr,
        network: Option<crate::ipc_contract::WireNetwork>,
    ) -> io::Result<Self> {
        if profile.protocol != "hysteria2"
            || profile.credential.is_empty()
            || profile.server_name.is_empty()
            || profile.port == 0
            || profile.public_key.is_some()
            || profile.short_id.is_some()
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "invalid Hysteria2 profile",
            ));
        }
        let address =
            dns_engine::resolve_server_ip_bound(&profile.server, bootstrap, &network).await?;
        let start = std::time::Instant::now();
        let config = rsteria2::Config {
            server_addr: SocketAddr::new(address, profile.port).to_string(),
            local_address: network
                .as_ref()
                .map(|net| {
                    if address.is_ipv6() {
                        net.physical_address6.as_deref().unwrap_or("")
                    } else {
                        &net.physical_address
                    }
                })
                .map(|ip| {
                    ip.parse().map_err(|_| {
                        io::Error::new(
                            io::ErrorKind::InvalidInput,
                            "HYSTERIA2_PHYSICAL_ADDRESS_INVALID",
                        )
                    })
                })
                .transpose()?,
            interface_index: network.as_ref().and_then(|net| {
                if address.is_ipv6() {
                    net.interface_index6
                } else {
                    Some(net.interface_index)
                }
            }),
            server_name: profile.server_name.clone(),
            auth: profile.credential.clone(),
            insecure: false,
            // Some upstreams defer TCPResponse until the application sends data.
            // Do not deadlock HTTP CONNECT / TLS / WebSocket clients waiting for it.
            // DuplexStream still validates and strips the response on first read.
            fast_open: true,
            ..Default::default()
        };
        // Never include library errors here: they may contain remote addresses
        // or authentication headers. Detailed diagnostics use stage codes.
        let client = tokio::time::timeout(
            Duration::from_secs(15),
            rsteria2::ReconnectableClient::connect_now(config),
        )
        .await
        .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "HYSTERIA2_HANDSHAKE_TIMEOUT"))?
        .map_err(|_| {
            io::Error::new(
                io::ErrorKind::ConnectionRefused,
                "HYSTERIA2_HANDSHAKE_FAILED",
            )
        })?;
        crate::trace::emit(
            serde_json::json!({"stage":"handshake","code":"HYSTERIA2_HANDSHAKE_AUTHENTICATED","durationMs":start.elapsed().as_millis() as u64}),
        );
        Ok(Self {
            client: Arc::new(client),
        })
    }

    pub async fn udp_session(&self) -> io::Result<rsteria2::UdpSession> {
        self.client.udp().await.map_err(|_| {
            io::Error::new(
                io::ErrorKind::ConnectionAborted,
                "HYSTERIA2_UDP_SESSION_FAILED",
            )
        })
    }

    pub async fn close(&self) {
        self.client.close().await;
    }
}

impl OutboundConnector for Hysteria2Connector {
    fn udp(&self, host: String, port: u16) -> crate::udp_relay::UdpFuture {
        let client = self.client.clone();
        Box::pin(async move {
            let destination = if host.contains(':') {
                format!("[{host}]:{port}")
            } else {
                format!("{host}:{port}")
            };
            let session = client.udp().await.map_err(|_| {
                io::Error::new(io::ErrorKind::ConnectionAborted, "HY2_UDP_OPEN_FAILED")
            })?;
            Ok(Box::new(crate::udp_relay::HysteriaDatagram {
                session,
                destination,
            }) as Box<dyn crate::udp_relay::DatagramRelay>)
        })
    }
    fn connect(&self, host: String, port: u16) -> ConnectFuture {
        let client = self.client.clone();
        Box::pin(async move {
            if host.is_empty() || host.contains(['\r', '\n', '\0']) || port == 0 {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "invalid target",
                ));
            }
            let address = if host.contains(':') {
                format!("[{host}]:{port}")
            } else {
                format!("{host}:{port}")
            };
            let stream = client.tcp_connect(&address).await.map_err(|_| {
                io::Error::new(
                    io::ErrorKind::ConnectionAborted,
                    "HYSTERIA2_TCP_CONNECT_FAILED",
                )
            })?;
            Ok(Box::new(stream) as Box<dyn RelayStream>)
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};

    // Explicitly opt in: credentials are read locally, never placed in argv
    // or test output. This is a transport check, not a Windows TUN check.
    #[tokio::test]
    async fn live_hysteria2_https_and_udp() {
        let Ok(path) = std::env::var("IOR_LIVE_STATE_PATH") else {
            return;
        };
        let state: serde_json::Value =
            serde_json::from_slice(&std::fs::read(path).expect("private test state unavailable"))
                .expect("invalid test state");
        let profiles = state
            .get("Profiles")
            .or_else(|| state.get("profiles"))
            .and_then(|v| v.as_array())
            .expect("test profiles unavailable");
        let get = |p: &serde_json::Value, upper: &str, lower: &str| {
            p.get(upper)
                .or_else(|| p.get(lower))
                .cloned()
                .unwrap_or_default()
        };
        let p = profiles
            .iter()
            .find(|p| get(p, "Protocol", "protocol") == 4)
            .expect("Hysteria2 test profile unavailable");
        let text = |u, l| get(p, u, l).as_str().unwrap_or_default().to_owned();
        let profile = WireProfile {
            protocol: "hysteria2".into(),
            server: text("Server", "server"),
            port: get(p, "Port", "port").as_u64().unwrap_or_default() as u16,
            credential: text("Password", "password"),
            server_name: text("ServerName", "serverName"),
            public_key: None,
            short_id: None,
            flow: None,
            fingerprint: None,
            options: Default::default(),
        };
        let connector = Hysteria2Connector::from_profile(&profile, "1.1.1.1:53".parse().unwrap())
            .await
            .expect("HYSTERIA2_LIVE_HANDSHAKE_FAILED");
        let stream = connector
            .connect("example.com".into(), 443)
            .await
            .expect("HYSTERIA2_LIVE_TCP_FAILED");
        let mut roots = tokio_rustls::rustls::RootCertStore::empty();
        roots.extend(webpki_roots::TLS_SERVER_ROOTS.iter().cloned());
        let config = tokio_rustls::rustls::ClientConfig::builder()
            .with_root_certificates(roots)
            .with_no_client_auth();
        let tls = tokio_rustls::TlsConnector::from(Arc::new(config));
        let mut stream = tls
            .connect("example.com".try_into().unwrap(), stream)
            .await
            .expect("HYSTERIA2_LIVE_HTTPS_TLS_FAILED");
        stream
            .write_all(b"GET / HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n")
            .await
            .unwrap();
        let mut header = [0u8; 64];
        let n = tokio::time::timeout(Duration::from_secs(15), stream.read(&mut header))
            .await
            .expect("HYSTERIA2_LIVE_HTTPS_TIMEOUT")
            .expect("HYSTERIA2_LIVE_HTTPS_READ_FAILED");
        assert!(
            header[..n].starts_with(b"HTTP/1.1 200"),
            "HYSTERIA2_LIVE_HTTPS_STATUS_FAILED"
        );
        let mut udp = connector
            .udp_session()
            .await
            .expect("HYSTERIA2_LIVE_UDP_FAILED");
        let query = b"\x49\x4f\x01\x00\x00\x01\x00\x00\x00\x00\x00\x00\x07example\x03com\x00\x00\x01\x00\x01";
        udp.send(query, "1.1.1.1:53")
            .expect("HYSTERIA2_LIVE_DNS_SEND_FAILED");
        let (answer, _) = tokio::time::timeout(Duration::from_secs(10), udp.recv())
            .await
            .expect("HYSTERIA2_LIVE_DNS_TIMEOUT")
            .expect("HYSTERIA2_LIVE_DNS_RECEIVE_FAILED");
        assert!(
            answer.len() >= 12 && answer[..2] == query[..2] && answer[2] & 0x80 != 0,
            "HYSTERIA2_LIVE_DNS_INVALID_RESPONSE"
        );
        connector.close().await;
    }
}
