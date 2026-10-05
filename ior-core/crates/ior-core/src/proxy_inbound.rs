//! Local TCP ingress only. A real native outbound must be supplied explicitly;
//! there is no direct fallback in production code.

use std::{
    future::Future,
    io,
    net::{Ipv4Addr, Ipv6Addr},
    pin::Pin,
    sync::Arc,
    time::Duration,
};
use tokio::{
    io::{copy_bidirectional, AsyncReadExt, AsyncWriteExt},
    net::{TcpListener, TcpStream},
    sync::Semaphore,
    time::{timeout, timeout_at, Instant},
};

const MAX_CLIENTS: usize = 256;
const HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(10);
const CONNECT_TIMEOUT: Duration = Duration::from_secs(15);

async fn during_handshake<T>(
    deadline: Instant,
    future: impl Future<Output = io::Result<T>>,
) -> io::Result<T> {
    timeout_at(deadline, future)
        .await
        .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "proxy handshake timed out"))?
}

pub trait RelayStream: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin + Send {}
impl<T: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin + Send> RelayStream for T {}
pub type ConnectFuture = Pin<Box<dyn Future<Output = io::Result<Box<dyn RelayStream>>> + Send>>;
pub type AddressFuture = Pin<Box<dyn Future<Output = io::Result<Vec<std::net::IpAddr>>> + Send>>;
pub trait OutboundConnector: Send + Sync {
    fn dns_hedge_delay(&self) -> Duration {
        Duration::from_millis(500)
    }
    fn ipv4_only(&self) -> bool {
        false
    }
    fn ipv6_only(&self) -> bool {
        false
    }
    fn resolve_candidates(&self, _host: String) -> Option<AddressFuture> {
        None
    }
    fn connect(&self, host: String, port: u16) -> ConnectFuture;
    fn udp(&self, _host: String, _port: u16) -> crate::udp_relay::UdpFuture {
        Box::pin(async {
            Err(io::Error::new(
                io::ErrorKind::Unsupported,
                "UDP_TRANSPORT_UNAVAILABLE",
            ))
        })
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MixedInbound {
    pub port: u16,
    pub localhost_only: bool,
}

impl Default for MixedInbound {
    fn default() -> Self {
        Self {
            port: 2080,
            localhost_only: true,
        }
    }
}

/// Accepts HTTP CONNECT and SOCKS5 CONNECT on one loopback listener.
/// SOCKS5 UDP associations are scoped to their loopback TCP control connection.
pub async fn serve_local(
    listener: TcpListener,
    connector: Arc<dyn OutboundConnector>,
) -> io::Result<()> {
    serve_local_authenticated(listener, connector, None).await
}
pub async fn serve_local_authenticated(
    listener: TcpListener,
    connector: Arc<dyn OutboundConnector>,
    auth: Option<crate::ipc_contract::IngressAuth>,
) -> io::Result<()> {
    if !listener.local_addr()?.ip().is_loopback() {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "ior-core proxy inbound must listen on loopback",
        ));
    }
    let slots = Arc::new(Semaphore::new(MAX_CLIENTS));
    // Dropping the listener task must also close every accepted flow on stop/IPC EOF.
    let mut clients = tokio::task::JoinSet::new();
    loop {
        while clients.try_join_next().is_some() {}
        let permit = Arc::clone(&slots)
            .acquire_owned()
            .await
            .map_err(|_| io::Error::new(io::ErrorKind::BrokenPipe, "proxy ingress closed"))?;
        let (client, _) = listener.accept().await?;
        let connector = Arc::clone(&connector);
        let auth = auth.clone();
        clients.spawn(crate::trace::FLOW.scope(crate::trace::new_flow(),async move {
            let _permit = permit;
            crate::trace::code("ingress","LOCAL_FLOW_ACCEPTED");
            if let Err(error) = handle_client_authenticated(client, connector, auth).await {
                let detail=error.to_string();
                let reason=if detail.len()<=80 && detail.bytes().all(|byte|byte.is_ascii_uppercase() || byte.is_ascii_digit() || byte==b'_') {detail.as_str()} else {"FLOW_IO_ERROR"};
                crate::trace::emit(serde_json::json!({"stage":"ingress","code":"LOCAL_FLOW_FAILED","kind":format!("{:?}",error.kind()),"reason":reason}));
            }
        }));
    }
}

