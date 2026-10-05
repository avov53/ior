//! VMess AEAD, Shadowsocks and Trojan outbounds sharing ior ingress/routing.
use crate::{
    ipc_contract::{WireNetwork, WireProfile},
    proxy_inbound::{ConnectFuture, OutboundConnector, RelayStream},
    udp_relay::{DatagramRelay, ReceiveFuture, SendFuture, UdpFuture},
};
use ior_vmess_codec::{
    address::{Address, NetLocation},
    async_stream as vmio,
    tcp_handler::{TcpClientHandler, TcpClientUdpSetupResult, UdpStreamRequest},
    vmess::VmessTcpClientHandler,
};
use sha2::{Digest, Sha224};
use std::{
    io,
    net::SocketAddr,
    pin::Pin,
    sync::Arc,
    task::{Context, Poll},
};
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt, ReadBuf};
use vmio::{AsyncFlushMessage, AsyncReadMessage, AsyncWriteMessage};

#[derive(Clone)]
pub struct NativeProtocolConnector {
    profile: WireProfile,
    bootstrap: SocketAddr,
    network: Option<WireNetwork>,
    tls: tokio_rustls::TlsConnector,
}
impl NativeProtocolConnector {
    pub fn new(
        profile: &WireProfile,
        bootstrap: SocketAddr,
        network: Option<WireNetwork>,
    ) -> io::Result<Self> {
        match profile.protocol.as_str() {
            "vmess" => {
                uuid::Uuid::parse_str(&profile.credential)
                    .map_err(|_| invalid("VMESS_UUID_INVALID"))?;
                if profile.options.alter_id.unwrap_or(0) != 0 {
                    return Err(invalid("VMESS_LEGACY_ALTER_ID_UNSUPPORTED"));
                }
                if !matches!(
                    profile.options.method.as_deref(),
                    None | Some("")
                        | Some("auto")
                        | Some("aes-128-gcm")
                        | Some("chacha20-poly1305")
                        | Some("none")
                ) {
                    return Err(invalid("VMESS_CIPHER_UNSUPPORTED"));
                }
            }
            "shadowsocks" => {
                ss_config(profile, "127.0.0.1:1".parse().unwrap())?;
            }
            "trojan" if profile.options.tls == Some(true) => (),
            _ => return Err(invalid("TRANSPORT_PROFILE_UNSUPPORTED")),
        }
        if !matches!(
            profile.options.transport.as_deref(),
            None | Some("") | Some("tcp")
        ) {
            return Err(invalid("TRANSPORT_VARIANT_UNSUPPORTED"));
        }
        let mut roots = tokio_rustls::rustls::RootCertStore::empty();
        roots.extend(webpki_roots::TLS_SERVER_ROOTS.iter().cloned());
        let tls = tokio_rustls::TlsConnector::from(Arc::new(
            tokio_rustls::rustls::ClientConfig::builder()
                .with_root_certificates(roots)
                .with_no_client_auth(),
        ));
        Ok(Self {
            profile: profile.clone(),
            bootstrap,
            network,
            tls,
        })
    }
    async fn endpoint(&self) -> io::Result<SocketAddr> {
        Ok(SocketAddr::new(
            crate::dns_engine::resolve_server_ipv4(&self.profile.server, self.bootstrap)
                .await?
                .into(),
            self.profile.port,
        ))
    }
    async fn stream(&self) -> io::Result<Box<dyn RelayStream>> {
        let address = self.endpoint().await?;
        let socket = tokio::net::TcpSocket::from_std_stream(
            crate::native_router::bound_socket(false, &self.network)?.into(),
        );
        let socket = socket.connect(address).await?;
        socket.set_nodelay(true)?;
        let guard = crate::egress_registry::register(socket.local_addr()?, address, false);
        crate::trace::code("handshake", "TRANSPORT_TCP_CONNECTED");
        let socket = crate::egress_registry::RegisteredStream::new(socket, guard);
        if self.profile.options.tls.unwrap_or(false) {
            let name = tokio_rustls::rustls::pki_types::ServerName::try_from(
                self.profile.server_name.clone(),
            )
            .map_err(|_| invalid("TLS_SNI_INVALID"))?;
            let stream = self
                .tls
                .connect(name, socket)
                .await
                .map_err(|_| invalid("TLS_AUTHENTICATION_FAILED"))?;
            crate::trace::code("handshake", "TRANSPORT_TLS_VERIFIED");
            Ok(Box::new(stream))
        } else {
            Ok(Box::new(socket))
        }
    }
    fn vmess(&self) -> VmessTcpClientHandler {
        let method = match self.profile.options.method.as_deref() {
            None | Some("") | Some("auto") => "aes-128-gcm",
            Some(value) => value,
        };
        VmessTcpClientHandler::new(method, &self.profile.credential, true)
    }
}
fn invalid(code: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidInput, code)
}
fn ss_config(
    profile: &WireProfile,
    endpoint: SocketAddr,
) -> io::Result<shadowsocks::config::ServerConfig> {
    let method = profile
        .options
        .method
        .as_deref()
        .unwrap_or("")
        .parse::<shadowsocks::crypto::CipherKind>()
        .map_err(|_| invalid("SHADOWSOCKS_CIPHER_UNSUPPORTED"))?;
    shadowsocks::config::ServerConfig::new(endpoint, &profile.credential, method)
        .map_err(|_| invalid("SHADOWSOCKS_KEY_INVALID"))
}
fn trojan_header(password: &str, command: u8, host: &str, port: u16) -> io::Result<Vec<u8>> {
    let hash = Sha224::digest(password.as_bytes());
    let mut header = format!("{:x}\r\n", hash).into_bytes();
    header.push(command);
    header.extend(crate::udp_relay::encode_address(host, port)?);
    header.extend(b"\r\n");
    Ok(header)
}
impl OutboundConnector for NativeProtocolConnector {
    fn connect(&self, host: String, port: u16) -> ConnectFuture {
        let this = self.clone();
        Box::pin(async move {
            let mut stream = this.stream().await?;
            match this.profile.protocol.as_str() {
                "trojan" => {
                    stream
                        .write_all(&trojan_header(&this.profile.credential, 1, &host, port)?)
                        .await?;
                    stream.flush().await?;
                    Ok(stream)
                }
                "vmess" => {
                    let location = NetLocation::new(Address::from(&host)?, port);
                    let result = this
                        .vmess()
                        .setup_client_tcp_stream(Box::new(VmAdapter(stream)), location)
                        .await?;
                    Ok(Box::new(result.client_stream) as Box<dyn RelayStream>)
                }
                "shadowsocks" => {
                    let config = ss_config(&this.profile, this.endpoint().await?)?;
                    let context = shadowsocks::context::Context::new_shared(
                        shadowsocks::config::ServerType::Local,
                    );
                    let addr = shadowsocks::relay::socks5::Address::DomainNameAddress(host, port);
                    Ok(Box::new(BoundedWrite(
                        shadowsocks::relay::tcprelay::proxy_stream::ProxyClientStream::from_stream(
                            context, stream, &config, addr,
                        ),
                    )) as Box<dyn RelayStream>)
                }
                _ => Err(invalid("PROTOCOL_UNSUPPORTED")),
            }
        })
    }
    fn udp(&self, host: String, port: u16) -> UdpFuture {
        let this = self.clone();
        Box::pin(async move {
            if this.profile.protocol == "shadowsocks" {
                let endpoint = this.endpoint().await?;
                let socket = tokio::net::UdpSocket::from_std(
                    crate::native_router::bound_socket(true, &this.network)?.into(),
                )?;
                socket.connect(endpoint).await?;
                let guard = crate::egress_registry::register(socket.local_addr()?, endpoint, true);
                let config = ss_config(&this.profile, endpoint)?;
                let context = shadowsocks::context::Context::new_shared(
                    shadowsocks::config::ServerType::Local,
                );
                let socket = shadowsocks::relay::udprelay::proxy_socket::ProxySocket::from_socket(
                    shadowsocks::relay::udprelay::proxy_socket::UdpSocketType::Client,
                    context,
                    &config,
                    shadowsocks::net::UdpSocket::from(socket),
                );
                let mut control =
                    shadowsocks::relay::udprelay::options::UdpSocketControlData::default();
                let mut random = [0; 8];
                getrandom::getrandom(&mut random).map_err(io::Error::other)?;
                control.client_session_id = u64::from_ne_bytes(random);
                return Ok(Box::new(SsDatagram {
                    socket,
                    destination: shadowsocks::relay::socks5::Address::DomainNameAddress(host, port),
                    control,
                    windows: std::collections::HashMap::new(),
                    _guard: guard,
                }) as Box<dyn DatagramRelay>);
            }
            let mut stream = this.stream().await?;
            if this.profile.protocol == "trojan" {
                stream
                    .write_all(&trojan_header(&this.profile.credential, 3, "0.0.0.0", 1)?)
                    .await?;
                return Ok(Box::new(TrojanDatagram {
                    stream,
                    header: crate::udp_relay::encode_address(&host, port)?,
                    port,
                    buffer: Vec::new(),
                }) as Box<dyn DatagramRelay>);
            }
            let request = UdpStreamRequest::Bidirectional {
                server_stream: Box::new(NullMessage),
                target: NetLocation::new(Address::from(&host)?, port),
            };
            match this
                .vmess()
                .setup_client_udp_stream(Box::new(VmAdapter(stream)), request)
                .await?
            {
                TcpClientUdpSetupResult::Bidirectional { client_stream, .. } => {
                    Ok(Box::new(VmDatagram(client_stream)) as Box<dyn DatagramRelay>)
                }
                _ => Err(invalid("VMESS_UDP_SETUP_FAILED")),
            }
        })
    }
}
struct VmAdapter(Box<dyn RelayStream>);
// Keep the first address+payload below the SS AEAD frame limit. The library
// assumes its first encrypted write is complete, unlike later partial writes.
struct BoundedWrite<S>(S);
impl<S: AsyncRead + Unpin> AsyncRead for BoundedWrite<S> {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        Pin::new(&mut self.0).poll_read(cx, buf)
    }
}
impl<S: AsyncWrite + Unpin> AsyncWrite for BoundedWrite<S> {
    fn poll_write(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &[u8],
    ) -> Poll<io::Result<usize>> {
        Pin::new(&mut self.0).poll_write(cx, &buf[..buf.len().min(8192)])
    }
    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.0).poll_flush(cx)
    }
    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.0).poll_shutdown(cx)
    }
}
impl AsyncRead for VmAdapter {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        Pin::new(&mut self.0).poll_read(cx, buf)
    }
}
impl AsyncWrite for VmAdapter {
    fn poll_write(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &[u8],
    ) -> Poll<io::Result<usize>> {
        Pin::new(&mut self.0).poll_write(cx, buf)
    }
    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.0).poll_flush(cx)
    }
    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.0).poll_shutdown(cx)
    }
}
impl vmio::AsyncPing for VmAdapter {
    fn supports_ping(&self) -> bool {
        false
    }
    fn poll_write_ping(self: Pin<&mut Self>, _: &mut Context<'_>) -> Poll<io::Result<bool>> {
        Poll::Ready(Ok(false))
    }
}
impl vmio::AsyncStream for VmAdapter {}
struct NullMessage;
impl vmio::AsyncPing for NullMessage {
    fn supports_ping(&self) -> bool {
        false
    }
    fn poll_write_ping(self: Pin<&mut Self>, _: &mut Context<'_>) -> Poll<io::Result<bool>> {
        Poll::Ready(Ok(false))
    }
}
impl vmio::AsyncReadMessage for NullMessage {
    fn poll_read_message(
        self: Pin<&mut Self>,
        _: &mut Context<'_>,
        _: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        Poll::Pending
    }
}
impl vmio::AsyncWriteMessage for NullMessage {
    fn poll_write_message(
        self: Pin<&mut Self>,
        _: &mut Context<'_>,
        _: &[u8],
    ) -> Poll<io::Result<()>> {
        Poll::Ready(Ok(()))
    }
}
impl vmio::AsyncFlushMessage for NullMessage {
    fn poll_flush_message(self: Pin<&mut Self>, _: &mut Context<'_>) -> Poll<io::Result<()>> {
        Poll::Ready(Ok(()))
    }
}
impl vmio::AsyncShutdownMessage for NullMessage {
    fn poll_shutdown_message(self: Pin<&mut Self>, _: &mut Context<'_>) -> Poll<io::Result<()>> {
        Poll::Ready(Ok(()))
    }
}
impl vmio::AsyncMessageStream for NullMessage {}
struct VmDatagram(Box<dyn vmio::AsyncMessageStream>);
impl DatagramRelay for VmDatagram {
    fn send<'a>(&'a mut self, data: &'a [u8]) -> SendFuture<'a> {
        Box::pin(async move {
            futures::future::poll_fn(|cx| Pin::new(&mut self.0).poll_write_message(cx, data))
                .await?;
            futures::future::poll_fn(|cx| Pin::new(&mut self.0).poll_flush_message(cx)).await
        })
    }
    fn receive(&mut self) -> ReceiveFuture<'_> {
        Box::pin(async move {
            let mut bytes = vec![0; 65535];
            let mut buf = ReadBuf::new(&mut bytes);
            futures::future::poll_fn(|cx| Pin::new(&mut self.0).poll_read_message(cx, &mut buf))
                .await?;
            Ok(buf.filled().to_vec())
        })
    }
}
struct SsDatagram {
    socket: shadowsocks::relay::udprelay::proxy_socket::ProxySocket<shadowsocks::net::UdpSocket>,
    destination: shadowsocks::relay::socks5::Address,
    control: shadowsocks::relay::udprelay::options::UdpSocketControlData,
    windows: std::collections::HashMap<u64, (u64, u64)>,
    _guard: Option<crate::egress_registry::Guard>,
}
impl DatagramRelay for SsDatagram {
    fn send<'a>(&'a mut self, data: &'a [u8]) -> SendFuture<'a> {
        Box::pin(async move {
            self.control.packet_id = self
                .control
                .packet_id
                .checked_add(1)
                .ok_or(io::ErrorKind::InvalidData)?;
            self.socket
                .send_with_ctrl(&self.destination, &self.control, data)
                .await
                .map_err(io::Error::from)?;
            Ok(())
        })
    }
    fn receive(&mut self) -> ReceiveFuture<'_> {
        Box::pin(async move {
            let mut buf = vec![0; 65536];
            loop {
                let (n, _, _, control) = self
                    .socket
                    .recv_with_ctrl(&mut buf)
                    .await
                    .map_err(io::Error::from)?;
                if let Some(control) = control {
                    if control.client_session_id != self.control.client_session_id {
                        return Err(invalid("SHADOWSOCKS_UDP_SESSION_MISMATCH"));
                    }
                    if !self.windows.contains_key(&control.server_session_id)
                        && self.windows.len() >= 8
                    {
                        return Err(invalid("SHADOWSOCKS_UDP_SESSION_LIMIT"));
                    }
                    let window = self
                        .windows
                        .entry(control.server_session_id)
                        .or_insert((0, 0));
                    if !accept_packet(window, control.packet_id) {
                        continue;
                    }
                }
                return Ok(buf[..n].to_vec());
            }
        })
    }
}
fn accept_packet(window: &mut (u64, u64), packet: u64) -> bool {
    if window.1 == 0 {
        *window = (packet, 1);
        return true;
    }
    if packet > window.0 {
        let shift = packet - window.0;
        window.1 = if shift >= 64 {
            1
        } else {
            (window.1 << shift) | 1
        };
        window.0 = packet;
        true
    } else {
        let shift = window.0 - packet;
        if shift >= 64 || window.1 & (1 << shift) != 0 {
            false
        } else {
            window.1 |= 1 << shift;
            true
        }
    }
}
#[cfg(test)]
#[path = "native_protocol_tests.rs"]
mod tests;
struct TrojanDatagram {
    stream: Box<dyn RelayStream>,
    header: Vec<u8>,
    port: u16,
    buffer: Vec<u8>,
}
impl DatagramRelay for TrojanDatagram {
    fn send<'a>(&'a mut self, data: &'a [u8]) -> SendFuture<'a> {
        Box::pin(async move {
            if data.len() > 65507 {
                return Err(io::ErrorKind::InvalidInput.into());
            }
            self.stream.write_all(&self.header).await?;
            self.stream.write_u16(data.len() as u16).await?;
            self.stream.write_all(b"\r\n").await?;
            self.stream.write_all(data).await?;
            self.stream.flush().await
        })
    }
    fn receive(&mut self) -> ReceiveFuture<'_> {
        Box::pin(async move {
            loop {
                if let Some(&atyp) = self.buffer.first() {
                    let address_size = match atyp {
                        1 => Some(5),
                        4 => Some(17),
                        3 => self.buffer.get(1).map(|length| 2 + *length as usize),
                        _ => return Err(io::ErrorKind::InvalidData.into()),
                    };
                    if let Some(size) = address_size {
                        if self.buffer.len() >= size + 6 {
                            let port =
                                u16::from_be_bytes(self.buffer[size..size + 2].try_into().unwrap());
                            let length = u16::from_be_bytes(
                                self.buffer[size + 2..size + 4].try_into().unwrap(),
                            ) as usize;
                            if self.buffer[size + 4..size + 6] != *b"\r\n" || port != self.port {
                                return Err(io::ErrorKind::InvalidData.into());
                            }
                            if self.buffer.len() >= size + 6 + length {
                                let data = self.buffer[size + 6..size + 6 + length].to_vec();
                                self.buffer.drain(..size + 6 + length);
                                return Ok(data);
                            }
                        }
                    }
                }
                if self.buffer.len() > 131072 {
                    return Err(io::ErrorKind::InvalidData.into());
                }
                if self.stream.read_buf(&mut self.buffer).await? == 0 {
                    return Err(io::ErrorKind::UnexpectedEof.into());
                }
            }
        })
    }
}
