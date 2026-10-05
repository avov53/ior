//! Private inherited stdin/stdout control protocol. No credentials are echoed.
use crate::{
    hysteria2::Hysteria2Connector, ipc_contract::StartRequest, proxy_inbound,
    vless_tls::VlessTlsConnector,
};
use serde::{Deserialize, Serialize};
use std::{io, sync::Arc};
use tokio::io::{AsyncBufRead, AsyncBufReadExt, AsyncReadExt, AsyncWrite, AsyncWriteExt};

const MAX_FRAME: usize = 8 * 1024 * 1024;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Command {
    api_version: u16,
    request_id: String,
    operation: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Event<'a> {
    api_version: u16,
    request_id: &'a str,
    operation: &'a str,
    code: &'a str,
    stage: &'a str,
    tcp: bool,
    udp: bool,
    tun: bool,
}

async fn read_frame(reader: &mut (impl AsyncBufRead + Unpin)) -> io::Result<Vec<u8>> {
    let mut frame = Vec::new();
    // Limit memory even when a sender never terminates a frame.
    let count = reader
        .take((MAX_FRAME + 1) as u64)
        .read_until(b'\n', &mut frame)
        .await?;
    if count == 0 {
        return Err(io::ErrorKind::UnexpectedEof.into());
    }
    if count > MAX_FRAME || frame.last() != Some(&b'\n') {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "control frame exceeds limit or is incomplete",
        ));
    }
    Ok(frame)
}

async fn emit(
    writer: &mut (impl AsyncWrite + Unpin),
    id: &str,
    operation: &str,
    code: &str,
) -> io::Result<()> {
    emit_runtime(writer, id, operation, code, false, false).await
}
async fn emit_runtime(
    writer: &mut (impl AsyncWrite + Unpin),
    id: &str,
    operation: &str,
    code: &str,
    udp: bool,
    tun: bool,
) -> io::Result<()> {
    let event = Event {
        api_version: crate::ipc_contract::API_VERSION,
        request_id: id,
        operation,
        code,
        stage: if code.starts_with("HYSTERIA2") {
            "handshake"
        } else {
            "control"
        },
        tcp: matches!(operation, "ready" | "status"),
        udp,
        tun,
    };
    let mut bytes = serde_json::to_vec(&event).map_err(io::Error::other)?;
    bytes.push(b'\n');
    writer.write_all(&bytes).await?;
    writer.flush().await
}