async fn handle_client_authenticated(
    mut client: TcpStream,
    connector: Arc<dyn OutboundConnector>,
    auth: Option<crate::ipc_contract::IngressAuth>,
) -> io::Result<()> {
    let deadline = Instant::now() + HANDSHAKE_TIMEOUT;
    let first = during_handshake(deadline, client.read_u8()).await?;
    if auth.is_some() && first != 5 {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "BRIDGE_SOCKS_AUTH_REQUIRED",
        ));
    }
    match first {
        0x04 => handle_socks4(client, connector, deadline).await,
        0x05 => handle_socks5(client, connector, deadline, auth).await,
        b'C' | b'G' | b'P' | b'H' | b'D' | b'O' => {
            handle_http_connect(client, connector, deadline, first).await
        }
        _ => Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "INGRESS_PROTOCOL_UNSUPPORTED",
        )),
    }
}

// Windows' legacy socks= proxy entry is interpreted as SOCKS4 by some clients.
// SOCKS4a carries a hostname; neither variant supports UDP ASSOCIATE.
async fn handle_socks4(
    mut client: TcpStream,
    connector: Arc<dyn OutboundConnector>,
    deadline: Instant,
) -> io::Result<()> {
    let command = during_handshake(deadline, client.read_u8()).await?;
    let port = during_handshake(deadline, client.read_u16()).await?;
    let mut address = [0; 4];
    during_handshake(deadline, client.read_exact(&mut address)).await?;
    async fn terminated(client: &mut TcpStream, deadline: Instant) -> io::Result<Vec<u8>> {
        let mut value = Vec::new();
        for _ in 0..256 {
            let byte = during_handshake(deadline, client.read_u8()).await?;
            if byte == 0 {
                return Ok(value);
            }
            value.push(byte);
        }
        Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "SOCKS4_FIELD_TOO_LONG",
        ))
    }
    let _user = terminated(&mut client, deadline).await?;
    let host = if address[..3] == [0, 0, 0] && address[3] != 0 {
        String::from_utf8(terminated(&mut client, deadline).await?)
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "SOCKS4_HOST_INVALID"))?
    } else {
        Ipv4Addr::from(address).to_string()
    };
    if command != 1 || port == 0 || host.is_empty() || host.contains(['\r', '\n']) {
        client.write_all(&[0, 91, 0, 0, 0, 0, 0, 0]).await?;
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "SOCKS4_REQUEST_UNSUPPORTED",
        ));
    }
    eprintln!("{{\"stage\":\"ingress\",\"code\":\"SOCKS4_CONNECT_ACCEPTED\"}}");
    match timeout(CONNECT_TIMEOUT, connector.connect(host, port))
        .await
        .map_err(|_| io::ErrorKind::TimedOut)?
    {
        Ok(mut remote) => {
            client.write_all(&[0, 90, 0, 0, 0, 0, 0, 0]).await?;
            copy_bidirectional(&mut client, &mut remote).await?;
            Ok(())
        }
        Err(error) => {
            client.write_all(&[0, 91, 0, 0, 0, 0, 0, 0]).await?;
            Err(error)
        }
    }
}

