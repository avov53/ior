use crate::{
    dns_engine,
    ipc_contract::WireProfile,
    proxy_inbound::{ConnectFuture, OutboundConnector, RelayStream},
    vless_codec,
};
use reality_rustls::Connection;
use std::{io, net::SocketAddr, sync::Arc, time::Duration};
use tokio::io::AsyncWriteExt;

#[derive(Clone)]
pub struct RealityConnector {
    profile: WireProfile,
    bootstrap: SocketAddr,
    config: Arc<reality_rustls::ClientConfig>,
    network: Option<crate::ipc_contract::WireNetwork>,
    transports:Arc<crate::vless_transport::Runtime>,
    server_dns: Arc<dns_engine::ServerIpv4Resolver>,
}
impl RealityConnector {
    pub fn secure_transport(&self)->ConnectFuture{self.carrier(String::new(),0,false,true)}
    pub fn new(profile: &WireProfile, bootstrap: SocketAddr) -> io::Result<Self> {
        crate::vless_transport::validate(profile)?;
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
        config.alpn_protocols=profile.options.alpn.as_ref().map(|values|values.iter().map(|v|v.as_bytes().to_vec().into()).collect()).unwrap_or_else(||if matches!(crate::vless_transport::kind(profile),"grpc"|"xhttp"){vec![b"h2".to_vec().into()]}else{vec![]});
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
            transports:Arc::new(Default::default()),
            server_dns: Arc::new(Default::default()),
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
    fn named_tun_destination(&self)->bool {true}
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
        self.server_dns = Arc::new(Default::default());
        self
    }
    fn open(&self, host: String, port: u16, mux: bool) -> ConnectFuture {
        if crate::vless_transport::kind(&self.profile)=="raw"{return self.carrier(host,port,mux,false);}
        let profile=self.profile.clone();let bootstrap=self.bootstrap;let network=self.network.clone();let transports=self.transports.clone();
        Box::pin(async move{
            let mut request=vless_codec::encode_tcp_request(&profile.credential,&host,port)?;
            if mux{request[18]=3;request.truncate(19);}
            let dial:crate::vless_transport::Dial=Arc::new(move|p|{
                if p.protocol=="vless_reality"{match Self::new(&p,bootstrap){Ok(connector)=>connector.with_network(network.clone()).carrier(String::new(),0,false,true),Err(error)=>Box::pin(async{Err(error)})}}
                else{crate::vless_tls::VlessTlsConnector::secure_profile(p,bootstrap,network.clone())}
            });
            let remote=transports.connect(profile,dial).await?;
            Ok(crate::vless_transport::vless_stream(remote,request))
        })
    }
    fn carrier(&self, host:String,port:u16,mux:bool,transport_only:bool)->ConnectFuture {
        let profile = self.profile.clone();
        let config = self.config.clone();
        let bootstrap = self.bootstrap;
        let network = self.network.clone();
        let server_dns = self.server_dns.clone();
        Box::pin(async move {
            let mut header = if transport_only{Vec::new()}else{vless_codec::encode_tcp_request(&profile.credential, &host, port)?};
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
            let ip = server_dns.resolve(&profile.server, bootstrap, &network).await?;
            let bound = crate::native_router::bound_socket(false, &network)?;
            let endpoint = SocketAddr::new(ip.into(), profile.port);
            let scheduled = std::time::Instant::now();
            let trace_flow = crate::trace::current();
            let id = *uuid::Uuid::parse_str(&profile.credential)
                .unwrap()
                .as_bytes();
            let (carrier,guard) = tokio::task::spawn_blocking(move || -> io::Result<_> {
                use std::io::Write;
                crate::trace::emit_with_flow(serde_json::json!({"stage":"handshake","code":"REALITY_WORKER_READY","durationMs":scheduled.elapsed().as_millis() as u64}), trace_flow.as_ref());
                // The blocking TLS worker owns TCP from its first byte. Do not
                // transfer a registered IOCP socket with pending readiness reads
                // from Tokio into a blocking reader during the server handshake.
                bound.set_nonblocking(false)?;
                bound.connect_timeout(&endpoint.into(),Duration::from_secs(10))?;
                let socket:std::net::TcpStream=bound.into();
                socket.set_nodelay(true)?;
                let guard=crate::egress_registry::register(socket.local_addr()?,endpoint,false);
                crate::trace::emit_with_flow(serde_json::json!({"stage":"handshake","code":"REALITY_TCP_CONNECTED"}),trace_flow.as_ref());
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
                // Xray's authenticated REALITY carrier can omit ALPN; its gRPC/XHTTP
                // client then speaks HTTP/2 with prior knowledge over that carrier.
                if transport_only&&conn.alpn_protocol().is_some_and(|p|p.as_ref()!=b"h2"){
                    crate::trace::emit(serde_json::json!({"stage":"handshake","code":"REALITY_ALPN_UNEXPECTED","reason":"not_h2"}));
                    return Err(io::Error::new(io::ErrorKind::Unsupported,"VLESS_HTTP2_ALPN_REQUIRED"));
                }
                if transport_only&&conn.alpn_protocol().is_none(){crate::trace::code("handshake","REALITY_HTTP2_PRIOR_KNOWLEDGE");}
                let (reads, read_wait_us) = socket.handshake_metrics();
                crate::trace::emit_with_flow(
                    serde_json::json!({"stage":"handshake","code":"REALITY_TLS_COMPLETE","durationMs":exchanging.elapsed().as_millis() as u64,"readCalls":reads,"readWaitUs":read_wait_us}),
                    trace_flow.as_ref(),
                );
                socket.finish_handshake();
                let mut tls = reality_util::StreamOwned::new(conn, socket);
                let carrier=if transport_only {crate::vision_io::VlessCarrier::transport(tls)}
                else{tls.write_all(&header)?;tls.flush()?;crate::vision_io::VlessCarrier::new(tls,if vision { Some(id) } else { None })};
                Ok((carrier,guard))
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
