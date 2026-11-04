//! Owns the pinned official AWG component using inherited packet pipes.
use crate::{
    ipc_contract::{WireNetwork, WireProfile},
    tunnel_outbound::{dispatch, key32},
};
use sha2::{Digest, Sha256};
use std::{
    io,
    net::{Ipv4Addr, Ipv6Addr, SocketAddr},
};
use tokio::{
    io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt},
    sync::mpsc,
};
pub fn validate_options(profile: &WireProfile) -> io::Result<()> {
    if profile.options.awg_engine.as_deref()==Some("rust"){return crate::awg_rust::validate(profile);}
    if profile
        .options
        .awg_adapter_path
        .as_deref()
        .is_none_or(|p| !std::path::Path::new(p).is_absolute())
        || profile
            .options
            .awg_adapter_sha256
            .as_deref()
            .is_none_or(|v| v.len() != 64 || !v.bytes().all(|b| b.is_ascii_hexdigit()))
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "AWG_ADAPTER_INVALID",
        ));
    }
    for (key, value) in profile
        .options
        .awg
        .as_ref()
        .ok_or(io::ErrorKind::InvalidInput)?
    {
        if key == "header_protection_key" {
            header_key_hex(value)?;
        }
        if !matches!(
            key.as_str(),
            "jc" | "jmin"
                | "jmax"
                | "s1"
                | "s2"
                | "s3"
                | "s4"
                | "h1"
                | "h2"
                | "h3"
                | "h4"
                | "i1"
                | "i2"
                | "i3"
                | "i4"
                | "i5"
                | "header_protection_key"
                | "content_padding_addition"
                | "rekey_after_time"
                | "rekey_timeout"
                | "reject_after_time"
                | "keepalive_timeout"
                | "max_handshake_attempts"
                | "random_trailers"
                | "disable_cookies"
        ) || value.contains(['\n', '\r'])
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "AWG_PARAMETER_UNSUPPORTED",
            ));
        }
    }
    Ok(())
}
pub(crate) async fn start(
    profile: &WireProfile,
    bootstrap: SocketAddr,
    network: Option<WireNetwork>,
    mut input: mpsc::Receiver<crate::packet_budget::Packet>,
    flows: crate::tunnel_outbound::Flows,
    local: Ipv4Addr,
    local6: Option<Ipv6Addr>,
) -> io::Result<tokio::task::JoinHandle<io::Result<()>>> {
    let path = profile
        .options
        .awg_adapter_path
        .as_deref()
        .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "AWG_ADAPTER_MISSING"))?;
    let expected = profile
        .options
        .awg_adapter_sha256
        .as_deref()
        .ok_or(io::ErrorKind::InvalidInput)?;
    let actual = format!("{:x}", Sha256::digest(tokio::fs::read(path).await?));
    if actual != expected.to_lowercase() {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "AWG_ADAPTER_HASH_MISMATCH",
        ));
    }
    let network = network.ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidInput,
            "AWG_PHYSICAL_INTERFACE_REQUIRED",
        )
    })?;
    let mut uapi = format!("private_key={}\n", hex(&key32(&profile.credential)?));
    for (key, value) in profile
        .options
        .awg
        .as_ref()
        .ok_or(io::ErrorKind::InvalidInput)?
    {
        if !matches!(
            key.as_str(),
            "jc" | "jmin"
                | "jmax"
                | "s1"
                | "s2"
                | "s3"
                | "s4"
                | "h1"
                | "h2"
                | "h3"
                | "h4"
                | "i1"
                | "i2"
                | "i3"
                | "i4"
                | "i5"
                | "header_protection_key"
                | "content_padding_addition"
                | "rekey_after_time"
                | "rekey_timeout"
                | "reject_after_time"
                | "keepalive_timeout"
                | "max_handshake_attempts"
                | "random_trailers"
                | "disable_cookies"
        ) || value.contains(['\n', '\r'])
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "AWG_PARAMETER_UNSUPPORTED",
            ));
        }
        let value = if key == "header_protection_key" {
            header_key_hex(value)?
        } else {
            value.clone()
        };
        uapi.push_str(&format!("{key}={value}\n"));
    }
    let endpoint = SocketAddr::new(
        crate::dns_engine::resolve_server_ip_bound(
            &profile.server,
            bootstrap,
            &Some(network.clone()),
        )
        .await?,
        profile.port,
    );
    uapi.push_str(&format!(
        "public_key={}\nendpoint={}\nreplace_allowed_ips=true\n",
        hex(&key32(
            profile
                .options
                .peer_public_key
                .as_deref()
                .ok_or(io::ErrorKind::InvalidInput)?
        )?),
        endpoint
    ));
    if let Some(psk) = profile
        .options
        .preshared_key
        .as_deref()
        .filter(|s| !s.is_empty())
    {
        uapi.push_str(&format!("preshared_key={}\n", hex(&key32(psk)?)));
    }
    if let Some(keepalive) = profile.options.keepalive {
        uapi.push_str(&format!("persistent_keepalive_interval={keepalive}\n"));
    }
    for ip in profile
        .options
        .allowed_ips
        .as_ref()
        .ok_or(io::ErrorKind::InvalidInput)?
    {
        if ip.contains(['\r', '\n']) {
            return Err(io::ErrorKind::InvalidInput.into());
        }
        uapi.push_str(&format!("allowed_ip={ip}\n"));
    }
    let mut command = tokio::process::Command::new(path);
    command
        .stdin(std::process::Stdio::piped())
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::piped())
        .kill_on_drop(true);
    #[cfg(windows)]
    command.creation_flags(0x08000000);
    let mut child = command.spawn()?;
    let mut writer = child.stdin.take().ok_or(io::ErrorKind::BrokenPipe)?;
    let mut reader = child.stdout.take().ok_or(io::ErrorKind::BrokenPipe)?;
    let mut errors =
        tokio::io::BufReader::new(child.stderr.take().ok_or(io::ErrorKind::BrokenPipe)?);
    let config = serde_json::json!({"api":2,"uapi":uapi,"interfaceIndex":network.interface_index,"interfaceIndex6":network.interface_index6.unwrap_or(0),"endpointV6":endpoint.is_ipv6(),"mtu":profile.options.mtu.unwrap_or(1380)});
    writer
        .write_all(
            serde_json::to_string(&config)
                .map_err(io::Error::other)?
                .as_bytes(),
        )
        .await?;
    writer.write_all(b"\n").await?;
    writer.flush().await?;
    let mut line = String::new();
    tokio::time::timeout(
        std::time::Duration::from_secs(10),
        errors.read_line(&mut line),
    )
    .await
    .map_err(|_| io::ErrorKind::TimedOut)??;
    if line.trim() != "AWG_ADAPTER_READY" {
        return Err(io::Error::new(
            io::ErrorKind::ConnectionAborted,
            "AWG_ADAPTER_START_FAILED",
        ));
    }
    Ok(tokio::spawn(async move {
        struct EndFlows(crate::tunnel_outbound::Flows);
        impl Drop for EndFlows {
            fn drop(&mut self) {
                if let Ok(mut flows) = self.0.lock() {
                    flows.clear();
                }
            }
        }
        let _end_flows = EndFlows(flows.clone());
        let mut tasks = tokio::task::JoinSet::new();
        tasks.spawn(async move {
            while let Some(packet) = input.recv().await {
                crate::tunnel_metrics::pipe_queue(packet.created());
                let started = std::time::Instant::now();
                writer.write_u32(packet.len() as u32).await?;
                writer.write_all(&packet).await?;
                writer.flush().await?;
                crate::tunnel_metrics::pipe_write(started);
            }
            Ok::<(), io::Error>(())
        });
        tasks.spawn(async move {
            let mut reassembly = crate::tunnel_fragments::Reassembly::default();
            loop {
                let length = reader.read_u32().await? as usize;
                if !(20..=65535).contains(&length) {
                    return Err(io::ErrorKind::InvalidData.into());
                }
                let mut packet = vec![0; length];
                reader.read_exact(&mut packet).await?;
                match reassembly.accept(&packet) {
                    Ok(Some(packet)) => dispatch(&flows, local, local6, &packet).await?,
                    Ok(None) => (),
                    Err(_) => crate::trace::code("tunnel", "TUNNEL_FRAGMENT_REJECTED"),
                }
            }
        });
        tasks.spawn(async move {
            let mut traffic_report = std::time::Instant::now();
            loop {
                let mut line = String::new();
                if errors.read_line(&mut line).await? == 0 {
                    return Err(io::ErrorKind::BrokenPipe.into());
                }
                if let Ok(event) = serde_json::from_str::<serde_json::Value>(&line) {
                    if matches!(
                        event["code"].as_str(),
                        Some("AWG_HANDSHAKE_AUTHENTICATED" | "AWG_TRAFFIC_METRICS")
                    ) {
                        // Drain every adapter message, but don't flood the GUI at 4 Hz.
                        if event["code"] == "AWG_TRAFFIC_METRICS" {
                            if traffic_report.elapsed() < std::time::Duration::from_secs(1) {
                                continue;
                            }
                            traffic_report = std::time::Instant::now();
                        }
                        let mut safe =
                            serde_json::json!({"stage":"transport","code":event["code"]});
                        for key in ["elapsedMs", "sentBytes", "receivedBytes"] {
                            if let Some(value) = event[key].as_u64() {
                                safe[key] = value.into();
                            }
                        }
                        if event["code"] == "AWG_HANDSHAKE_AUTHENTICATED" {
                            if let Some(value) = event["elapsedMs"].as_u64() {
                                safe["durationMs"] = value.into();
                            }
                        }
                        crate::trace::emit(safe);
                        continue;
                    }
                }
                crate::trace::code("transport", "AWG_ENGINE_IO_ERROR");
            }
        });
        tokio::select! {result=tasks.join_next()=>{let result=result.ok_or(io::ErrorKind::BrokenPipe)?.map_err(io::Error::other)?;let _=child.kill().await;result},status=child.wait()=>{let _=status?;Err(io::ErrorKind::ConnectionAborted.into())}}
    }))
}
fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}
fn header_key_hex(value: &str) -> io::Result<String> {
    if value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Ok(value.to_ascii_lowercase());
    }
    key32(value).map(|bytes| hex(&bytes)).map_err(|_| {
        io::Error::new(
            io::ErrorKind::InvalidInput,
            "AWG_HEADER_PROTECTION_KEY_INVALID",
        )
    })
}
#[cfg(test)]
mod tests {
    use super::*;
    use base64::Engine;
    #[test]
    fn header_protection_key_is_converted_without_changing_key_bytes() {
        let bytes = [9; 32];
        let expected = hex(&bytes);
        assert_eq!(
            header_key_hex(&base64::engine::general_purpose::STANDARD.encode(bytes)).unwrap(),
            expected
        );
        assert_eq!(header_key_hex(&expected.to_uppercase()).unwrap(), expected);
        assert!(header_key_hex("invalid").is_err());
    }
}