async fn handle_http_connect(
    mut client: TcpStream,
    connector: Arc<dyn OutboundConnector>,
    deadline: Instant,
    first: u8,
) -> io::Result<()> {
    let mut request = vec![first];
    while !request.ends_with(b"\r\n\r\n") {
        if request.len() >= 65536 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "HTTP_HEADER_TOO_LARGE",
            ));
        }
        request.push(during_handshake(deadline, client.read_u8()).await?);
    }
    let text = std::str::from_utf8(&request)
        .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "invalid HTTP header"))?;
    let line = text.lines().next().unwrap_or("");
    let mut parts = line.split_whitespace();
    let method = parts.next().unwrap_or("");
    let target = parts.next().unwrap_or("");
    let version = parts.next().unwrap_or("");
    if !matches!(
        method,
        "CONNECT" | "GET" | "POST" | "HEAD" | "PUT" | "DELETE" | "OPTIONS" | "PATCH"
    ) || !matches!(version, "HTTP/1.1" | "HTTP/1.0")
        || parts.next().is_some()
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "HTTP_REQUEST_INVALID",
        ));
    }
    let tunnel = method == "CONNECT";
    let (host, port, forward) = if tunnel {
        let (host, port) = parse_authority(target)?;
        (host, port, None)
    } else {
        let value = target.strip_prefix("http://").ok_or(io::Error::new(
            io::ErrorKind::InvalidInput,
            "HTTP_ABSOLUTE_URI_REQUIRED",
        ))?;
        let end = value.find(['/', '?', '#']).unwrap_or(value.len());
        let authority = &value[..end];
        if authority.contains('@') || authority.is_empty() {
            return Err(io::ErrorKind::InvalidInput.into());
        }
        let (host, port) = if authority.contains(':') && !authority.ends_with(']') {
            parse_authority(authority)?
        } else {
            (
                authority
                    .trim_start_matches('[')
                    .trim_end_matches(']')
                    .to_string(),
                80,
            )
        };
        let path = if end == value.len() {
            "/".to_string()
        } else if value[end..].starts_with('/') {
            value[end..].to_string()
        } else {
            format!("/{}", &value[end..])
        };
        let path = path.split('#').next().unwrap();
        let mut rewritten = format!("{method} {path} {version}\r\n");
        for header in text.lines().skip(1).filter(|line| !line.is_empty()) {
            let name = header.split(':').next().unwrap_or("");
            if !name.eq_ignore_ascii_case("proxy-connection")
                && !name.eq_ignore_ascii_case("proxy-authorization")
                && !name.eq_ignore_ascii_case("connection")
            {
                rewritten.push_str(header);
                rewritten.push_str("\r\n");
            }
        }
        rewritten.push_str("Connection: close\r\n\r\n");
        (host, port, Some(rewritten.into_bytes()))
    };
    match timeout(CONNECT_TIMEOUT, connector.connect(host, port))
        .await
        .unwrap_or_else(|_| {
            Err(io::Error::new(
                io::ErrorKind::TimedOut,
                "outbound connect timed out",
            ))
        }) {
        Ok(mut remote) => {
            crate::trace::code("relay", "TCP_RELAY_READY");
            if let Some(header) = forward {
                remote.write_all(&header).await?;
                remote.flush().await?;
            } else {
                client
                    .write_all(b"HTTP/1.1 200 Connection Established\r\n\r\n")
                    .await?;
            }
            let (sent, received) = copy_bidirectional(&mut client, &mut remote).await?;
            crate::trace::emit(
                serde_json::json!({"stage":"relay","code":"TCP_RELAY_CLOSED","sentBytes":sent,"receivedBytes":received}),
            );
            Ok(())
        }
        Err(error) => {
            client
                .write_all(b"HTTP/1.1 502 Bad Gateway\r\n\r\n")
                .await?;
            Err(error)
        }
    }
}

fn parse_authority(authority: &str) -> io::Result<(String, u16)> {
    let (host, port) = authority
        .rsplit_once(':')
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "CONNECT port is missing"))?;
    let host = host.trim_start_matches('[').trim_end_matches(']');
    let port = port
        .parse::<u16>()
        .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "invalid CONNECT port"))?;
    if host.is_empty() || port == 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "invalid CONNECT authority",
        ));
    }
    Ok((host.to_owned(), port))
}

