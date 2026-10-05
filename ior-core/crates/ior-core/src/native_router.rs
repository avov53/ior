use crate::{
    dns_engine,
    ipc_contract::{WireNetwork, WireRule},
    proxy_inbound::{ConnectFuture, OutboundConnector, RelayStream},
    udp_relay::{DatagramRelay, ReceiveFuture, SendFuture, UdpFuture},
};
use std::{
    io,
    net::{IpAddr, SocketAddr},
    sync::Arc,
};

pub struct NativeRouter {
    proxy: Arc<dyn OutboundConnector>,
    rules: Arc<Vec<WireRule>>,
    bootstrap: SocketAddr,
    network: Option<WireNetwork>,
    regexes: Arc<std::collections::HashMap<usize, regex::Regex>>,
    ip_sets: Arc<std::collections::HashMap<usize, IpSet>>,
}

pub struct RouteDecision {
    pub target: String,
    pub index: usize,
    pub source: String,
}

struct IpSet {
    ipv4: Vec<(u128, u128)>,
    ipv6: Vec<(u128, u128)>,
}
impl IpSet {
    fn parse(value: &str) -> io::Result<Self> {
        let mut set = Self {
            ipv4: Vec::new(),
            ipv6: Vec::new(),
        };
        for line in value.lines() {
            validate_cidr(line)?;
            let (address, prefix) = line.split_once('/').unwrap();
            let address: IpAddr = address.parse().unwrap();
            let prefix: u32 = prefix.parse().unwrap();
            let (bits, number) = match address {
                IpAddr::V4(ip) => (32, u32::from(ip) as u128),
                IpAddr::V6(ip) => (128, u128::from(ip)),
            };
            let host_bits = bits - prefix;
            let mask = if host_bits == 128 {
                u128::MAX
            } else {
                (1u128 << host_bits) - 1
            };
            let start = number & !mask;
            let range = (start, start | mask);
            if bits == 32 {
                set.ipv4.push(range);
            } else {
                set.ipv6.push(range);
            }
        }
        fn merge(ranges: &mut Vec<(u128, u128)>) {
            ranges.sort_unstable();
            let mut result = Vec::<(u128, u128)>::new();
            for &(start, end) in ranges.iter() {
                if let Some(last) = result.last_mut() {
                    if start <= last.1.saturating_add(1) {
                        last.1 = last.1.max(end);
                        continue;
                    }
                }
                result.push((start, end));
            }
            *ranges = result;
        }
        merge(&mut set.ipv4);
        merge(&mut set.ipv6);
        if set.ipv4.is_empty() && set.ipv6.is_empty() {
            return Err(io::ErrorKind::InvalidInput.into());
        }
        Ok(set)
    }
    fn contains(&self, ip: IpAddr) -> bool {
        let (ranges, value) = match ip {
            IpAddr::V4(ip) => (&self.ipv4, u32::from(ip) as u128),
            IpAddr::V6(ip) => (&self.ipv6, u128::from(ip)),
        };
        let index = ranges.partition_point(|range| range.0 <= value);
        index > 0 && value <= ranges[index - 1].1
    }
}
impl NativeRouter {
    /// A raw SYN has no TLS name. Admit only a route whose target cannot
    /// change for ANY domain name, including encrypted/cached DNS cases.

