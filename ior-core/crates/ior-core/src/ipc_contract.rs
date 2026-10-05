//! Versioned C# ↔ Rust control payload. This module validates shape only;
//! native transport readiness is checked separately before any network change.

use serde::{Deserialize, Serialize};

pub const API_VERSION: u16 = 3;

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct StartRequest {
    pub api_version: u16,
    pub request_id: String,
    pub operation: String,
    pub profile: WireProfile,
    pub dns: WireDns,
    pub routes: Vec<WireRule>,
    #[serde(default)]
    pub network: Option<WireNetwork>,
    #[serde(default)]
    pub tun: Option<WireTun>,
    #[serde(default)]
    pub ingress_auth: Option<IngressAuth>,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct IngressAuth {
    pub username: String,
    pub password: String,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct WireNetwork {
    pub physical_address: String,
    pub interface_index: u32,
    #[serde(default)]
    pub physical_address6: Option<String>,
    #[serde(default)]
    pub interface_index6: Option<u32>,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct WireTun {
    pub name: String,
    pub mtu: u16,
    pub wintun_path: String,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct WireProfile {
    pub protocol: String,
    pub server: String,
    pub port: u16,
    pub credential: String,
    pub server_name: String,
    pub public_key: Option<String>,
    pub short_id: Option<String>,
    #[serde(default)]
    pub flow: Option<String>,
    #[serde(default)]
    pub fingerprint: Option<String>,
    #[serde(default)]
    pub options: ProtocolOptions,
}

#[derive(Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ProtocolOptions {
    pub method: Option<String>,
    pub alter_id: Option<u32>,
    pub tls: Option<bool>,
    pub transport: Option<String>,
    pub host: Option<String>,
    pub path: Option<String>,
    pub service_name: Option<String>,
    pub local_addresses: Option<Vec<String>>,
    pub allowed_ips: Option<Vec<String>>,
    pub peer_public_key: Option<String>,
    pub preshared_key: Option<String>,
    pub keepalive: Option<u16>,
    pub mtu: Option<u16>,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct WireDns {
    pub bootstrap: String,
    pub upstream: String,
    pub ipv4_only: bool,
    #[serde(default)]
    pub upstreams: Vec<String>,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct WireRule {
    pub kind: String,
    pub value: String,
    pub target: String,
    pub no_resolve: bool,
    pub source: String,
}

impl StartRequest {
    pub fn validate_shape(&self) -> Result<(), &'static str> {
        if self.ingress_auth.as_ref().is_some_and(|auth| {
            auth.username.is_empty()
                || auth.username.len() > 255
                || auth.password.len() < 16
                || auth.password.len() > 255
        }) {
            return Err("private ingress credentials are invalid");
        }
        if !matches!(self.api_version, 1 | 2 | 3) {
            return Err("unsupported API version");
        }
        if self.request_id.is_empty()
            || self.request_id.len() > 64
            || !self
                .request_id
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
            || !matches!(self.operation.as_str(), "start" | "validate")
        {
            return Err("invalid request identity or operation");
        }
        if self.profile.server.trim().is_empty()
            || self.profile.port == 0
            || self.profile.credential.is_empty()
            || self.profile.server_name.is_empty()
        {
            return Err("incomplete server profile");
        }
        match self.profile.protocol.as_str() {
            "vless_tls" | "hysteria2" | "vmess" | "shadowsocks" | "trojan" | "wireguard" => (),
            "vless_reality"
                if self
                    .profile
                    .public_key
                    .as_deref()
                    .is_some_and(|key| !key.is_empty()) =>
            {
                ()
            }
            _ => return Err("unsupported or incomplete transport profile"),
        }
        if self.dns.bootstrap.is_empty() || self.dns.upstream.is_empty() {
            return Err("DNS bootstrap or upstream is missing");
        }
        if self.routes.last().map(|rule| rule.kind.as_str()) != Some("MATCH") {
            return Err("route plan has no final MATCH");
        }
        for (index, rule) in self.routes.iter().enumerate() {
            if rule.kind == "MATCH" && index != self.routes.len() - 1 {
                return Err("MATCH must be the final rule");
            }
            if !matches!(rule.target.as_str(), "PROXY" | "DIRECT" | "REJECT") {
                return Err("route target is not supported");
            }
            if !matches!(
                rule.kind.as_str(),
                "MATCH"
                    | "DOMAIN"
                    | "DOMAIN-SUFFIX"
                    | "DOMAIN-KEYWORD"
                    | "IP-CIDR"
                    | "IP-SET"
                    | "REGEXP"
            ) {
                return Err("route matcher is not supported");
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shared_fixture_round_trips_and_checks_version() {
        let json = include_str!("../../../fixtures/start-v1.json");
        let request: StartRequest = serde_json::from_str(json).unwrap();
        request.validate_shape().unwrap();
        let round_trip: StartRequest =
            serde_json::from_str(&serde_json::to_string(&request).unwrap()).unwrap();
        assert_eq!(round_trip.api_version, 1);
        let mut unsupported = request.clone();
        unsupported.api_version = 4;
        assert_eq!(unsupported.validate_shape(), Err("unsupported API version"));
    }
}