async fn handle_socks5(
    mut client: TcpStream,
    connector: Arc<dyn OutboundConnector>,
    deadline: Instant,
    auth: Option<crate::ipc_contract::IngressAuth>,
) -> io::Result<()> {
    let count = during_handshake(deadline, client.read_u8()).await? as usize;
    if count == 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "no SOCKS5 auth methods",
        ));
    }
    let mut methods = vec![0; count];
    during_handshake(deadline, client.read_exact(&mut methods)).await?;
    let method = if auth.is_some() { 2 } else { 0 };
    if !methods.contains(&method) {
        client.write_all(&[5, 0xff]).await?;
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "SOCKS5 no-auth not offered",
        ));
    }
    client.write_all(&[5, method]).await?;
    if let Some(auth) = auth {
        let version = during_handshake(deadline, client.read_u8()).await?;
        let length = during_handshake(deadline, client.read_u8()).await? as usize;
        let mut username = vec![0; length];
        during_handshake(deadline, client.read_exact(&mut username)).await?;
        let length = during_handshake(deadline, client.read_u8()).await? as usize;
        let mut password = vec![0; length];
        during_handshake(deadline, client.read_exact(&mut password)).await?;
        // Constant-time comparison of fixed-length digests; credentials never enter diagnostics.
        use sha2::{Digest, Sha256};
        let expected = Sha256::digest(format!("{}\0{}", auth.username, auth.password).as_bytes());
        let received = Sha256::digest([username.as_slice(), b"\0", password.as_slice()].concat());
        let valid = version == 1
            && expected
                .iter()
                .zip(received.iter())
                .fold(0u8, |diff, (a, b)| diff | (a ^ b))
                == 0;
        client.write_all(&[1, if valid { 0 } else { 1 }]).await?;
        if !valid {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "BRIDGE_SOCKS_AUTH_REJECTED",
            ));
        }
    }
    let mut header = [0; 4];
    during_handshake(deadline, client.read_exact(&mut header)).await?;
    if header[0] != 5 || header[2] != 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "invalid SOCKS5 request",
        ));
    }
    if !matches!(header[1], 1 | 3) {
        client.write_all(&[5, 7, 0, 1, 0, 0, 0, 0, 0, 0]).await?;
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "SOCKS5 command unsupported",
        ));
    }
    let host = match header[3] {
        1 => {
            let mut octets = [0; 4];
            during_handshake(deadline, client.read_exact(&mut octets)).await?;
            Ipv4Addr::from(octets).to_string()
        }
        4 => {
            let mut octets = [0; 16];
            during_handshake(deadline, client.read_exact(&mut octets)).await?;
            Ipv6Addr::from(octets).to_string()
        }
        3 => {
            let len = during_handshake(deadline, client.read_u8()).await? as usize;
            if len == 0 {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "empty SOCKS5 host",
                ));
            }
            let mut bytes = vec![0; len];
            during_handshake(deadline, client.read_exact(&mut bytes)).await?;
            String::from_utf8(bytes)
                .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "invalid SOCKS5 host"))?
        }
        _ => {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "invalid SOCKS5 address type",
            ))
        }
    };
    let port = during_handshake(deadline, client.read_u16()).await?;
    if header[1] == 3 {
        return serve_udp_association(client, connector, host, port).await;
    }
    match timeout(CONNECT_TIMEOUT, connector.connect(host, port))
        .await
        .unwrap_or_else(|_| {
            Err(io::Error::new(
                io::ErrorKind::TimedOut,
                "outbound connect timed out",
            ))
        }) {
        Ok(mut remote) => {
            client.write_all(&[5, 0, 0, 1, 0, 0, 0, 0, 0, 0]).await?;
            copy_bidirectional(&mut client, &mut remote).await?;
            Ok(())
        }
        Err(error) => {
            client.write_all(&[5, 5, 0, 1, 0, 0, 0, 0, 0, 0]).await?;
            Err(error)
        }
    }
}

