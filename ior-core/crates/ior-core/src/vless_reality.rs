use crate::{
    dns_engine,
    ipc_contract::WireProfile,
    proxy_inbound::{ConnectFuture, OutboundConnector, RelayStream},
    vless_codec,
};
use reality_rustls::Connection;
use std::{io, net::SocketAddr, sync::Arc, time::Duration};
use tokio::io::AsyncWriteExt;

pub struct RealityConnector {
    profile: WireProfile,
    bootstrap: SocketAddr,
    config: Arc<reality_rustls::ClientConfig>,
    network: Option<crate::ipc_contract::WireNetwork>,
}
impl RealityConnector {
    pub fn new(profile: &WireProfile, bootstrap: SocketAddr) -> io::Result<Self> {
        if profile.protocol != "vless_reality" {
            return Err(io::ErrorKind::InvalidInput.into());
        }
        uuid::Uuid::parse_str(&profile.credential).map_err(|_| io::ErrorKind::InvalidInput)?;
        if !matches!(
            profile.flow.as_deref(),
            None | Some("") | Some("xtls-rprx-vision") | Some("xtls-rprx-vision-udp443")
        ) {
            return Err(io::Error::new(
                io::ErrorKind::Unsupported,
                "REALITY_FLOW_UNSUPPORTED",
            ));
        }
        let mut roots = reality_rustls::RootCertStore::empty();
        roots.extend(webpki_roots::TLS_SERVER_ROOTS.iter().cloned());
        let provider = if profile.fingerprint.as_deref() == Some("safari") {
            reality_provider::reality::default_x25519_tls13_reality_provider()
        } else {
            reality_provider::DEFAULT_TLS13_PROVIDER.clone()
        };
        let mut config = reality_rustls::ClientConfig::builder(Arc::new(provider))
            .with_root_certificates(roots)
            .with_no_client_auth()
            .map_err(|_| {
                io::Error::new(io::ErrorKind::InvalidInput, "REALITY_CLIENT_BUILD_FAILED")
            })?;
        reality_provider::reality::RealitySessionIdConfig::from_xray_fields(
            [26, 9, 9],
            profile.short_id.as_deref().unwrap_or(""),
            profile.public_key.as_deref().unwrap_or(""),
        )
        .and_then(|parameters| parameters.install_into(&mut config))
        .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "REALITY_PARAMETERS_INVALID"))?;
        config.resumption = reality_rustls::client::Resumption::disabled();
        let verifier = config.verifier().clone();
        config.dangerous().set_certificate_verifier(Arc::new(
            reality_provider::reality::RealityServerVerifier::new(Arc::new(RejectCamouflage {
                delegate: verifier,
            })),
        ));
        config.client_hello_profile = match profile.fingerprint.as_deref() {
            None | Some("") | Some("chrome") => reality_rustls::client::ClientHelloProfile::Chrome,
            Some("firefox") => reality_rustls::client::ClientHelloProfile::Firefox,
            Some("safari") => reality_rustls::client::ClientHelloProfile::Safari,
            _ => {
                return Err(io::Error::new(
                    io::ErrorKind::Unsupported,
                    "REALITY_FINGERPRINT_UNSUPPORTED",
                ))
            }
        };
        Ok(Self {
            profile: profile.clone(),
            bootstrap,
            config: Arc::new(config),
            network: None,
        })
    }
}