    pub fn proxy_dns_hedge_delay(&self) -> std::time::Duration {
        self.proxy.dns_hedge_delay()
    }
    pub fn proxy_ipv4_only(&self) -> bool {
        self.proxy.ipv4_only()
    }
    pub fn proxy_ipv6_only(&self) -> bool {
        self.proxy.ipv6_only()
    }
    pub fn new(
        proxy: Arc<dyn OutboundConnector>,
        rules: Vec<WireRule>,
        bootstrap: SocketAddr,
        network: Option<WireNetwork>,
    ) -> io::Result<Self> {
        let mut regexes = std::collections::HashMap::new();
        let mut ip_sets = std::collections::HashMap::new();
        for (index, rule) in rules.iter().enumerate() {
            if rule.kind == "IP-SET" {
                ip_sets.insert(index, IpSet::parse(&rule.value)?);
            }
            if rule.kind == "REGEXP" {
                let matcher = regex::RegexBuilder::new(&rule.value)
                    .size_limit(1024 * 1024)
                    .build()
                    .map_err(|_| {
                        io::Error::new(io::ErrorKind::InvalidInput, "ROUTE_REGEX_UNSUPPORTED")
                    })?;
                regexes.insert(index, matcher);
            }
            if rule.kind == "IP-CIDR" {
                validate_cidr(&rule.value)?;
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
                return Err(io::Error::new(
                    io::ErrorKind::Unsupported,
                    format!("ROUTE_MATCHER_UNAVAILABLE:{}", rule.kind),
                ));
            }
        }
        Ok(Self {
            proxy,
            rules: Arc::new(rules),
            bootstrap,
            network,
            regexes: Arc::new(regexes),
            ip_sets: Arc::new(ip_sets),
        })
    }
    fn owned(&self) -> Self {
        Self {
            proxy: self.proxy.clone(),
            rules: self.rules.clone(),
            bootstrap: self.bootstrap,
            network: self.network.clone(),
            regexes: self.regexes.clone(),
            ip_sets: self.ip_sets.clone(),
        }
    }
    fn domain_decision(&self, host: &str) -> Option<RouteDecision> {
        if host.parse::<IpAddr>().is_ok() {
            return None;
        }
        let host = host.trim_end_matches('.').to_ascii_lowercase();
        for (index, rule) in self.rules.iter().enumerate() {
            if matches!(rule.kind.as_str(), "IP-CIDR" | "IP-SET") && !rule.no_resolve {
                return None;
            }
            let value = rule.value.trim_end_matches('.').to_ascii_lowercase();
            let matched = match rule.kind.as_str() {
                "MATCH" => true,
                "DOMAIN" => host == value,
                "DOMAIN-SUFFIX" => host == value || host.ends_with(&format!(".{value}")),
                "DOMAIN-KEYWORD" => host.contains(&value),
                "REGEXP" => self.regexes[&index].is_match(&host),
                _ => false,
            };
            if matched {
                return Some(RouteDecision {
                    target: rule.target.clone(),
                    index,
                    source: rule.source.clone(),
                });
            }
        }
        None
    }
    #[cfg(test)]
    async fn target(&self, host: &str) -> io::Result<String> {
        Ok(self.decide(host, None).await?.target)
    }
    pub async fn decide(
        &self,
        host: &str,
        destination: Option<IpAddr>,
    ) -> io::Result<RouteDecision> {
        let host = host.trim_end_matches('.').to_ascii_lowercase();
        let mut ip = destination.or_else(|| host.parse::<IpAddr>().ok());
        let domain = host.parse::<IpAddr>().is_err();
        for (index, rule) in self.rules.iter().enumerate() {
            let value = if rule.kind == "IP-SET" {
                String::new()
            } else {
                rule.value.trim_end_matches('.').to_ascii_lowercase()
            };
            let matched = match rule.kind.as_str() {
                "MATCH" => true,
                "DOMAIN" => domain && host == value,
                "DOMAIN-SUFFIX" => {
                    domain && (host == value || host.ends_with(&format!(".{value}")))
                }
                "DOMAIN-KEYWORD" => domain && host.contains(&value),
                "REGEXP" => domain && self.regexes[&index].is_match(&host),
                "IP-SET" => {
                    if ip.is_none() && !rule.no_resolve {
                        ip = Some(
                            dns_engine::resolve_server_ipv4_bound(
                                &host,
                                self.bootstrap,
                                &self.network,
                            )
                            .await?
                            .into(),
                        );
                    }
                    ip.is_some_and(|ip| self.ip_sets[&index].contains(ip))
                }
                "IP-CIDR" => {
                    if ip.is_none() && !rule.no_resolve {
                        ip = Some(
                            dns_engine::resolve_server_ipv4_bound(
                                &host,
                                self.bootstrap,
                                &self.network,
                            )
                            .await?
                            .into(),
                        );
                    }
                    ip.is_some_and(|ip| contains(&value, ip).unwrap_or(false))
                }
                _ => return Err(io::ErrorKind::Unsupported.into()),
            };
            if matched {
                return Ok(RouteDecision {
                    target: rule.target.clone(),
                    index,
                    source: rule.source.clone(),
                });
            }
        }
        Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "ROUTE_FINAL_MISSING",
        ))
    }
    async fn direct_address(&self, host: &str, port: u16) -> io::Result<SocketAddr> {
        let ip = dns_engine::resolve_server_ip_bound(host, self.bootstrap, &self.network).await?;
        Ok(SocketAddr::new(ip.into(), port))
    }
    pub fn connect_destination(
        &self,
        address: SocketAddr,
        domain: Option<String>,
    ) -> ConnectFuture {
        let this = self.owned();
        Box::pin(async move {
            let host = domain.unwrap_or_else(|| address.ip().to_string());
            let decision = this.decide(&host, Some(address.ip())).await?;
            emit_decision(&host, address.port(), &decision, "tcp");
            // Keep the original address except for an IPv4-only proxy's named IPv6 flow.
            // Route selection above still uses the original destination and rule order.
            if decision.target == "PROXY"
                && ((this.proxy.ipv4_only() && address.is_ipv6())
                    || (this.proxy.ipv6_only() && address.is_ipv4()))
                && host.parse::<IpAddr>().is_err()
            {
                crate::trace::emit(
                    serde_json::json!({"stage":"routing","code":"TUNNEL_ADDRESS_FAMILY_FALLBACK","reason":"LOCAL_ADDRESS_FAMILY_UNAVAILABLE","addressFamily":if address.is_ipv6(){"ipv6"}else{"ipv4"},"transport":"tcp"}),
                );
                return this.connect(host, address.port()).await;
            }
            let outbound_host =
                if decision.target == "PROXY" && this.proxy.ipv4_only() && address.is_ipv6() {
                    host
                } else {
                    address.ip().to_string()
                };
            this.connect_target(&decision.target, &outbound_host, address.port())
                .await
        })
    }
    pub fn udp_destination(&self, address: SocketAddr, domain: Option<String>) -> UdpFuture {
        let this = self.owned();
        Box::pin(async move {
            let host = domain.unwrap_or_else(|| address.ip().to_string());
            let decision = this.decide(&host, Some(address.ip())).await?;
            emit_decision(&host, address.port(), &decision, "udp");
            if decision.target == "PROXY"
                && ((this.proxy.ipv4_only() && address.is_ipv6())
                    || (this.proxy.ipv6_only() && address.is_ipv4()))
                && host.parse::<IpAddr>().is_err()
            {
                crate::trace::emit(
                    serde_json::json!({"stage":"routing","code":"TUNNEL_ADDRESS_FAMILY_FALLBACK","reason":"LOCAL_ADDRESS_FAMILY_UNAVAILABLE","addressFamily":if address.is_ipv6(){"ipv6"}else{"ipv4"},"transport":"udp"}),
                );
                return this.udp(host, address.port()).await;
            }
            let outbound_host =
                if decision.target == "PROXY" && this.proxy.ipv4_only() && address.is_ipv6() {
                    host
                } else {
                    address.ip().to_string()
                };
            this.udp_target(&decision.target, &outbound_host, address.port())
                .await
        })
    }
    pub async fn connect_target(
        &self,
        target: &str,
        host: &str,
        port: u16,
    ) -> io::Result<Box<dyn RelayStream>> {
        match target {
            "PROXY" => self.proxy.connect(host.to_owned(), port).await,
            "DIRECT" => {
                let address = self.direct_address(host, port).await?;
                let socket = tokio::net::TcpSocket::from_std_stream(
                    bound_socket_family(false, &self.network, address.is_ipv6())?.into(),
                );
                let guard = crate::egress_registry::register(socket.local_addr()?, address, false);
                let stream = socket.connect(address).await?;
                stream.set_nodelay(true)?;
                Ok(Box::new(crate::egress_registry::RegisteredStream::new(
                    stream, guard,
                )))
            }
            "REJECT" => Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "ROUTE_REJECTED",
            )),
            _ => Err(io::Error::new(
                io::ErrorKind::Unsupported,
                "ROUTE_GROUP_UNAVAILABLE",
            )),
        }
    }
    pub async fn udp_target(
        &self,
        target: &str,
        host: &str,
        port: u16,
    ) -> io::Result<Box<dyn DatagramRelay>> {
        match target {
            "PROXY" => self.proxy.udp(host.to_owned(), port).await,
            "DIRECT" => {
                let address = self.direct_address(host, port).await?;
                let socket = tokio::net::UdpSocket::from_std(
                    bound_socket_family(true, &self.network, address.is_ipv6())?.into(),
                )?;
                socket.connect(address).await?;
                let guard = crate::egress_registry::register(socket.local_addr()?, address, true);
                Ok(Box::new(DirectDatagram(socket, guard)))
            }
            "REJECT" => Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "ROUTE_REJECTED",
            )),
            _ => Err(io::Error::new(
                io::ErrorKind::Unsupported,
                "ROUTE_GROUP_UNAVAILABLE",
            )),
        }
    }
}
fn validate_cidr(value: &str) -> io::Result<()> {
    let (ip, prefix) = value.split_once('/').ok_or(io::ErrorKind::InvalidInput)?;
    let ip: IpAddr = ip.parse().map_err(|_| io::ErrorKind::InvalidInput)?;
    let prefix: u32 = prefix.parse().map_err(|_| io::ErrorKind::InvalidInput)?;
    if prefix > if ip.is_ipv4() { 32 } else { 128 } {
        return Err(io::ErrorKind::InvalidInput.into());
    }
    Ok(())
}
fn contains(value: &str, ip: IpAddr) -> io::Result<bool> {
    validate_cidr(value)?;
    let (network, prefix) = value.split_once('/').unwrap();
    let prefix: u32 = prefix.parse().unwrap();
    let network: IpAddr = network.parse().unwrap();
    Ok(match (network, ip) {
        (IpAddr::V4(a), IpAddr::V4(b)) => {
            let mask = if prefix == 0 {
                0
            } else {
                u32::MAX << (32 - prefix)
            };
            u32::from(a) & mask == u32::from(b) & mask
        }
        (IpAddr::V6(a), IpAddr::V6(b)) => {
            let mask = if prefix == 0 {
                0
            } else {
                u128::MAX << (128 - prefix)
            };
            u128::from(a) & mask == u128::from(b) & mask
        }
        _ => false,
    })
}