async fn serve_udp_association(
    mut client: TcpStream,
    connector: Arc<dyn OutboundConnector>,
    requested_host: String,
    requested_port: u16,
) -> io::Result<()> {
    use std::{collections::HashMap, net::SocketAddr};
    use tokio::{net::UdpSocket, sync::mpsc};
    let peer = client.peer_addr()?;
    if !peer.ip().is_loopback() {
        return Err(io::ErrorKind::PermissionDenied.into());
    }
    let requested_ip: std::net::IpAddr = requested_host
        .parse()
        .map_err(|_| io::ErrorKind::InvalidInput)?;
    if !requested_ip.is_unspecified() && requested_ip != peer.ip() {
        return Err(io::ErrorKind::PermissionDenied.into());
    }
    let socket = Arc::new(UdpSocket::bind(("127.0.0.1", 0)).await?);
    let address = socket.local_addr()?;
    let mut reply = vec![5, 0, 0];
    reply.extend(crate::udp_relay::encode_address(
        &address.ip().to_string(),
        address.port(),
    )?);
    client.write_all(&reply).await?;
    let mut learned_peer: Option<SocketAddr> = None;
    let mut flows: HashMap<(String, u16), mpsc::Sender<Vec<u8>>> = HashMap::new();
    let mut tasks = tokio::task::JoinSet::new();
    let mut packet = vec![0; 65535];
    let mut control = [0u8; 1];
    loop {
        while let Some(result) = tasks.try_join_next() {
            if !matches!(result, Ok(Ok(()))) {
                eprintln!(
                    "{}",
                    serde_json::json!({"stage":"udp","code":"UDP_RELAY_FAILED"})
                );
            }
        }
        flows.retain(|_, sender| !sender.is_closed());
        let (n, source) = tokio::select! {
            _ = client.read(&mut control) => return Ok(()),
            received = timeout(Duration::from_secs(120),socket.recv_from(&mut packet)) => {
                match received { Ok(result)=>result?, Err(_)=>return Ok(()) }
            }
        };
        if source.ip() != peer.ip()
            || (requested_port != 0 && source.port() != requested_port)
            || learned_peer.is_some_and(|value| value != source)
        {
            continue;
        }
        let Ok((host, port, offset)) = crate::udp_relay::decode_packet(&packet[..n]) else {
            continue;
        };
        learned_peer = Some(source);
        let key = (host.clone(), port);
        if flows.get(&key).is_some_and(|sender| sender.is_closed()) {
            flows.remove(&key);
        }
        if !flows.contains_key(&key) {
            if flows.len() >= 64 {
                continue;
            }
            let (tx, mut rx) = mpsc::channel::<Vec<u8>>(64);
            flows.insert(key, tx);
            let connector = connector.clone();
            let socket = socket.clone();
            let host = host.clone();
            tasks.spawn(async move {
                let mut relay=timeout(CONNECT_TIMEOUT,connector.udp(host.clone(),port)).await
                    .map_err(|_|io::ErrorKind::TimedOut)??;
                let mut prefix=vec![0,0,0];prefix.extend(crate::udp_relay::encode_address(&host,port)?);
                loop {
                    tokio::select! {
                        payload=rx.recv()=>match payload {Some(data)=>relay.send(&data).await?,None=>return Ok::<(),io::Error>(())},
                        payload=timeout(Duration::from_secs(60),relay.receive())=>{
                            let data=payload.map_err(|_|io::ErrorKind::TimedOut)??;
                            let mut reply=prefix.clone();reply.extend(data);
                            socket.send_to(&reply,source).await?;
                        }
                    }
                }
            });
        }
        if let Some(sender) = flows.get(&(host, port)) {
            let _ = sender.try_send(packet[offset..n].to_vec());
        }
    }
}