#[derive(Debug)]
struct RejectCamouflage {
    delegate: Arc<dyn reality_rustls::client::danger::ServerVerifier>,
}
impl reality_rustls::client::danger::ServerVerifier for RejectCamouflage {
    fn verify_identity(
        &self,
        _: &reality_rustls::client::danger::ServerIdentity<'_>,
    ) -> Result<reality_rustls::client::danger::PeerVerified, reality_rustls::Error> {
        Err(reality_rustls::Error::General(
            "REALITY_AUTHENTICATION_REJECTED".into(),
        ))
    }
    fn verify_tls12_signature(
        &self,
        input: &reality_rustls::client::danger::SignatureVerificationInput<'_>,
    ) -> Result<reality_rustls::client::danger::HandshakeSignatureValid, reality_rustls::Error>
    {
        self.delegate.verify_tls12_signature(input)
    }
    fn verify_tls13_signature(
        &self,
        input: &reality_rustls::client::danger::SignatureVerificationInput<'_>,
    ) -> Result<reality_rustls::client::danger::HandshakeSignatureValid, reality_rustls::Error>
    {
        self.delegate.verify_tls13_signature(input)
    }
    fn supported_verify_schemes(&self) -> Vec<reality_rustls::crypto::SignatureScheme> {
        self.delegate.supported_verify_schemes()
    }
    fn request_ocsp_response(&self) -> bool {
        self.delegate.request_ocsp_response()
    }
    fn hash_config(&self, h: &mut dyn std::hash::Hasher) {
        self.delegate.hash_config(h);
        h.write(b"ior-require-reality-auth");
    }
}
impl OutboundConnector for RealityConnector {
    fn connect(&self, host: String, port: u16) -> ConnectFuture {
        self.open(host, port, false)
    }
    fn udp(&self, host: String, port: u16) -> crate::udp_relay::UdpFuture {
        let forbidden = port == 443 && self.profile.flow.as_deref() == Some("xtls-rprx-vision");
        let connection = self.open("v1.mux.cool".into(), 666, true);
        Box::pin(async move {
            if forbidden {
                return Err(io::Error::new(
                    io::ErrorKind::PermissionDenied,
                    "VISION_UDP443_BLOCKED",
                ));
            }
            let stream = connection.await?;
            Ok(
                Box::new(crate::xudp::XudpDatagram::new(stream, host, port)?)
                    as Box<dyn crate::udp_relay::DatagramRelay>,
            )
        })
    }
}
impl RealityConnector {
    pub fn with_network(mut self, network: Option<crate::ipc_contract::WireNetwork>) -> Self {
        self.network = network;
        self
    }
    fn open(&self, host: String, port: u16, mux: bool) -> ConnectFuture {
        let profile = self.profile.clone();
        let config = self.config.clone();
        let bootstrap = self.bootstrap;
        let network = self.network.clone();
        Box::pin(async move {
            let mut header = vless_codec::encode_tcp_request(&profile.credential, &host, port)?;
            if mux {
                header[18] = 3;
                header.truncate(19);
            }
            let vision = profile
                .flow
                .as_deref()
                .is_some_and(|value| !value.is_empty());
            if vision {
                let flow = b"xtls-rprx-vision";
                let mut addons = vec![0x0a, flow.len() as u8];
                addons.extend(flow);
                header[17] = addons.len() as u8;
                header.splice(18..18, addons);
            }
            let ip = dns_engine::resolve_server_ipv4(&profile.server, bootstrap).await?;
            let bound = crate::native_router::bound_socket(false, &network)?;
            let socket = tokio::net::TcpSocket::from_std_stream(bound.into());
            let endpoint = SocketAddr::new(ip.into(), profile.port);
            let guard = crate::egress_registry::register(socket.local_addr()?, endpoint, false);
            let socket = socket.connect(endpoint).await?;
            socket.set_nodelay(true)?;
            let socket = socket.into_std()?;
            crate::trace::code("handshake", "REALITY_TCP_CONNECTED");
            let scheduled = std::time::Instant::now();
            let trace_flow = crate::trace::current();
            let id = *uuid::Uuid::parse_str(&profile.credential)
                .unwrap()
                .as_bytes();
            let carrier = tokio::task::spawn_blocking(move || -> io::Result<_> {
                use std::io::Write;
                crate::trace::emit_with_flow(serde_json::json!({"stage":"handshake","code":"REALITY_WORKER_READY","durationMs":scheduled.elapsed().as_millis() as u64}), trace_flow.as_ref());
                socket.set_nonblocking(false)?;
                socket.set_read_timeout(Some(Duration::from_secs(10)))?;
                socket.set_write_timeout(Some(Duration::from_secs(10)))?;
                let sni = profile
                    .server_name
                    .try_into()
                    .map_err(|_| io::ErrorKind::InvalidInput)?;
                let building = std::time::Instant::now();
                let mut conn = config.connect(sni).build().map_err(|_| {
                    io::Error::new(
                        io::ErrorKind::ConnectionAborted,
                        "REALITY_CLIENT_BUILD_FAILED",
                    )
                })?;
                crate::trace::emit_with_flow(serde_json::json!({"stage":"handshake","code":"REALITY_CLIENT_HELLO_READY","durationMs":building.elapsed().as_millis() as u64}), trace_flow.as_ref());
                let exchanging = std::time::Instant::now();
                let mut socket = crate::vision_io::RecordSocket::new_handshake(socket);
                while conn.is_handshaking() {
                    reality_util::complete_io(&mut socket, &mut conn).map_err(|error| {
                        let code = handshake_failure_code(&error.to_string());
                        crate::trace::emit_with_flow(
                            serde_json::json!({"stage":"handshake","code":code}),
                            trace_flow.as_ref(),
                        );
                        io::Error::new(io::ErrorKind::ConnectionAborted, code)
                    })?;
                }
                let (reads, read_wait_us) = socket.handshake_metrics();
                crate::trace::emit_with_flow(
                    serde_json::json!({"stage":"handshake","code":"REALITY_TLS_COMPLETE","durationMs":exchanging.elapsed().as_millis() as u64,"readCalls":reads,"readWaitUs":read_wait_us}),
                    trace_flow.as_ref(),
                );
                socket.finish_handshake();
                let mut tls = reality_util::StreamOwned::new(conn, socket);
                tls.write_all(&header)?;
                tls.flush()?;
                Ok(crate::vision_io::VlessCarrier::new(
                    tls,
                    if vision { Some(id) } else { None },
                ))
            })
            .await
            .map_err(io::Error::other)??;
            let mut bridge = crate::reality_bridge::into_async_vless(carrier)?;
            bridge.flush().await?;
            Ok(
                Box::new(crate::egress_registry::RegisteredStream::new(bridge, guard))
                    as Box<dyn RelayStream>,
            )
        })
    }
}

