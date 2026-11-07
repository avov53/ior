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
    route_dns: Option<Arc<crate::ipc_contract::WireDns>>,
    route_dns_cache: Arc<crate::dns_response_cache::DnsResponseCache>,
    reject_unsupported_packet_family: bool,
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
    pub fn with_route_dns(mut self,dns:&crate::ipc_contract::WireDns)->Self{self.route_dns=Some(Arc::new(dns.clone()));self}
    pub fn with_full_awg_family_policy(mut self, enabled:bool)->Self {self.reject_unsupported_packet_family=enabled;self}
    pub fn reject_packet_family(&self,destination:IpAddr)->bool {self.reject_unsupported_packet_family && ((destination.is_ipv6()&&self.proxy.ipv4_only())||(destination.is_ipv4()&&self.proxy.ipv6_only()))}
    pub fn packet_path_enabled(&self)->bool {self.proxy.packet_path_enabled()}
    pub fn direct_family_fallback_enabled(&self)->bool {cfg!(target_os="windows") && self.network.as_ref().is_some_and(|n|n.direct_ipv4_fallback && n.physical_address6.is_none())}
    pub fn named_tun_targets(&self)->bool {self.proxy.named_tun_destination()}
    pub fn raw_packet(&self,source:SocketAddr,destination:SocketAddr,protocol:u8)->Result<crate::awg_packets::OpenFuture,&'static str> {
        if !self.packet_route_is_proxy(destination.ip(), &[]) {return Err("route_requires_name_or_nonproxy");}
        self.proxy.raw_packet(source,destination,protocol).ok_or("packet_path_unavailable")
    }
    /// Resolve only observed DNS names. Conflicting CDN observations stay on the name-inspecting stack.
    pub fn raw_packet_observed(&self, source:SocketAddr, destination:SocketAddr, protocol:u8, names:&[String])->Result<crate::awg_packets::OpenFuture,&'static str> {
        if !self.packet_route_is_proxy(destination.ip(), names) {
            return Err(if names.is_empty(){"route_requires_name_or_nonproxy"}else{"dns_route_conflict_or_nonproxy"});
        }
        self.proxy.raw_packet(source,destination,protocol).ok_or("packet_path_unavailable")
    }
    pub(crate) fn packet_route_is_proxy(&self, destination:IpAddr, names:&[String])->bool {
        if !names.is_empty() {
            return names.iter().all(|name| self.packet_named_target(destination, name)==Some("PROXY"));
        }
        // Without a DNS observation, every possible name must select PROXY.
        for (index,rule) in self.rules.iter().enumerate(){
            let matched=match rule.kind.as_str(){
                "IP-CIDR"=>contains(&rule.value,destination).unwrap_or(false),
                "IP-SET"=>self.ip_sets[&index].contains(destination),
                "MATCH"=>true,
                _=>{if rule.target!="PROXY" {return false;} continue;}
            };
            if matched {return rule.target=="PROXY";}
        }
        false
    }
    fn packet_named_target(&self, destination:IpAddr, host:&str)->Option<&str> {
        for (index, rule) in self.rules.iter().enumerate() {
            let matched=match rule.kind.as_str() {
                "IP-CIDR"=>contains(&rule.value,destination).unwrap_or(false),
                "IP-SET"=>self.ip_sets[&index].contains(destination),
                "MATCH"=>true,
                "DOMAIN"=>host.eq_ignore_ascii_case(rule.value.trim_end_matches('.')),
                "DOMAIN-SUFFIX"=>{
                    let value=rule.value.trim_end_matches('.');
                    host.eq_ignore_ascii_case(value) || host.len()>value.len() && host.as_bytes()[host.len()-value.len()-1]==b'.' && host.get(host.len()-value.len()..).is_some_and(|suffix|suffix.eq_ignore_ascii_case(value))
                },
                "DOMAIN-KEYWORD"=>rule.value.is_empty() || host.as_bytes().windows(rule.value.len()).any(|part|part.eq_ignore_ascii_case(rule.value.as_bytes())),
                "REGEXP"=>self.regexes[&index].is_match(host),
                _=>return None,
            };
            if matched {return Some(rule.target.as_str());}
        }
        None
    }

    pub fn proxy_dns_hedge_delay(&self) -> std::time::Duration {
        self.proxy.dns_hedge_delay()
    }
    pub fn proxy_ipv4_only(&self) -> bool {
        self.proxy.ipv4_only()
    }
    pub fn proxy_dns_observations(&self)->Option<Arc<crate::tun_names::NameCache>> {self.proxy.dns_observations()}
    pub fn proxy_dns_response_cache(&self)->Option<Arc<crate::dns_response_cache::DnsResponseCache>> {self.proxy.dns_response_cache()}
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
            route_dns: None,
            route_dns_cache: proxy.dns_response_cache().unwrap_or_else(||Arc::new(crate::dns_response_cache::DnsResponseCache::default())),
            reject_unsupported_packet_family:false,
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
            route_dns: self.route_dns.clone(),
            route_dns_cache: self.route_dns_cache.clone(),
            reject_unsupported_packet_family:self.reject_unsupported_packet_family,
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
                            self.resolve_route_ip(&host)
                            .await?
                            .into(),
                        );
                    }
                    ip.is_some_and(|ip| self.ip_sets[&index].contains(ip))
                }
                "IP-CIDR" => {
                    if ip.is_none() && !rule.no_resolve {
                        ip = Some(
                            self.resolve_route_ip(&host)
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
    /// DNS has a name, not a destination address yet. Resolving it through the
    /// physical bootstrap to choose its resolver creates a circular dependency.
    /// Apply name/final rules here; enforce IP rules when the resolved flow opens.
    pub fn decide_dns(&self,host:&str)->io::Result<RouteDecision> {
        let host=host.trim_end_matches('.').to_ascii_lowercase();
        for (index,rule) in self.rules.iter().enumerate(){
            if matches!(rule.kind.as_str(),"IP-CIDR"|"IP-SET"){continue;}
            let value=rule.value.trim_end_matches('.').to_ascii_lowercase();
            let matched=match rule.kind.as_str(){
                "IP-CIDR"|"IP-SET"=>continue,
                "MATCH"=>true,
                "DOMAIN"=>host==value,
                "DOMAIN-SUFFIX"=>host==value||host.ends_with(&format!(".{value}")),
                "DOMAIN-KEYWORD"=>host.contains(&value),
                "REGEXP"=>self.regexes[&index].is_match(&host),
                _=>return Err(io::ErrorKind::Unsupported.into()),
            };
            if matched{return Ok(RouteDecision{target:rule.target.clone(),index,source:rule.source.clone()});}
        }
        Err(io::Error::new(io::ErrorKind::InvalidInput,"ROUTE_FINAL_MISSING"))
    }
    async fn resolve_route_ip(&self,host:&str)->io::Result<std::net::Ipv4Addr> {
        let Some(dns)=&self.route_dns else{return dns_engine::resolve_server_ipv4_bound(host,self.bootstrap,&self.network).await;};
        let route=self.decide_dns(host)?;
        match route.target.as_str(){
            "DIRECT"=>return dns_engine::resolve_server_ipv4_bound(host,self.bootstrap,&self.network).await,
            "REJECT"=>return Err(io::Error::new(io::ErrorKind::PermissionDenied,"DNS_ROUTE_REJECTED")),
            "PROXY"=>(),_=>return Err(io::ErrorKind::Unsupported.into()),
        }
        let servers=if dns.upstreams.is_empty(){vec![dns.upstream.clone()]}else{dns.upstreams.clone()};
        let servers=servers.iter().map(|value|value.parse::<IpAddr>().map(|ip|SocketAddr::new(ip,53).to_string()).map_err(|_|io::ErrorKind::InvalidInput)).collect::<Result<Vec<_>,_>>()?;
        let mut id=[0;2];getrandom::getrandom(&mut id).map_err(io::Error::other)?;let query=dns_engine::encode_address_question(host,id,1)?;
        let answer=tokio::time::timeout(std::time::Duration::from_secs(8),crate::dns_exchange::hedged_with_delay(&servers,self.proxy_dns_hedge_delay(),|server|{
            let query=&query;
            async move{
                let scope=format!("PROXY|{server}");if let Some(reply)=self.route_dns_cache.get(query,&scope,false){return Ok(reply);}
                let endpoint:SocketAddr=server.parse().map_err(|_|io::ErrorKind::InvalidInput)?;
                let mut relay=self.proxy.udp(endpoint.ip().to_string(),53).await?;relay.send(query).await?;
                let mut reply=relay.receive().await?;let question=crate::tun_names::question(query)?;
                if !crate::tun_names::matches_response(&reply,&question){return Err(io::ErrorKind::InvalidData.into());}
                if reply[2]&2!=0 {use tokio::io::{AsyncReadExt,AsyncWriteExt};let mut tcp=self.proxy.connect(endpoint.ip().to_string(),53).await?;tcp.write_u16(query.len() as u16).await?;tcp.write_all(query).await?;tcp.flush().await?;let len=tcp.read_u16().await? as usize;if len<12{return Err(io::ErrorKind::InvalidData.into());}reply.resize(len,0);tcp.read_exact(&mut reply).await?;}
                dns_engine::decode_address_answers(&reply,query)?;self.route_dns_cache.insert(query,&scope,false,&reply);Ok(reply)
            }
        })).await.map_err(|_|io::ErrorKind::TimedOut)??;
        dns_engine::decode_address_answers(&answer,&query)?.into_iter().find_map(|ip|if let IpAddr::V4(ip)=ip{Some(ip)}else{None}).ok_or_else(||io::ErrorKind::NotFound.into())
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
        self.connect_destination_policy(address,domain,false,false)
    }
    pub fn connect_sniffed_destination(&self,address:SocketAddr,domain:Option<String>)->ConnectFuture {
        self.connect_destination_policy(address,domain,true,false)
    }
    pub fn connect_observed_destination(&self,address:SocketAddr,domain:Option<String>,sniffed:bool,observed:bool)->ConnectFuture {
        self.connect_destination_policy(address,domain,sniffed,observed)
    }
    fn connect_destination_policy(&self,address:SocketAddr,domain:Option<String>,sniffed:bool,observed:bool)->ConnectFuture {
        let this = self.owned();
        Box::pin(async move {
            let host = domain.unwrap_or_else(|| address.ip().to_string());
            let decision = this.decide(&host, Some(address.ip())).await?;
            emit_decision(&host, address.port(), &decision, "tcp");
            if decision.target=="PROXY" && this.reject_packet_family(address.ip()) {
                crate::trace::emit(serde_json::json!({"stage":"routing","code":"AWG_PROXY_FAMILY_REJECTED","destinationName":host,"transport":"tcp","addressFamily":if address.is_ipv6(){"ipv6"}else{"ipv4"},"reason":"address_family"}));
                return Err(io::Error::new(io::ErrorKind::Unsupported,"AWG_PROXY_ADDRESS_FAMILY_UNSUPPORTED"));
            }
            // Route against the original IP/SNI first. A stream proxy can resolve
            // the name at its exit, avoiding unusable locally cached IPv6/CDN IPs.
            if sniffed && decision.target=="PROXY" && this.proxy.named_tun_destination() && host.parse::<IpAddr>().is_err(){
                crate::trace::code("routing","TUN_PROXY_NAMED_DESTINATION");
                return this.connect_target("PROXY",&host,address.port()).await;
            }
            if direct_ipv4_fallback_allowed(&this.network,address,&host,&decision.target,observed) {
                let stream=this.connect_target("DIRECT",&host,address.port()).await?;
                crate::trace::emit(serde_json::json!({"stage":"connect","code":"DIRECT_IPV4_FALLBACK_READY","destinationName":host,"reason":"PHYSICAL_IPV6_ADDRESS_MISSING","addressFamily":"ipv4","transport":"tcp"}));
                return Ok(stream);
            }
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
            if decision.target=="PROXY" && this.reject_packet_family(address.ip()) {
                return Err(io::Error::new(io::ErrorKind::Unsupported,"AWG_PROXY_ADDRESS_FAMILY_UNSUPPORTED"));
            }
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
pub(crate) fn direct_ipv4_fallback_allowed(network:&Option<WireNetwork>,address:SocketAddr,host:&str,target:&str,observed:bool)->bool {
    cfg!(target_os="windows") && target=="DIRECT" && observed && address.is_ipv6() && host.parse::<IpAddr>().is_err() &&
        network.as_ref().is_some_and(|n|n.direct_ipv4_fallback && n.physical_address6.is_none() && n.physical_address.parse::<std::net::Ipv4Addr>().is_ok())
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
    #[cfg(target_os = "android")]
    {
        use std::os::fd::AsRawFd;
        crate::android_network::protect(socket.as_raw_fd())?;
    }
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
    #[test]
    fn direct_family_policy_requires_verified_name_and_explicit_opt_in() {
        let mut n:WireNetwork=serde_json::from_value(serde_json::json!({"physicalAddress":"127.0.0.1","interfaceIndex":1})).unwrap();
        let address:SocketAddr="[2001:db8::7]:443".parse().unwrap();
        assert!(!direct_ipv4_fallback_allowed(&Some(n.clone()),address,"video.test","DIRECT",true));
        n.direct_ipv4_fallback=true;
        assert_eq!(direct_ipv4_fallback_allowed(&Some(n.clone()),address,"video.test","DIRECT",true),cfg!(target_os="windows"));
        for (host,target,observed) in [("video.test","PROXY",true),("video.test","REJECT",true),("video.test","DIRECT",false),("2001:db8::7","DIRECT",true)] {
            assert!(!direct_ipv4_fallback_allowed(&Some(n.clone()),address,host,target,observed));
        }
        n.physical_address6=Some("::1".into());assert!(!direct_ipv4_fallback_allowed(&Some(n),address,"video.test","DIRECT",true));
    }
    #[cfg(windows)]
    #[tokio::test]
    async fn direct_ipv4_reserve_reaches_only_local_direct_target() {
        use tokio::io::{AsyncReadExt,AsyncWriteExt};
        let target=tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();let port=target.local_addr().unwrap().port();
        let responder=tokio::spawn(async move {let(mut stream,_)=target.accept().await.unwrap();let mut bytes=[0;5];stream.read_exact(&mut bytes).await.unwrap();assert_eq!(&bytes,b"proof");});
        let dns=tokio::net::UdpSocket::bind("127.0.0.1:0").await.unwrap();let bootstrap=dns.local_addr().unwrap();
        let dns_task=tokio::spawn(async move{let mut data=[0;512];let(len,from)=dns.recv_from(&mut data).await.unwrap();let mut reply=data[..len].to_vec();reply[2]=0x81;reply[3]=0x80;reply[7]=1;reply.extend([0xc0,0x0c,0,1,0,1,0,0,0,30,0,4,127,0,0,1]);dns.send_to(&reply,from).await.unwrap();});
        let network=serde_json::from_value(serde_json::json!({"physicalAddress":"127.0.0.1","interfaceIndex":1,"directIpv4Fallback":true})).unwrap();
        let router=NativeRouter::new(Arc::new(RoutingDnsProxy),vec![rule("MATCH","","DIRECT")],bootstrap,Some(network)).unwrap();
        let mut stream=tokio::time::timeout(std::time::Duration::from_secs(3),router.connect_observed_destination(SocketAddr::new("2001:db8::7".parse().unwrap(),port),Some("video.test".into()),true,true)).await.unwrap().unwrap();
        stream.write_all(b"proof").await.unwrap();responder.await.unwrap();dns_task.await.unwrap();
    }
    struct RoutingDnsProxy;
    struct RoutingDnsRelay(Option<Vec<u8>>);
    impl DatagramRelay for RoutingDnsRelay {
        fn send<'a>(&'a mut self,query:&'a [u8])->SendFuture<'a>{Box::pin(async move{let mut reply=query.to_vec();reply[2]=0x81;reply[3]=0x80;reply[7]=1;reply.extend([0xc0,0x0c,0,1,0,1,0,0,0,30,0,4,198,51,100,7]);self.0=Some(reply);Ok(())})}
        fn receive(&mut self)->ReceiveFuture<'_>{Box::pin(async move{Ok(self.0.take().unwrap())})}
    }
    impl OutboundConnector for RoutingDnsProxy {
        fn udp(&self,host:String,port:u16)->UdpFuture{assert_eq!((host.as_str(),port),("8.8.8.8",53));Box::pin(async{Ok(Box::new(RoutingDnsRelay(None)) as Box<dyn DatagramRelay>)})}
        fn connect(&self,host:String,_:u16)->ConnectFuture{Box::pin(async move{Err(io::Error::new(io::ErrorKind::Other,host))})}
    }
    #[tokio::test]
    async fn proxy_ip_rules_use_configured_proxy_dns_without_physical_bootstrap(){
        let dns=crate::ipc_contract::WireDns{bootstrap:"127.0.0.1".into(),upstream:"8.8.8.8".into(),upstreams:vec![],ipv4_only:false};
        let mut ip_rule=rule("IP-CIDR","203.0.113.0/24","REJECT");ip_rule.no_resolve=false;
        let router=NativeRouter::new(Arc::new(RoutingDnsProxy),vec![ip_rule,rule("MATCH","","PROXY")],"127.0.0.1:9".parse().unwrap(),None).unwrap().with_route_dns(&dns);
        assert_eq!(router.connect("tr.rbxcdn.com".into(),443).await.err().unwrap().to_string(),"tr.rbxcdn.com");
        let mut ip_rule=rule("IP-CIDR","198.51.100.0/24","REJECT");ip_rule.no_resolve=false;
        let blocked=NativeRouter::new(Arc::new(RoutingDnsProxy),vec![ip_rule,rule("MATCH","","PROXY")],"127.0.0.1:9".parse().unwrap(),None).unwrap().with_route_dns(&dns);
        assert_eq!(blocked.connect("tr.rbxcdn.com".into(),443).await.err().unwrap().kind(),io::ErrorKind::PermissionDenied);
    }
    #[test]
    fn dns_name_rules_preserve_direct_reject_and_final_without_ip_resolution(){
        let router=NativeRouter::new(Arc::new(NeverProxy),vec![rule("IP-CIDR","10.0.0.0/8","DIRECT"),rule("DOMAIN","local.example","DIRECT"),rule("DOMAIN-SUFFIX","blocked.example","REJECT"),rule("MATCH","","PROXY")],"127.0.0.1:9".parse().unwrap(),None).unwrap();
        assert_eq!(router.decide_dns("local.example").unwrap().target,"DIRECT");
        assert_eq!(router.decide_dns("cdn.blocked.example").unwrap().target,"REJECT");
        assert_eq!(router.decide_dns("tr.rbxcdn.com").unwrap().target,"PROXY");
    }
    struct NamedRecordingProxy(bool);
    impl OutboundConnector for NamedRecordingProxy {
        fn named_tun_destination(&self)->bool{self.0}
        fn connect(&self,host:String,_:u16)->ConnectFuture {Box::pin(async move{Err(io::Error::new(io::ErrorKind::Other,host))})}
    }
    #[tokio::test]
    async fn sniffed_proxy_names_preserve_rules_and_do_not_change_ip_tunnels_or_dns_only_names(){
        let address:SocketAddr="[2001:db8::9]:443".parse().unwrap();
        for named in [false,true]{
            let router=NativeRouter::new(Arc::new(NamedRecordingProxy(named)),vec![rule("MATCH","","PROXY")],"1.1.1.1:53".parse().unwrap(),None).unwrap();
            let result=router.connect_sniffed_destination(address,Some("tr.rbxcdn.com".into())).await.err().unwrap().to_string();
            assert_eq!(result,if named{"tr.rbxcdn.com"}else{"2001:db8::9"});
            assert_eq!(router.connect_destination(address,Some("tr.rbxcdn.com".into())).await.err().unwrap().to_string(),"2001:db8::9");
        }
        let router=NativeRouter::new(Arc::new(NamedRecordingProxy(true)),vec![rule("IP-CIDR","2001:db8::/32","REJECT"),rule("DOMAIN-SUFFIX","rbxcdn.com","PROXY"),rule("MATCH","","PROXY")],"1.1.1.1:53".parse().unwrap(),None).unwrap();
        assert_eq!(router.connect_sniffed_destination(address,Some("tr.rbxcdn.com".into())).await.err().unwrap().kind(),io::ErrorKind::PermissionDenied);
    }
    #[tokio::test]
    async fn sniffed_direct_name_keeps_original_address(){
        let listener=tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();let address=listener.local_addr().unwrap();
        let router=NativeRouter::new(Arc::new(NamedRecordingProxy(true)),vec![rule("DOMAIN-SUFFIX","rbxcdn.com","DIRECT"),rule("MATCH","","PROXY")],"1.1.1.1:53".parse().unwrap(),None).unwrap();
        let stream=router.connect_sniffed_destination(address,Some("tr.rbxcdn.com".into())).await.unwrap();
        let(_peer,_)=listener.accept().await.unwrap();drop(stream);
    }
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
    async fn awg_named_family_refusal_does_not_open_a_second_tcp_path_or_bypass_rules(){
        let router=NativeRouter::new(Arc::new(Ipv4RecordingProxy),vec![rule("DOMAIN","blocked.test","REJECT"),rule("MATCH","","PROXY")],"127.0.0.1:9".parse().unwrap(),None).unwrap().with_full_awg_family_policy(true);
        let address="[2001:db8::1]:443".parse().unwrap();
        for sniffed in [false,true]{let result=if sniffed{router.connect_sniffed_destination(address,Some("video.test".into()))}else{router.connect_destination(address,Some("video.test".into()))};assert_eq!(result.await.err().unwrap().to_string(),"AWG_PROXY_ADDRESS_FAMILY_UNSUPPORTED");}
        assert_eq!(router.udp_destination(address,Some("video.test".into())).await.err().unwrap().to_string(),"AWG_PROXY_ADDRESS_FAMILY_UNSUPPORTED");
        assert_eq!(router.connect_destination(address,Some("blocked.test".into())).await.err().unwrap().kind(),io::ErrorKind::PermissionDenied);
        assert_eq!(router.connect_destination("192.0.2.1:443".parse().unwrap(),Some("video.test".into())).await.err().unwrap().to_string(),"192.0.2.1");
        let direct=NativeRouter::new(Arc::new(Ipv4RecordingProxy),vec![rule("DOMAIN","local.test","DIRECT"),rule("MATCH","","PROXY")],"127.0.0.1:9".parse().unwrap(),None).unwrap().with_full_awg_family_policy(true);
        let listener=tokio::net::TcpListener::bind("[::1]:0").await.unwrap();
        assert!(direct.connect_destination(listener.local_addr().unwrap(),Some("local.test".into())).await.is_ok());
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
    if crate::resource_diagnostics::enabled(host){crate::resource_diagnostics::emit(host,"RESOURCE_ROUTE",serde_json::json!({"stage":"routing","target":decision.target,"ruleIndex":decision.index,"transport":transport}));}
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