pub async fn serve(
    reader: &mut (impl AsyncBufRead + Unpin),
    writer: &mut (impl AsyncWrite + Unpin),
    port: u16,
) -> io::Result<()> {
    if port == 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "invalid local port",
        ));
    }
    let frame = read_frame(reader).await?;
    let request: StartRequest = serde_json::from_slice(&frame).map_err(|_| {
        io::Error::new(io::ErrorKind::InvalidInput, "invalid private start payload")
    })?;
    request
        .validate_shape()
        .map_err(|message| io::Error::new(io::ErrorKind::InvalidInput, message))?;
    crate::trace::set_run(&request.request_id);
    if !matches!(
        request.profile.protocol.as_str(),
        "vless_tls"
            | "vless_reality"
            | "hysteria2"
            | "vmess"
            | "shadowsocks"
            | "trojan"
            | "wireguard"
    ) {
        emit(
            writer,
            &request.request_id,
            "error",
            "TRANSPORT_OR_ROUTE_NOT_IMPLEMENTED",
        )
        .await?;
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "native preview requires a supported TCP transport with MATCH PROXY",
        ));
    }
    let bootstrap = request
        .dns
        .bootstrap
        .parse()
        .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "invalid bootstrap DNS IP"))?;
    let bootstrap = std::net::SocketAddr::new(bootstrap, 53);
    if request.operation == "validate" {
        // Compile routes and validate credentials without starting any network transport.
        if matches!(request.profile.protocol.as_str(), "wireguard") {
            crate::tunnel_outbound::TunnelConnector::validate_profile(&request.profile)?;
        } else if matches!(
            request.profile.protocol.as_str(),
            "vmess" | "shadowsocks" | "trojan"
        ) {
            let _ = crate::native_protocols::NativeProtocolConnector::new(
                &request.profile,
                bootstrap,
                request.network.clone(),
            )?;
        } else if request.profile.protocol == "vless_reality" {
            let _ = crate::vless_reality::RealityConnector::new(&request.profile, bootstrap)?;
        } else if request.profile.protocol == "vless_tls" {
            let _ = VlessTlsConnector::from_profile(&request.profile, bootstrap)?;
        }
        struct ValidationOnly;
        impl proxy_inbound::OutboundConnector for ValidationOnly {
            fn connect(&self, _: String, _: u16) -> proxy_inbound::ConnectFuture {
                Box::pin(async { Err(io::ErrorKind::Unsupported.into()) })
            }
        }
        let router = Arc::new(crate::native_router::NativeRouter::new(
            Arc::new(ValidationOnly),
            request.routes.clone(),
            bootstrap,
            request.network.clone(),
        )?);
        let _ = crate::tun_dns::TunDns::new(router, &request.dns)?;
        emit_runtime(
            writer,
            &request.request_id,
            "validated",
            "PROFILE_VALIDATED",
            false,
            false,
        )
        .await?;
        return Ok(());
    }
    crate::trace::emit(
        serde_json::json!({"stage":"transport","code":"NATIVE_PROTOCOL_SELECTED","protocol":request.profile.protocol,"implementation":"ior-core-rust"}),
    );
    let build_connector = async {
        let connector: Arc<dyn proxy_inbound::OutboundConnector> =
            if request.profile.protocol == "hysteria2" {
                match Hysteria2Connector::from_profile_bound(
                    &request.profile,
                    bootstrap,
                    request.network.clone(),
                )
                .await
                {
                    Ok(connector) => Arc::new(connector),
                    Err(error) => {
                        let code = if error.kind() == io::ErrorKind::TimedOut {
                            "HYSTERIA2_HANDSHAKE_TIMEOUT"
                        } else {
                            "HYSTERIA2_HANDSHAKE_FAILED"
                        };
                        emit(writer, &request.request_id, "error", code).await?;
                        return Err(error);
                    }
                }
            } else if matches!(request.profile.protocol.as_str(), "wireguard") {
                Arc::new(
                    crate::tunnel_outbound::TunnelConnector::new(
                        &request.profile,
                        bootstrap,
                        request.network.clone(),
                    )
                    .await?
                    .with_upstreams(if request.dns.upstreams.is_empty() {
                        vec![std::net::SocketAddr::new(
                            request
                                .dns
                                .upstream
                                .parse()
                                .map_err(|_| io::ErrorKind::InvalidInput)?,
                            53,
                        )]
                    } else {
                        request
                            .dns
                            .upstreams
                            .iter()
                            .map(|value| {
                                value
                                    .parse()
                                    .map(|ip| std::net::SocketAddr::new(ip, 53))
                                    .map_err(|_| io::ErrorKind::InvalidInput)
                            })
                            .collect::<Result<Vec<_>, _>>()?
                    })?,
                )
            } else if matches!(
                request.profile.protocol.as_str(),
                "vmess" | "shadowsocks" | "trojan"
            ) {
                Arc::new(crate::native_protocols::NativeProtocolConnector::new(
                    &request.profile,
                    bootstrap,
                    request.network.clone(),
                )?)
            } else if request.profile.protocol == "vless_reality" {
                Arc::new(
                    crate::vless_reality::RealityConnector::new(&request.profile, bootstrap)?
                        .with_network(request.network.clone()),
                )
            } else {
                Arc::new(VlessTlsConnector::from_profile(
                    &request.profile,
                    bootstrap,
                )?)
            };
        Ok::<_, io::Error>(connector)
    };
    let connector = match build_connector.await {
        Ok(connector) => connector,
        Err(error) => {
            let text = error.to_string();
            let code = if text.len() <= 80
                && text
                    .bytes()
                    .all(|b| b.is_ascii_uppercase() || b.is_ascii_digit() || b == b'_')
            {
                text.as_str()
            } else {
                "TRANSPORT_INITIALIZATION_FAILED"
            };
            crate::trace::code("transport", code);
            emit(writer, &request.request_id, "error", code).await?;
            return Err(error);
        }
    };
    let connector = Arc::new(crate::native_router::NativeRouter::new(
        connector,
        request.routes.clone(),
        bootstrap,
        request.network.clone(),
    )?);
    let listener = tokio::net::TcpListener::bind(("127.0.0.1", port)).await?;
    let mut tun_task = if let Some(options) = &request.tun {
        Some(crate::tun::start(options, connector.clone(), &request.dns).await?)
    } else {
        None
    };
    let mut ingress = tokio::spawn(proxy_inbound::serve_local_authenticated(
        listener,
        connector.clone(),
        request.ingress_auth.clone(),
    ));
    let result = async {
        emit_runtime(
            writer,
            &request.request_id,
            "ready",
            "LOCAL_INGRESS_READY",request.profile.protocol != "vless_tls",request.tun.is_some(),
        )
        .await?;
        loop {
            let frame = tokio::select! {
                frame = read_frame(reader) => frame,
                result = &mut ingress => return result.map_err(io::Error::other)?,
                result = async {match &mut tun_task {Some(task)=>task.await,None=>std::future::pending().await}} => return result.map_err(io::Error::other)?,
            };
            let frame = match frame {
                Ok(frame) => frame,
                Err(error) if error.kind() == io::ErrorKind::UnexpectedEof => return Ok(()),
                Err(error) => return Err(error),
            };
            let command: Command = serde_json::from_slice(&frame).map_err(|_| {
                io::Error::new(io::ErrorKind::InvalidData, "invalid private command")
            })?;
            if !matches!(command.api_version,1|2|3)
                || command.request_id.is_empty()
                || command.request_id.len() > 64
            {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "invalid command version or identity",
                ));
            }
            match command.operation.as_str() {
                "probeDirect"=>{
                    let code=if crate::dns_engine::probe_direct(&connector).await.is_ok(){"DIRECT_TCP_UDP_VERIFIED"}else{"DIRECT_TCP_UDP_PROBE_FAILED"};
                    crate::trace::code("probe",code);
                    emit_runtime(writer,&command.request_id,"probeDirect",code,true,request.tun.is_some()).await?;
                },
                "status" => {
                    emit_runtime(
                        writer,
                        &command.request_id,
                        "status",
                        "LOCAL_INGRESS_READY",request.profile.protocol != "vless_tls",request.tun.is_some(),
                    )
                    .await?
                }
                "stop" => {
                    emit(writer, &command.request_id, "stopped", "STOPPED").await?;
                    return Ok(());
                }
                _ => {
                    emit(writer, &command.request_id, "error", "UNKNOWN_OPERATION").await?;
                }
            }
        }
    }
    .await;
    ingress.abort();
    if let Some(task) = tun_task {
        task.abort();
        let _ = task.await;
    }
    // Runtime exit closes accepted sockets; no external engine is launched.
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn tunnel_preflight_never_resolves_or_starts_the_adapter() {
        let mut request: StartRequest =
            serde_json::from_str(include_str!("../../../fixtures/start-v1.json")).unwrap();
        request.operation = "validate".into();
        request.profile.protocol = "wireguard".into();
        request.profile.server = "must-not-resolve.invalid".into();
        request.profile.credential = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=".into();
        request.profile.options.peer_public_key = Some(request.profile.credential.clone());
        request.profile.options.local_addresses = Some(vec!["10.9.0.2/32".into()]);
        request.profile.options.allowed_ips = Some(vec!["0.0.0.0/0".into()]);
        let mut bytes = serde_json::to_vec(&request).unwrap();
        bytes.push(b'\n');
        let mut input = tokio::io::BufReader::new(bytes.as_slice());
        let mut output = Vec::new();
        tokio::time::timeout(
            std::time::Duration::from_millis(500),
            serve(&mut input, &mut output, 1),
        )
        .await
        .unwrap()
        .unwrap();
        let response: serde_json::Value = serde_json::from_slice(&output).unwrap();
        assert_eq!(response["code"], "PROFILE_VALIDATED");
        assert_eq!(response["tun"], false);
        request.profile.options.local_addresses = Some(vec!["invalid/32".into()]);
        assert!(
            crate::tunnel_outbound::TunnelConnector::validate_profile(&request.profile).is_err()
        );
    }
    #[tokio::test]
    async fn oversized_unterminated_frame_is_rejected() {
        let bytes = vec![b'a'; MAX_FRAME + 1];
        let mut reader = tokio::io::BufReader::new(bytes.as_slice());
        assert_eq!(
            read_frame(&mut reader).await.unwrap_err().kind(),
            io::ErrorKind::InvalidData
        );
    }
    #[tokio::test]
    async fn events_never_contain_profile_credentials() {
        let mut output = Vec::new();
        emit(&mut output, "test-run", "ready", "LOCAL_TCP_INGRESS_READY")
            .await
            .unwrap();
        let event: serde_json::Value = serde_json::from_slice(&output).unwrap();
        assert_eq!(event["udp"], false);
        assert_eq!(event["tun"], false);
        assert!(event.get("profile").is_none());
    }
}