fn handshake_failure_code(message: &str) -> &'static str {
    if message.contains("REALITY_AUTHENTICATION_REJECTED") {
        return "REALITY_CAMOUFLAGE_CERTIFICATE";
    }
    if message.contains("REALITY certificate signature mismatch") {
        return "REALITY_SIGNATURE_MISMATCH";
    }
    if message.contains("NotValidForName") || message.contains("not valid for name") {
        return "REALITY_CERTIFICATE_NAME_MISMATCH";
    }
    if message.contains("InvalidKeyShare") || message.contains("key share") {
        return "REALITY_KEY_SHARE_FAILED";
    }
    if message.contains("Unsupported") || message.contains("unsupported") {
        return "REALITY_TLS_UNSUPPORTED";
    }
    if message.contains("BadSignature") || message.contains("signature") {
        return "REALITY_TLS_SIGNATURE_FAILED";
    }
    if message.contains("Alert") || message.contains("alert") {
        return "REALITY_TLS_ALERT";
    }
    if message.contains("timed out") || message.contains("os error 10060") {
        return "REALITY_HANDSHAKE_TIMEOUT";
    }
    "REALITY_HANDSHAKE_FAILED"
}

#[cfg(test)]
mod authentication_tests {
    use super::*;
    #[test]
    fn signatures_require_the_matching_auth_key_and_unmodified_signature() {
        let key = [0x11; 32];
        let wrong = [0x12; 32];
        let public = [0x44; 32];
        let mut signature = reality_provider::reality::reality_certificate_signature(&key, &public);
        assert!(
            reality_provider::reality::verify_reality_certificate_signature(
                &key, &public, &signature
            )
        );
        assert!(
            !reality_provider::reality::verify_reality_certificate_signature(
                &wrong, &public, &signature
            )
        );
        signature[0] ^= 1;
        assert!(
            !reality_provider::reality::verify_reality_certificate_signature(
                &key, &public, &signature
            )
        );
    }
    #[test]
    fn diagnostic_codes_never_echo_sensitive_error_text() {
        assert_eq!(
            handshake_failure_code("secret url token arbitrary"),
            "REALITY_HANDSHAKE_FAILED"
        );
        assert_eq!(
            handshake_failure_code("REALITY_AUTHENTICATION_REJECTED"),
            "REALITY_CAMOUFLAGE_CERTIFICATE"
        );
        assert_eq!(
            handshake_failure_code("REALITY certificate signature mismatch"),
            "REALITY_SIGNATURE_MISMATCH"
        );
    }
}
