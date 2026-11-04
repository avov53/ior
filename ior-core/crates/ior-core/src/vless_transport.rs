//! VLESS carriers. TLS/REALITY and the VLESS codec are separate from HTTP framing.
use crate::{
    ipc_contract::WireProfile,
    proxy_inbound::{ConnectFuture, RelayStream},
    vless_codec,
};
use bytes::{Buf, Bytes, BytesMut};
use futures::{SinkExt, StreamExt};
use std::{
    collections::HashMap,
    io,
    pin::Pin,
    sync::Arc,
    task::{Context, Poll},
};
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt, ReadBuf};
pub type Dial = Arc<dyn Fn(WireProfile) -> ConnectFuture + Send + Sync>;
fn invalid(code: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidInput, code)
}
pub fn kind(profile: &WireProfile) -> &str {
    match profile.options.transport.as_deref().unwrap_or("tcp") {
        "" | "tcp" | "raw" => "raw",
        "websocket" | "ws" => "ws",
        value => value,
    }
}
pub fn validate(profile: &WireProfile) -> io::Result<()> {
    let transport = kind(profile);
    if !matches!(transport, "raw" | "ws" | "httpupgrade" | "grpc" | "xhttp") {
        return Err(invalid("VLESS_TRANSPORT_UNSUPPORTED"));
    }
    if let Some(protocols)=&profile.options.alpn{if protocols.is_empty()||protocols.iter().any(|p|match transport{"grpc"|"xhttp"=>p!="h2","ws"|"httpupgrade"=>p!="http/1.1",_=>!matches!(p.as_str(),"h2"|"http/1.1")}){return Err(invalid("VLESS_ALPN_UNSUPPORTED"));}}
    if profile.protocol == "vless_reality" && matches!(transport, "ws" | "httpupgrade") {
        return Err(invalid("VLESS_REALITY_TRANSPORT_INCOMPATIBLE"));
    }
    if transport != "raw" && profile.flow.as_deref().is_some_and(|s| !s.is_empty()) {
        return Err(invalid("VLESS_VISION_REQUIRES_RAW"));
    }
    if profile.protocol == "vless_tls" && profile.flow.as_deref().is_some_and(|s| !s.is_empty()) {
        return Err(invalid("VLESS_TLS_VISION_UNSUPPORTED"));
    }
    for value in [
        &profile.options.host,
        &profile.options.path,
        &profile.options.service_name,
    ]
    .into_iter()
    .flatten()
    {
        if value.len() > 4096 || value.bytes().any(|b| b == b'\r' || b == b'\n') {
            return Err(invalid("VLESS_HTTP_PARAMETER_INVALID"));
        }
    }
    if let Some(options) = &profile.options.vless_transport {
        if let Some(mode) = &options.mode {
            if transport != "xhttp"
                || !matches!(
                    mode.as_str(),
                    "auto" | "packet-up" | "stream-up" | "stream-one"
                )
            {
                return Err(invalid("VLESS_XHTTP_MODE_INVALID"));
            }
        }
        if options.multi_mode && transport != "grpc" {
            return Err(invalid("VLESS_GRPC_MODE_INVALID"));
        }
        if let Some(download) = &options.download {
            if transport != "xhttp"
                || kind(download) != "xhttp"
                || download
                    .options
                    .vless_transport
                    .as_ref()
                    .is_some_and(|x| x.download.is_some())
            {
                return Err(invalid("VLESS_XHTTP_DOWNLOAD_INVALID"));
            }
            validate(download)?;
        }
        for (name, value) in &options.headers {
            let name = http::header::HeaderName::from_bytes(name.as_bytes())
                .map_err(|_| invalid("VLESS_HEADER_INVALID"))?;
            http::HeaderValue::from_str(value).map_err(|_| invalid("VLESS_HEADER_INVALID"))?;
            if matches!(
                name.as_str(),
                "authorization"
                    | "proxy-authorization"
                    | "connection"
                    | "upgrade"
                    | "content-length"
                    | "host"
                    | "transfer-encoding"
            ) {
                return Err(invalid("VLESS_HEADER_RESERVED"));
            }
        }
    }
    Ok(())
}
struct AbortTask(tokio::task::JoinHandle<()>);
impl Drop for AbortTask {
    fn drop(&mut self) {
        self.0.abort();
    }
}
struct ManagedStream {
    inner: tokio::io::DuplexStream,
    _task: AbortTask,
}
impl AsyncRead for ManagedStream {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        out: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        Pin::new(&mut self.inner).poll_read(cx, out)
    }
}
impl AsyncWrite for ManagedStream {
    fn poll_write(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        b: &[u8],
    ) -> Poll<io::Result<usize>> {
        Pin::new(&mut self.inner).poll_write(cx, b)
    }
    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.inner).poll_flush(cx)
    }
    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.inner).poll_shutdown(cx)
    }
}
fn managed<F, Fut>(run: F) -> Box<dyn RelayStream>
where
    F: FnOnce(tokio::io::DuplexStream) -> Fut + Send + 'static,
    Fut: std::future::Future<Output = io::Result<()>> + Send + 'static,
{
    let (inner, bridge) = tokio::io::duplex(64 * 1024);
    let task = tokio::spawn(async move {
        if run(bridge).await.is_err() {
            crate::trace::code("transport", "VLESS_CARRIER_FAILED");
        }
    });
    Box::new(ManagedStream {
        inner,
        _task: AbortTask(task),
    })
}
pub fn vless_stream(mut remote: Box<dyn RelayStream>, request: Vec<u8>) -> Box<dyn RelayStream> {
    managed(move |bridge| async move {
        remote.write_all(&request).await?;
        remote.flush().await?;
        let (mut input, mut output) = tokio::io::split(bridge);
        let (mut read, mut write) = tokio::io::split(remote);
        let up = async {
            tokio::io::copy(&mut input, &mut write).await?;
            write.shutdown().await
        };
        let down = async {
            vless_codec::read_response_header(&mut read).await?;
            tokio::io::copy(&mut read, &mut output).await?;
            output.shutdown().await
        };
        tokio::try_join!(up, down)?;
        Ok(())
    })
}
#[derive(Clone)]
struct H2Carrier {
    sender: h2::client::SendRequest<Bytes>,
    _driver: Arc<AbortTask>,
}
#[derive(Default)]
pub struct Runtime {
    pool: tokio::sync::Mutex<HashMap<String, H2Carrier>>,
}
impl Runtime {
    async fn h2(&self, profile: &WireProfile, dial: &Dial) -> io::Result<H2Carrier> {
        // Profile belongs to the immutable connector snapshot; credentials never enter logs.
        let key = serde_json::to_string(profile).map_err(io::Error::other)?;
        let mut pool = self.pool.lock().await;
        if let Some(entry) = pool.get(&key) {
            if entry.sender.clone().ready().await.is_ok() {
                return Ok(entry.clone());
            }
        }
        let io = dial(profile.clone()).await?;
        let (sender, connection) = h2::client::handshake(io).await.map_err(io::Error::other)?;
        let entry = H2Carrier {
            sender,
            _driver: Arc::new(AbortTask(tokio::spawn(async move {
                let _ = connection.await;
            }))),
        };
        pool.insert(key, entry.clone());
        Ok(entry)
    }
    pub async fn connect(
        self: &Arc<Self>,
        profile: WireProfile,
        dial: Dial,
    ) -> io::Result<Box<dyn RelayStream>> {
        validate(&profile)?;
        match kind(&profile) {
            "raw" => dial(profile).await,
            "ws" => {
                use tokio_tungstenite::tungstenite::{client::IntoClientRequest, Message};
                let remote = dial(profile.clone()).await?;
                let host = authority(&profile);
                let path = path(&profile);
                let mut req = format!("wss://{host}{path}")
                    .into_client_request()
                    .map_err(io::Error::other)?;
                if let Some(options) = &profile.options.vless_transport {
                    for (k, v) in &options.headers {
                        req.headers_mut().insert(
                            http::HeaderName::from_bytes(k.as_bytes()).map_err(io::Error::other)?,
                            v.parse().map_err(io::Error::other)?,
                        );
                    }
                }
                let (ws, _) = tokio_tungstenite::client_async(req, remote)
                    .await
                    .map_err(|_|invalid("VLESS_WS_HANDSHAKE_FAILED"))?;
                Ok(managed(move |bridge| async move {
                    let (mut input, mut output) = tokio::io::split(bridge);
                    let (mut sink, mut source) = ws.split();
                    let up = async {
                        let mut b = vec![0; 16 * 1024];
                        loop {
                            let n = input.read(&mut b).await?;
                            if n == 0 {
                                sink.close().await.map_err(io::Error::other)?;
                                return Ok::<_, io::Error>(());
                            }
                            sink.send(Message::Binary(Bytes::copy_from_slice(&b[..n])))
                                .await
                                .map_err(io::Error::other)?;
                        }
                    };
                    let down = async {
                        while let Some(message) = source.next().await {
                            match message.map_err(io::Error::other)? {
                                Message::Binary(data) => output.write_all(&data).await?,
                                Message::Close(_) => break,
                                Message::Text(_) => {
                                    return Err(invalid("VLESS_WS_TEXT_UNEXPECTED"))
                                }
                                _ => (),
                            }
                        }
                        output.shutdown().await
                    };
                    tokio::try_join!(up, down)?;
                    Ok(())
                }))
            }
            "httpupgrade" => {
                let mut remote = dial(profile.clone()).await?;
                let host = authority(&profile);
                let path = path(&profile);
                let mut header=format!("GET {path} HTTP/1.1\r\nHost: {host}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n");
                if let Some(options) = &profile.options.vless_transport {
                    for (k, v) in &options.headers {
                        header.push_str(&format!("{k}: {v}\r\n"));
                    }
                }
                header.push_str("\r\n");
                remote.write_all(header.as_bytes()).await?;
                let mut response = Vec::new();
                while !response.ends_with(b"\r\n\r\n") {
                    if response.len() >= 16384 {
                        return Err(invalid("VLESS_UPGRADE_HEADER_TOO_LARGE"));
                    }
                    response.push(remote.read_u8().await?);
                }
                let text = std::str::from_utf8(&response).map_err(io::Error::other)?;
                if !text.starts_with("HTTP/1.1 101 ")
                    || !text
                        .lines()
                        .any(|l| l.eq_ignore_ascii_case("upgrade: websocket"))
                {
                    return Err(invalid("VLESS_UPGRADE_REJECTED"));
                }
                Ok(remote)
            }
            "grpc" => {
                let carrier = self.h2(&profile, &dial).await?;
                let multi = profile
                    .options
                    .vless_transport
                    .as_ref()
                    .is_some_and(|o| o.multi_mode);
                let service = profile.options.service_name.as_deref().unwrap_or("");
                let route = if service.starts_with('/') {
                    service.to_owned()
                } else {
                    format!("/{service}/{}", if multi { "TunMulti" } else { "Tun" })
                };
                let mut request = http::Request::builder()
                    .method("POST")
                    .uri(format!("https://{}{route}", authority(&profile)))
                    .header("content-type", "application/grpc")
                    .header("te", "trailers");
                if let Some(options)=&profile.options.vless_transport{for(key,value)in &options.headers{request=request.header(key,value);}}
                let request=request.body(()).map_err(io::Error::other)?;
                let (mut response, send) = carrier
                    .sender
                    .clone()
                    .ready()
                    .await
                    .map_err(io::Error::other)?
                    .send_request(request, false)
                    .map_err(io::Error::other)?;
                Ok(managed(move |bridge| async move {
                    let _keep = carrier;
                    let (mut input, mut output) = tokio::io::split(bridge);
                    let up = async {
                        let mut send = send;
                        let mut b = vec![0; 16 * 1024];
                        loop {
                            let n = input.read(&mut b).await?;
                            if n == 0 {
                                return send
                                    .send_data(Bytes::new(), true)
                                    .map_err(io::Error::other);
                            }
                            h2_send(&mut send, grpc_encode(&b[..n], multi), false).await?;
                        }
                    };
                    let down = async {
                        let reply = (&mut response).await.map_err(io::Error::other)?;
                        if !reply.status().is_success() {
                            return Err(invalid("VLESS_GRPC_STATUS"));
                        }
                        let mut body = reply.into_body();
                        let mut pending = BytesMut::new();
                        while let Some(data) = body.data().await {
                            let data = data.map_err(io::Error::other)?;
                            pending.extend_from_slice(&data);
                            body.flow_control()
                                .release_capacity(data.len())
                                .map_err(io::Error::other)?;
                            while let Some(payload) = grpc_decode(&mut pending, multi)? {
                                output.write_all(&payload).await?;
                            }
                        }
                        if !pending.is_empty() {
                            return Err(invalid("VLESS_GRPC_TRUNCATED"));
                        }
                        if let Some(trailers) = body.trailers().await.map_err(io::Error::other)? {
                            if trailers.get("grpc-status").is_some_and(|s| s != "0") {
                                return Err(invalid("VLESS_GRPC_STATUS"));
                            }
                        }
                        output.shutdown().await
                    };
                    tokio::try_join!(up, down)?;
                    Ok(())
                }))
            }
            "xhttp" => self.xhttp(profile, dial).await,
            _ => Err(invalid("VLESS_TRANSPORT_UNSUPPORTED")),
        }
    }
    async fn xhttp(
        self: &Arc<Self>,
        profile: WireProfile,
        dial: Dial,
    ) -> io::Result<Box<dyn RelayStream>> {
        let options = profile.options.vless_transport.clone().unwrap_or_default();
        let requested = options.mode.as_deref().unwrap_or("auto");
        let mode = if requested == "auto" {
            if profile.protocol == "vless_reality" && options.download.is_none() {
                "stream-one"
            } else {
                "stream-up"
            }
        } else {
            requested
        };
        if mode == "stream-one" && options.download.is_some() {
            return Err(invalid("VLESS_XHTTP_DOWNLOAD_REQUIRES_SPLIT"));
        }
        let carrier = self.h2(&profile, &dial).await?;
        let base = format!("{}/", path(&profile).trim_end_matches('/'));
        let session = uuid::Uuid::new_v4().to_string();
        let download = options.download.as_deref().unwrap_or(&profile);
        let down_carrier = if mode == "stream-one" {
            carrier.clone()
        } else {
            self.h2(download, &dial).await?
        };
        let route = if mode == "stream-one" {
            base.clone()
        } else {
            format!("{base}{session}")
        };
        let mut upload_response = None;
        let (down_response, upload) = if mode == "stream-one" {
            let request = xhttp_request(&profile, "POST", &route, true)?;
            let (response, send) = carrier
                .sender
                .clone()
                .ready()
                .await
                .map_err(io::Error::other)?
                .send_request(request, false)
                .map_err(io::Error::other)?;
            (response, Some(send))
        } else {
            let down_path = format!("{}/{session}", path(download).trim_end_matches('/'));
            let (response, _) = down_carrier
                .sender
                .clone()
                .ready()
                .await
                .map_err(io::Error::other)?
                .send_request(xhttp_request(download, "GET", &down_path, false)?, true)
                .map_err(io::Error::other)?;
            let send = if mode == "stream-up" {
                let (response, send) = carrier
                    .sender
                    .clone()
                    .ready()
                    .await
                    .map_err(io::Error::other)?
                    .send_request(xhttp_request(&profile, "POST", &route, true)?, false)
                    .map_err(io::Error::other)?;
                upload_response = Some(response);
                Some(send)
            } else {
                None
            };
            (response, send)
        };
        let packet_up = mode == "packet-up";
        Ok(managed(move |bridge| async move {
            let _keep = down_carrier;
            let (mut input, mut output) = tokio::io::split(bridge);
            let up = async {
                let mut upload = upload;
                let mut seq = 0;
                let mut responses = tokio::task::JoinSet::<io::Result<()>>::new();
                if let Some(response)=upload_response { responses.spawn(check_upload(response)); }
                let mut b = vec![0; 16 * 1024];
                loop {
                    let n = tokio::select!{
                        result=input.read(&mut b)=>result?,
                        result=responses.join_next(),if !responses.is_empty()=>{result.unwrap().map_err(io::Error::other)??;continue;}
                    };
                    if n == 0 {
                        if let Some(send) = upload.as_mut() {
                            send.send_data(Bytes::new(), true)
                                .map_err(io::Error::other)?;
                        }
                        while let Some(result)=responses.join_next().await{result.map_err(io::Error::other)??;}
                        return Ok::<_, io::Error>(());
                    }
                    if packet_up {
                        let request =
                            xhttp_request(&profile, "POST", &format!("{route}/{seq}"), false)?;
                        let (response, mut send) = carrier
                            .sender
                            .clone()
                            .ready()
                            .await
                            .map_err(io::Error::other)?
                            .send_request(request, false)
                            .map_err(io::Error::other)?;
                        h2_send(&mut send, Bytes::copy_from_slice(&b[..n]), true).await?;
                        // Bodies remain ordered; acknowledgements overlap within a bounded queue.
                        responses.spawn(check_upload(response));
                        if responses.len()>=8{responses.join_next().await.unwrap().map_err(io::Error::other)??;}
                        seq += 1;
                    } else {
                        h2_send(
                            upload.as_mut().unwrap(),
                            Bytes::copy_from_slice(&b[..n]),
                            false,
                        )
                        .await?;
                    }
                    while let Some(result)=responses.try_join_next(){result.map_err(io::Error::other)??;}
                }
            };
            let down = async {
                let reply = down_response.await.map_err(io::Error::other)?;
                if !reply.status().is_success() {
                    return Err(invalid("VLESS_XHTTP_DOWNLOAD_STATUS"));
                }
                let mut body = reply.into_body();
                while let Some(data) = body.data().await {
                    let data = data.map_err(io::Error::other)?;
                    output.write_all(&data).await?;
                    body.flow_control()
                        .release_capacity(data.len())
                        .map_err(io::Error::other)?;
                }
                output.shutdown().await
            };
            tokio::try_join!(up, down)?;
            Ok(())
        }))
    }
}
async fn check_upload(response:h2::client::ResponseFuture)->io::Result<()>{let reply=response.await.map_err(io::Error::other)?;if !reply.status().is_success(){return Err(invalid("VLESS_XHTTP_UPLOAD_STATUS"));}let mut body=reply.into_body();while let Some(data)=body.data().await{let data=data.map_err(io::Error::other)?;body.flow_control().release_capacity(data.len()).map_err(io::Error::other)?;}Ok(())}
fn authority(p: &WireProfile) -> String {
    p.options
        .host
        .clone()
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| p.server_name.clone())
}
fn path(p: &WireProfile) -> String {
    let value = p.options.path.as_deref().unwrap_or("/");
    if value.starts_with('/') {
        value.into()
    } else {
        format!("/{value}")
    }
}
fn xhttp_request(
    p: &WireProfile,
    method: &str,
    path: &str,
    stream: bool,
) -> io::Result<http::Request<()>> {
    let host = authority(p);
    let mut builder = http::Request::builder()
        .method(method)
        .uri(format!("https://{host}{path}"))
        .header(
            "referer",
            format!("https://{host}{path}?x_padding={}", "X".repeat(128)),
        );
    if stream {
        builder = builder.header("content-type", "application/grpc");
    }
    if let Some(options) = &p.options.vless_transport {
        for (k, v) in &options.headers {
            builder = builder.header(k, v);
        }
    }
    builder.body(()).map_err(io::Error::other)
}
async fn h2_send(send: &mut h2::SendStream<Bytes>, mut bytes: Bytes, end: bool) -> io::Result<()> {
    while !bytes.is_empty() {
        send.reserve_capacity(bytes.len());
        let capacity = futures::future::poll_fn(|cx| send.poll_capacity(cx))
            .await
            .ok_or(io::ErrorKind::BrokenPipe)?
            .map_err(io::Error::other)?;
        if capacity == 0 {
            continue;
        }
        let n = capacity.min(bytes.len());
        let chunk = bytes.split_to(n);
        send.send_data(chunk, end && bytes.is_empty())
            .map_err(io::Error::other)?;
    }
    Ok(())
}
fn varint(mut value: usize, out: &mut Vec<u8>) {
    while value >= 128 {
        out.push((value as u8) | 128);
        value >>= 7;
    }
    out.push(value as u8);
}
fn grpc_encode(data: &[u8], _multi: bool) -> Bytes {
    let mut body = Vec::new();
    body.push(0x0a);
    varint(data.len(), &mut body);
    body.extend_from_slice(data);
    let mut frame = vec![0];
    frame.extend_from_slice(&(body.len() as u32).to_be_bytes());
    frame.extend(body);
    frame.into()
}
fn proto_bytes(mut bytes: &[u8]) -> io::Result<Vec<u8>> {
    let mut out = Vec::new();
    while !bytes.is_empty() {
        if bytes[0] != 0x0a {
            return Err(invalid("VLESS_GRPC_PROTO_INVALID"));
        }
        bytes = &bytes[1..];
        let mut length = 0usize;
        let mut shift = 0;
        loop {
            let b = *bytes
                .first()
                .ok_or_else(|| invalid("VLESS_GRPC_PROTO_INVALID"))?;
            bytes = &bytes[1..];
            length |= ((b & 127) as usize) << shift;
            if b < 128 {
                break;
            }
            shift += 7;
            if shift >= 28 {
                return Err(invalid("VLESS_GRPC_PROTO_INVALID"));
            }
        }
        if length > bytes.len() {
            return Err(invalid("VLESS_GRPC_PROTO_INVALID"));
        }
        out.extend_from_slice(&bytes[..length]);
        bytes = &bytes[length..];
    }
    Ok(out)
}
fn grpc_decode(pending: &mut BytesMut, _multi: bool) -> io::Result<Option<Vec<u8>>> {
    if pending.len() < 5 {
        return Ok(None);
    }
    if pending[0] != 0 {
        return Err(invalid("VLESS_GRPC_COMPRESSION_UNSUPPORTED"));
    }
    let length = u32::from_be_bytes(pending[1..5].try_into().unwrap()) as usize;
    if length > 1024 * 1024 {
        return Err(invalid("VLESS_GRPC_FRAME_TOO_LARGE"));
    }
    if pending.len() < 5 + length {
        return Ok(None);
    }
    pending.advance(5);
    let bytes = pending.split_to(length);
    let data = proto_bytes(&bytes)?;
    Ok(Some(data))
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn grpc_frames_roundtrip_fragmented() {
        for multi in [false, true] {
            let payload = vec![7; 65536];
            let encoded = grpc_encode(&payload, multi);
            let mut pending = BytesMut::new();
            for b in &encoded[..encoded.len() - 1] {
                pending.extend_from_slice(&[*b]);
                assert!(grpc_decode(&mut pending, multi).unwrap().is_none());
            }
            pending.extend_from_slice(&encoded[encoded.len() - 1..]);
            assert_eq!(grpc_decode(&mut pending, multi).unwrap().unwrap(), payload);
            assert!(pending.is_empty());
        }
    }
}