#[cfg(test)]
mod tests {
    #[tokio::test]
    async fn private_bridge_requires_socks_password_and_rejects_wrong_credentials() {
        use super::*;
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let task = tokio::spawn(serve_local_authenticated(
            listener,
            Arc::new(DirectTestConnector),
            Some(crate::ipc_contract::IngressAuth {
                username: "ior".into(),
                password: "test-only-long-password".into(),
            }),
        ));
        let mut anonymous = TcpStream::connect(address).await.unwrap();
        anonymous.write_all(&[5, 1, 0]).await.unwrap();
        let mut reply = [0; 2];
        anonymous.read_exact(&mut reply).await.unwrap();
        assert_eq!(reply, [5, 255]);
        for (password, success) in [("wrong", false), ("test-only-long-password", true)] {
            let mut client = TcpStream::connect(address).await.unwrap();
            client.write_all(&[5, 1, 2]).await.unwrap();
            client.read_exact(&mut reply).await.unwrap();
            assert_eq!(reply, [5, 2]);
            let mut auth = vec![1, 3, b'i', b'o', b'r', password.len() as u8];
            auth.extend(password.as_bytes());
            client.write_all(&auth).await.unwrap();
            client.read_exact(&mut reply).await.unwrap();
            assert_eq!(reply, [1, if success { 0 } else { 1 }]);
        }
        task.abort();
        let _ = task.await;
    }
    use super::*;
    #[tokio::test]
    async fn socks4_and_socks4a_relay_tcp() {
        for domain in [false, true] {
            let echo = TcpListener::bind("127.0.0.1:0").await.unwrap();
            let destination = echo.local_addr().unwrap().port();
            let remote = tokio::spawn(async move {
                let (mut socket, _) = echo.accept().await.unwrap();
                let mut data = [0; 4];
                socket.read_exact(&mut data).await.unwrap();
                socket.write_all(&data).await.unwrap();
            });
            let inbound = TcpListener::bind("127.0.0.1:0").await.unwrap();
            let port = inbound.local_addr().unwrap().port();
            let task = tokio::spawn(serve_local(inbound, Arc::new(DirectTestConnector)));
            let mut client = TcpStream::connect(("127.0.0.1", port)).await.unwrap();
            let mut request = vec![4, 1];
            request.extend(destination.to_be_bytes());
            request.extend(if domain { [0, 0, 0, 1] } else { [127, 0, 0, 1] });
            request.extend(b"user\0");
            if domain {
                request.extend(b"127.0.0.1\0");
            }
            client.write_all(&request).await.unwrap();
            let mut response = [0; 8];
            client.read_exact(&mut response).await.unwrap();
            assert_eq!(response[1], 90);
            client.write_all(b"ping").await.unwrap();
            let mut reply = [0; 4];
            client.read_exact(&mut reply).await.unwrap();
            assert_eq!(&reply, b"ping");
            remote.await.unwrap();
            task.abort();
        }
    }

    #[tokio::test]
    async fn ordinary_http_rewrites_uri_and_removes_proxy_credentials() {
        let server = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let destination = server.local_addr().unwrap().port();
        let remote = tokio::spawn(async move {
            let (mut socket, _) = server.accept().await.unwrap();
            let mut bytes = Vec::new();
            loop {
                bytes.push(socket.read_u8().await.unwrap());
                if bytes.ends_with(b"\r\n\r\n") {
                    break;
                }
            }
            let header = String::from_utf8(bytes).unwrap();
            assert!(header.starts_with("GET /sample?q=1 HTTP/1.1"));
            assert!(!header.to_lowercase().contains("proxy-authorization"));
            assert!(header.contains("Connection: close"));
            socket
                .write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok")
                .await
                .unwrap();
        });
        let inbound = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = inbound.local_addr().unwrap().port();
        let task = tokio::spawn(serve_local(inbound, Arc::new(DirectTestConnector)));
        let mut socket = TcpStream::connect(("127.0.0.1", port)).await.unwrap();
        socket.write_all(format!("GET http://127.0.0.1:{destination}/sample?q=1 HTTP/1.1\r\nHost: localhost\r\nProxy-Authorization: secret\r\n\r\n").as_bytes()).await.unwrap();
        let mut response = String::new();
        timeout(Duration::from_secs(3), socket.read_to_string(&mut response))
            .await
            .unwrap()
            .unwrap();
        assert!(response.ends_with("\r\n\r\nok"));
        remote.await.unwrap();
        task.abort();
    }

