use crate::core_api::{Protocol, ServerProfile};

pub fn parse_link(input: &str) -> Result<ServerProfile, String> {
    let input = input.trim();
    if input.starts_with("[Interface]") {
        return parse_wireguard_config(input);
    }
    let (scheme, rest) = input
        .split_once("://")
        .ok_or("Profile link has no scheme")?;
    let protocol = match scheme.to_ascii_lowercase().as_str() {
        "vless" => Protocol::Vless,
        "vmess" => Protocol::Vmess,
        "trojan" => Protocol::Trojan,
        "ss" => Protocol::Shadowsocks,
        "hysteria2" | "hy2" => Protocol::Hysteria2,
        "wireguard" => Protocol::WireGuard,
        _ => return Err(format!("Unsupported protocol: {scheme}")),
    };
    if protocol == Protocol::Vmess {
        return parse_vmess(rest);
    }
    if matches!(protocol, Protocol::WireGuard) {
        let encoded = rest.split('#').next().unwrap_or(rest);
        let text = String::from_utf8(
            decode_base64(encoded).ok_or("Invalid tunnel configuration encoding")?,
        )
        .map_err(|_| "Invalid tunnel configuration text")?;
        let profile = parse_wireguard_config(&text)?;
        return Ok(profile);
    }
    let (rest, name) = rest.split_once('#').unwrap_or((rest, ""));
    let (authority, query) = rest.split_once('?').unwrap_or((rest, ""));
    let authority = if protocol == Protocol::Shadowsocks && !authority.contains('@') {
        decode_base64(authority)
            .and_then(|bytes| String::from_utf8(bytes).ok())
            .ok_or("Invalid Shadowsocks credential")?
    } else {
        authority.to_string()
    };
    let (credential, endpoint) = authority
        .rsplit_once('@')
        .ok_or("Profile link has no credentials")?;
    let (server, port) = parse_endpoint(endpoint)?;
    let credential = if protocol == Protocol::Shadowsocks && !credential.contains(':') {
        decode_base64(credential)
            .and_then(|bytes| String::from_utf8(bytes).ok())
            .ok_or("Invalid Shadowsocks credential")?
    } else {
        credential.to_string()
    };
    if credential.trim().is_empty() {
        return Err("Profile credential is empty".into());
    }
    let server_name = query_value(query, "sni").or_else(|| query_value(query, "peer"));
    Ok(ServerProfile {
        protocol,
        name: percent_decode(name).unwrap_or_else(|| server.clone()),
        server,
        port,
        credential,
        server_name,
    })
}

fn parse_endpoint(endpoint: &str) -> Result<(String, u16), String> {
    let (host, port) = if endpoint.starts_with('[') {
        let close = endpoint.find(']').ok_or("Invalid IPv6 address")?;
        (
            &endpoint[1..close],
            endpoint[close + 1..]
                .strip_prefix(':')
                .ok_or("Port is missing")?,
        )
    } else {
        endpoint.rsplit_once(':').ok_or("Port is missing")?
    };
    let port = port
        .parse::<u16>()
        .map_err(|_| "Invalid port".to_string())?;
    if host.is_empty() || port == 0 {
        return Err("Host or port is empty".into());
    }
    Ok((host.to_string(), port))
}

fn query_value(query: &str, key: &str) -> Option<String> {
    query
        .split('&')
        .filter_map(|pair| pair.split_once('='))
        .find(|(name, _)| name.eq_ignore_ascii_case(key))
        .and_then(|(_, value)| percent_decode(value))
}

fn percent_decode(value: &str) -> Option<String> {
    let mut result = Vec::new();
    let mut bytes = value.bytes();
    while let Some(byte) = bytes.next() {
        if byte == b'%' {
            let hi = (bytes.next()? as char).to_digit(16)?;
            let lo = (bytes.next()? as char).to_digit(16)?;
            result.push(((hi << 4) | lo) as u8);
        } else if byte == b'+' {
            result.push(b' ');
        } else {
            result.push(byte);
        }
    }
    String::from_utf8(result).ok()
}

fn decode_base64(input: &str) -> Option<Vec<u8>> {
    let mut result = Vec::new();
    let mut value = 0u32;
    let mut bits = 0;
    for byte in input.bytes().take_while(|byte| *byte != b'=') {
        let digit = match byte {
            b'A'..=b'Z' => byte - b'A',
            b'a'..=b'z' => byte - b'a' + 26,
            b'0'..=b'9' => byte - b'0' + 52,
            b'+' | b'-' => 62,
            b'/' | b'_' => 63,
            _ => return None,
        };
        value = (value << 6) | u32::from(digit);
        bits += 6;
        if bits >= 8 {
            bits -= 8;
            result.push((value >> bits) as u8);
            value &= (1 << bits) - 1;
        }
    }
    Some(result)
}