pub(crate) fn bound_socket(
    udp: bool,
    network: &Option<WireNetwork>,
) -> io::Result<socket2::Socket> {
    bound_socket_family(udp, network, false)
}
pub(crate) fn bound_socket_family(
    udp: bool,
    network: &Option<WireNetwork>,
    ipv6: bool,
) -> io::Result<socket2::Socket> {
    let socket = socket2::Socket::new(
        if ipv6 {
            socket2::Domain::IPV6
        } else {
            socket2::Domain::IPV4
        },
        if udp {
            socket2::Type::DGRAM
        } else {
            socket2::Type::STREAM
        },
        None,
    )?;
    socket.set_nonblocking(true)?;
    if let Some(network) = network {
        let ip: IpAddr = if ipv6 {
            network.physical_address6.as_deref().ok_or_else(|| {
                io::Error::new(
                    io::ErrorKind::AddrNotAvailable,
                    "PHYSICAL_IPV6_ADDRESS_MISSING",
                )
            })?
        } else {
            &network.physical_address
        }
        .parse()
        .map_err(|_| io::ErrorKind::InvalidInput)?;
        socket.bind(&SocketAddr::new(ip, 0).into())?;
        #[cfg(windows)]
        unsafe {
            use std::os::windows::io::AsRawSocket;
            let index = if ipv6 {
                network
                    .interface_index6
                    .ok_or(io::ErrorKind::AddrNotAvailable)?
            } else {
                network.interface_index.to_be()
            };
            if windows_sys::Win32::Networking::WinSock::setsockopt(
                socket.as_raw_socket() as usize,
                if ipv6 { 41 } else { 0 },
                31,
                &index as *const _ as *const u8,
                4,
            ) != 0
            {
                return Err(io::Error::last_os_error());
            }
        }
    } else {
        socket.bind(
            &SocketAddr::new(
                if ipv6 {
                    std::net::Ipv6Addr::UNSPECIFIED.into()
                } else {
                    std::net::Ipv4Addr::UNSPECIFIED.into()
                },
                0,
            )
            .into(),
        )?;
    }
    Ok(socket)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn every_resolved_candidate_keeps_its_own_ip_rule() {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        struct Candidates;
        impl OutboundConnector for Candidates {
            fn resolve_candidates(&self, _: String) -> Option<crate::proxy_inbound::AddressFuture> {
                Some(Box::pin(async {
                    Ok(vec![
                        "203.0.113.1".parse().unwrap(),
                        "127.0.0.1".parse().unwrap(),
                    ])
                }))
            }
            fn connect(&self, _: String, _: u16) -> ConnectFuture {
                panic!("neither candidate permits PROXY")
            }
        }
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = tokio::spawn(async move {
            let (mut stream, _) = listener.accept().await.unwrap();
            stream.write_all(&[7]).await.unwrap();
        });
        let router = NativeRouter::new(
            Arc::new(Candidates),
            vec![
                rule("IP-CIDR", "203.0.113.0/24", "REJECT"),
                rule("IP-CIDR", "127.0.0.0/8", "DIRECT"),
                rule("MATCH", "", "PROXY"),
            ],
            "127.0.0.1:9".parse().unwrap(),
            None,
        )
        .unwrap();
        let mut stream = tokio::time::timeout(
            std::time::Duration::from_secs(2),
            router.connect("candidate.test".into(), port),
        )
        .await
        .unwrap()
        .unwrap();
        let mut data = [0];
        stream.read_exact(&mut data).await.unwrap();
        assert_eq!(data, [7]);
        server.await.unwrap();
        let reject = NativeRouter::new(
            Arc::new(Candidates),
            vec![
                rule("DOMAIN", "deny.test", "REJECT"),
                rule("MATCH", "", "PROXY"),
            ],
            "127.0.0.1:9".parse().unwrap(),
            None,
        )
        .unwrap();
        assert!(reject.connect("deny.test".into(), 443).await.is_err());
    }
    #[test]
    fn ip_sets_merge_ranges_and_keep_address_families_separate() {
        let set = IpSet::parse("192.0.2.0/25\n192.0.2.128/25\n2001:db8::/32").unwrap();
        assert_eq!(set.ipv4.len(), 1);
        assert!(set.contains("192.0.2.255".parse().unwrap()));
        assert!(!set.contains("192.0.3.0".parse().unwrap()));
        assert!(set.contains("2001:db8::1".parse().unwrap()));
        assert!(!set.contains("2001:db9::1".parse().unwrap()));
        assert!(IpSet::parse("::/0")
            .unwrap()
            .contains("ffff::1".parse().unwrap()));
        assert!(!IpSet::parse("::/0")
            .unwrap()
            .contains("192.0.2.1".parse().unwrap()));
        assert!(IpSet::parse("192.0.2.1/33").is_err());
    }
    struct NeverProxy;
    struct Ipv4RecordingProxy;
    impl OutboundConnector for Ipv4RecordingProxy {
        fn ipv4_only(&self) -> bool {
            true
        }
        fn connect(&self, host: String, _: u16) -> ConnectFuture {
            Box::pin(async move { Err(io::Error::new(io::ErrorKind::Other, host)) })
        }
        fn udp(&self, host: String, _: u16) -> UdpFuture {
            Box::pin(async move { Err(io::Error::new(io::ErrorKind::Other, host)) })
        }
    }
    #[tokio::test]
    async fn ipv4_only_proxy_uses_known_name_for_ipv6_without_changing_rule_selection() {
        let router = NativeRouter::new(
            Arc::new(Ipv4RecordingProxy),
            vec![rule("MATCH", "", "PROXY")],
            "1.1.1.1:53".parse().unwrap(),
            None,
        )
        .unwrap();
        let address = "[2001:db8::1]:443".parse().unwrap();
        assert_eq!(
            router
                .connect_destination(address, Some("cdn.example.com".into()))
                .await
                .err()
                .unwrap()
                .to_string(),
            "cdn.example.com"
        );
        assert_eq!(
            router
                .udp_destination(address, Some("cdn.example.com".into()))
                .await
                .err()
                .unwrap()
                .to_string(),
            "cdn.example.com"
        );
        let address = "192.0.2.1:443".parse().unwrap();
        assert_eq!(
            router
                .connect_destination(address, Some("cdn.example.com".into()))
                .await
                .err()
                .unwrap()
                .to_string(),
            "192.0.2.1"
        );
    }
    #[tokio::test]
    async fn ipv6_only_named_ipv4_flows_fallback_but_original_reject_still_wins() {
        struct Ipv6RecordingProxy;
        impl OutboundConnector for Ipv6RecordingProxy {
            fn ipv6_only(&self) -> bool {
                true
            }
            fn connect(&self, host: String, _: u16) -> ConnectFuture {
                Box::pin(async move { Err(io::Error::other(host)) })
            }
            fn udp(&self, host: String, _: u16) -> UdpFuture {
                Box::pin(async move { Err(io::Error::other(host)) })
            }
        }
        let router = NativeRouter::new(
            Arc::new(Ipv6RecordingProxy),
            vec![rule("MATCH", "", "PROXY")],
            "1.1.1.1:53".parse().unwrap(),
            None,
        )
        .unwrap();
        let address = "192.0.2.1:443".parse().unwrap();
        for error in [
            router
                .connect_destination(address, Some("cdn.example".into()))
                .await
                .err()
                .unwrap(),
            router
                .udp_destination(address, Some("cdn.example".into()))
                .await
                .err()
                .unwrap(),
        ] {
            assert_eq!(error.to_string(), "cdn.example");
        }
        let reject = NativeRouter::new(
            Arc::new(Ipv6RecordingProxy),
            vec![
                rule("IP-CIDR", "192.0.2.0/24", "REJECT"),
                rule("MATCH", "", "PROXY"),
            ],
            "1.1.1.1:53".parse().unwrap(),
            None,
        )
        .unwrap();
        assert_eq!(
            reject
                .connect_destination(address, Some("cdn.example".into()))
                .await
                .err()
                .unwrap()
                .kind(),
            io::ErrorKind::PermissionDenied
        );
        assert_eq!(
            reject
                .udp_destination(address, Some("cdn.example".into()))
                .await
                .err()
                .unwrap()
                .kind(),
            io::ErrorKind::PermissionDenied
        );
    }
    impl OutboundConnector for NeverProxy {
        fn connect(&self, _: String, _: u16) -> crate::proxy_inbound::ConnectFuture {
            Box::pin(async {
                Err(io::Error::new(
                    io::ErrorKind::Other,
                    "test proxy must not run",
                ))
            })
        }
    }
    fn rule(kind: &str, value: &str, target: &str) -> WireRule {
        WireRule {
            kind: kind.into(),
            value: value.into(),
            target: target.into(),
            no_resolve: true,
            source: "clash".into(),
        }
    }
    #[tokio::test]
    async fn ordered_rules_preserve_reject_direct_and_domain_boundaries() {
        let router = NativeRouter::new(
            Arc::new(NeverProxy),
            vec![
                rule("DOMAIN", "private.example", "REJECT"),
                rule("DOMAIN-SUFFIX", "example", "DIRECT"),
                rule("MATCH", "", "PROXY"),
            ],
            "1.1.1.1:53".parse().unwrap(),
            None,
        )
        .unwrap();
        assert_eq!(router.target("private.example").await.unwrap(), "REJECT");
        assert_eq!(router.target("cdn.example").await.unwrap(), "DIRECT");
        assert_eq!(router.target("notexample").await.unwrap(), "PROXY");
        assert!(
            matches!(router.connect("private.example".into(),443).await,Err(error) if error.kind()==io::ErrorKind::PermissionDenied)
        );
    }
    #[tokio::test]
    async fn no_resolve_does_not_turn_domain_into_cidr_match() {
        let router = NativeRouter::new(
            Arc::new(NeverProxy),
            vec![
                rule("IP-CIDR", "0.0.0.0/0", "DIRECT"),
                rule("MATCH", "", "PROXY"),
            ],
            "127.0.0.1:9".parse().unwrap(),
            None,
        )
        .unwrap();
        assert_eq!(router.target("example.com").await.unwrap(), "PROXY");
        assert_eq!(router.target("203.0.113.1").await.unwrap(), "DIRECT");
    }
    #[test]
    fn unknown_matcher_and_invalid_cidr_fail_before_network() {
        assert!(NativeRouter::new(
            Arc::new(NeverProxy),
            vec![rule("RULE-SET", "unknown", "DIRECT")],
            "1.1.1.1:53".parse().unwrap(),
            None
        )
        .is_err());
        assert!(NativeRouter::new(
            Arc::new(NeverProxy),
            vec![rule("IP-CIDR", "geoip:ru", "DIRECT")],
            "1.1.1.1:53".parse().unwrap(),
            None
        )
        .is_err());
    }
    #[tokio::test]
    async fn tun_domain_context_keeps_the_original_ip_and_order() {
        let router = NativeRouter::new(
            Arc::new(NeverProxy),
            vec![
                rule("IP-CIDR", "203.0.113.0/24", "REJECT"),
                rule("DOMAIN", "cdn.example", "DIRECT"),
                rule("MATCH", "", "PROXY"),
            ],
            "127.0.0.1:9".parse().unwrap(),
            None,
        )
        .unwrap();
        let decision = router
            .decide("cdn.example", Some("203.0.113.7".parse().unwrap()))
            .await
            .unwrap();
        assert_eq!(decision.target, "REJECT");
        assert_eq!(decision.index, 0);
        assert_eq!(
            router
                .decide("cdn.example", Some("192.0.2.7".parse().unwrap()))
                .await
                .unwrap()
                .target,
            "DIRECT"
        );
        assert_eq!(
            router
                .decide("192.0.2.7", Some("192.0.2.7".parse().unwrap()))
                .await
                .unwrap()
                .target,
            "PROXY"
        );
        let error = router
            .connect_destination(
                "203.0.113.7:443".parse().unwrap(),
                Some("cdn.example".into()),
            )
            .await
            .err()
            .unwrap();
        assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
    }
}
impl OutboundConnector for NativeRouter {
    fn connect(&self, host: String, port: u16) -> ConnectFuture {
        let this = self.owned();
        Box::pin(async move {
            if let Some(decision) = this
                .domain_decision(&host)
                .filter(|decision| decision.target != "PROXY")
            {
                emit_decision(&host, port, &decision, "tcp");
                return this.connect_target(&decision.target, &host, port).await;
            }
            if let Some(resolve) = this.proxy.resolve_candidates(host.clone()) {
                use futures::{stream::FuturesUnordered, StreamExt};
                let addresses = resolve.await?;
                let mut active = FuturesUnordered::new();
                for (index, address) in addresses.into_iter().take(16).enumerate() {
                    let this = &this;
                    let host = &host;
                    active.push(async move {
                        tokio::time::sleep(std::time::Duration::from_millis(index as u64*250)).await;
                        let decision=this.decide(host,Some(address)).await?;
                        emit_decision(host,port,&decision,"tcp");
                        crate::trace::emit(serde_json::json!({"stage":"routing","code":"ROUTE_ADDRESS_ATTEMPT","attempt":index+1,"destination":SocketAddr::new(address,port).to_string(),"addressFamily":if address.is_ipv6(){"ipv6"}else{"ipv4"},"reason":if index==0{"FIRST_CANDIDATE"}else{"STAGGERED_CANDIDATE"}}));
                        let result=this.connect_target(&decision.target,&address.to_string(),port).await;
                        if let Err(error)=&result {crate::trace::emit(serde_json::json!({"stage":"routing","code":"ROUTE_ADDRESS_ATTEMPT_FAILED","attempt":index+1,"kind":format!("{:?}",error.kind())}));}
                        result
                    });
                }
                let mut failure = io::Error::new(io::ErrorKind::NotFound, "DNS_ADDRESS_NODATA");
                while let Some(result) = active.next().await {
                    match result {
                        Ok(stream) => return Ok(stream),
                        Err(error) => failure = error,
                    }
                }
                return Err(failure);
            }
            let decision = this.decide(&host, None).await?;
            emit_decision(&host, port, &decision, "tcp");
            this.connect_target(&decision.target, &host, port).await
        })
    }
    fn udp(&self, host: String, port: u16) -> UdpFuture {
        let this = self.owned();
        Box::pin(async move {
            if let Some(decision) = this
                .domain_decision(&host)
                .filter(|decision| decision.target != "PROXY")
            {
                emit_decision(&host, port, &decision, "udp");
                return this.udp_target(&decision.target, &host, port).await;
            }
            if let Some(resolve) = this.proxy.resolve_candidates(host.clone()) {
                let addresses = resolve.await?;
                let address = *addresses.first().ok_or(io::ErrorKind::NotFound)?;
                let decision = this.decide(&host, Some(address)).await?;
                emit_decision(&host, port, &decision, "udp");
                return this
                    .udp_target(&decision.target, &address.to_string(), port)
                    .await;
            }
            let decision = this.decide(&host, None).await?;
            emit_decision(&host, port, &decision, "udp");
            this.udp_target(&decision.target, &host, port).await
        })
    }
}
pub fn emit_decision(host: &str, port: u16, decision: &RouteDecision, transport: &str) {
    let destination = if host.len() <= 255
        && host
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || b".-:_[]".contains(&byte))
    {
        format!("{host}:{port}")
    } else {
        "redacted".into()
    };
    crate::trace::emit(
        serde_json::json!({"stage":"routing","code":"ROUTE_SELECTED","destination":destination,"target":decision.target,"ruleIndex":decision.index,"ruleSource":decision.source,"transport":transport}),
    );
}
struct DirectDatagram(
    tokio::net::UdpSocket,
    #[allow(dead_code)] Option<crate::egress_registry::Guard>,
);
impl DatagramRelay for DirectDatagram {
    fn send<'a>(&'a mut self, data: &'a [u8]) -> SendFuture<'a> {
        Box::pin(async move {
            self.0.send(data).await?;
            Ok(())
        })
    }
    fn receive(&mut self) -> ReceiveFuture<'_> {
        Box::pin(async move {
            let mut data = vec![0; 65535];
            let n = self.0.recv(&mut data).await?;
            data.truncate(n);
            Ok(data)
        })
    }
}