    struct DirectTestConnector;
    impl OutboundConnector for DirectTestConnector {
        fn connect(&self, host: String, port: u16) -> ConnectFuture {
            Box::pin(async move {
                let remote = TcpStream::connect((host.as_str(), port)).await?;
                Ok(Box::new(remote) as Box<dyn RelayStream>)
            })
        }
    }

    #[tokio::test]
    async fn http_connect_relays_loopback_tcp() {
        let echo = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let echo_port = echo.local_addr().unwrap().port();
        let echo_task = tokio::spawn(async move {
            let (mut peer, _) = echo.accept().await.unwrap();
            let mut payload = [0; 4];
            peer.read_exact(&mut payload).await.unwrap();
            peer.write_all(&payload).await.unwrap();
        });
        let inbound = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = inbound.local_addr().unwrap().port();
        let task = tokio::spawn(serve_local(inbound, Arc::new(DirectTestConnector)));
        let mut client = TcpStream::connect(("127.0.0.1", port)).await.unwrap();
        client
            .write_all(format!("CONNECT 127.0.0.1:{echo_port} HTTP/1.1\r\n\r\n").as_bytes())
            .await
            .unwrap();
        let mut response = [0; 39];
        client.read_exact(&mut response).await.unwrap();
        assert!(std::str::from_utf8(&response)
            .unwrap()
            .starts_with("HTTP/1.1 200"));
        client.write_all(b"ping").await.unwrap();
        let mut reply = [0; 4];
        client.read_exact(&mut reply).await.unwrap();
        assert_eq!(&reply, b"ping");
        task.abort();
        echo_task.await.unwrap();
    }

    #[tokio::test]
    async fn socks5_connect_relays_loopback_tcp() {
        let echo = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let echo_port = echo.local_addr().unwrap().port();
        let echo_task = tokio::spawn(async move {
            let (mut peer, _) = echo.accept().await.unwrap();
            let mut payload = [0; 4];
            peer.read_exact(&mut payload).await.unwrap();
            peer.write_all(&payload).await.unwrap();
        });
        let inbound = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = inbound.local_addr().unwrap().port();
        let task = tokio::spawn(serve_local(inbound, Arc::new(DirectTestConnector)));
        let mut client = TcpStream::connect(("127.0.0.1", port)).await.unwrap();
        client.write_all(&[5, 1, 0]).await.unwrap();
        let mut greeting = [0; 2];
        client.read_exact(&mut greeting).await.unwrap();
        assert_eq!(greeting, [5, 0]);
        let mut request = vec![5, 1, 0, 1, 127, 0, 0, 1];
        request.extend_from_slice(&echo_port.to_be_bytes());
        client.write_all(&request).await.unwrap();
        let mut response = [0; 10];
        client.read_exact(&mut response).await.unwrap();
        assert_eq!(&response[..4], &[5, 0, 0, 1]);
        client.write_all(b"ping").await.unwrap();
        let mut reply = [0; 4];
        client.read_exact(&mut reply).await.unwrap();
        assert_eq!(&reply, b"ping");
        task.abort();
        echo_task.await.unwrap();
    }

    #[test]
    fn rejects_missing_or_zero_port() {
        assert!(parse_authority("example.com").is_err());
        assert!(parse_authority("example.com:0").is_err());
    }
}