fn parse_vmess(payload: &str) -> Result<ServerProfile, String> {
    let decoded = decode_base64(payload).ok_or("Invalid VMess base64")?;
    let json = String::from_utf8(decoded).map_err(|_| "Invalid VMess UTF-8")?;
    let server = json_string(&json, "add")
        .or_else(|| json_string(&json, "server"))
        .ok_or("VMess server is missing")?;
    let port = json_string(&json, "port")
        .ok_or("VMess port is missing")?
        .parse::<u16>()
        .map_err(|_| "Invalid VMess port")?;
    let credential = json_string(&json, "id").ok_or("VMess ID is missing")?;
    Ok(ServerProfile {
        protocol: Protocol::Vmess,
        name: json_string(&json, "ps").unwrap_or_else(|| server.clone()),
        server,
        port,
        credential,
        server_name: json_string(&json, "sni"),
    })
}

// This scanner reads only the common flat VMess fields. A full JSON parser is needed before runtime use.
fn json_string(json: &str, key: &str) -> Option<String> {
    let start = json.find(&format!("\"{key}\""))?;
    let tail = json[start + key.len() + 2..].trim_start();
    let tail = tail.strip_prefix(':')?.trim_start();
    if let Some(tail) = tail.strip_prefix('"') {
        let mut escaped = false;
        let mut value = String::new();
        for character in tail.chars() {
            if escaped {
                value.push(character);
                escaped = false;
                continue;
            }
            if character == '\\' {
                escaped = true;
                continue;
            }
            if character == '"' {
                return Some(value);
            }
            value.push(character);
        }
        None
    } else {
        Some(
            tail.chars()
                .take_while(|character| character.is_ascii_digit())
                .collect(),
        )
    }
}

fn parse_wireguard_config(text: &str) -> Result<ServerProfile, String> {
    for line in text.lines() {
        if let Some((key, _)) = line.split_once('=') {
            if !matches!(
                key.trim().to_ascii_lowercase().as_str(),
                "privatekey"
                    | "address"
                    | "dns"
                    | "mtu"
                    | "publickey"
                    | "presharedkey"
                    | "endpoint"
                    | "allowedips"
                    | "persistentkeepalive"
            ) {
                return Err("Unsupported WireGuard parameter".into());
            }
        }
    }
    let endpoint = text
        .lines()
        .find_map(|line| {
            line.trim()
                .strip_prefix("Endpoint")
                .and_then(|value| value.trim().strip_prefix('='))
        })
        .ok_or("WireGuard endpoint is missing")?;
    let (server, port) = parse_endpoint(endpoint.trim())?;
    let credential = text
        .lines()
        .find_map(|line| {
            line.trim()
                .strip_prefix("PrivateKey")
                .and_then(|value| value.trim().strip_prefix('='))
        })
        .ok_or("WireGuard private key is missing")?
        .trim()
        .to_string();
    Ok(ServerProfile {
        protocol: Protocol::WireGuard,
        name: "WireGuard".into(),
        server,
        port,
        credential,
        server_name: None,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn removed_protocol_and_disguised_config_are_rejected() {
        assert!(parse_link("awg://anything").is_err());
        assert!(parse_link("amneziawg://anything").is_err());
        assert!(parse_wireguard_config(
            "[Interface]\nPrivateKey=test\nJc=4\n[Peer]\nEndpoint=example.com:443"
        )
        .is_err());
        let mut request: crate::ipc_contract::StartRequest =
            serde_json::from_str(include_str!("../../../fixtures/start-v1.json")).unwrap();
        request.profile.protocol = "amneziawg".into();
        assert!(request.validate_shape().is_err());
    }

    #[test]
    fn parses_vless_domain_and_sni() {
        let profile = parse_link("vless://11111111-1111-1111-1111-111111111111@edge.sslip.io:443?security=tls&sni=example.com#Home").unwrap();
        assert_eq!(profile.protocol, Protocol::Vless);
        assert_eq!(profile.server, "edge.sslip.io");
        assert_eq!(profile.server_name.as_deref(), Some("example.com"));
    }

    #[test]
    fn rejects_missing_port() {
        assert!(parse_link("hy2://secret@example.com#Bad").is_err());
    }
}
