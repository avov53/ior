#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Protocol {
    Vless,
    Vmess,
    Trojan,
    Shadowsocks,
    Hysteria2,
    WireGuard,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ServerProfile {
    pub protocol: Protocol,
    pub name: String,
    pub server: String,
    pub port: u16,
    pub credential: String,
    pub server_name: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RouteAction {
    Block,
    Proxy,
    Direct,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RouteRule {
    pub pattern: String,
    pub action: RouteAction,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RoutingProfile {
    pub name: String,
    pub default_action: RouteAction,
    pub rules: Vec<RouteRule>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProxyGroupMode {
    Manual,
    Auto,
    Fallback,
    LoadBalance,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProxyGroup {
    pub name: String,
    pub mode: ProxyGroupMode,
    pub members: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DnsProfile {
    pub server: String,
    pub bootstrap: String,
    pub ipv4_only: bool,
}

/// Order and destination are part of a Clash rule's meaning. Unlike the
/// legacy ior buckets, this model must never regroup rules by action.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum OrderedMatcher {
    Domain(String),
    DomainSuffix(String),
    DomainKeyword(String),
    IpCidr(String),
    GeoIp(String),
    GeoSite(String),
    RuleSet(String),
    Match,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RuleTarget {
    Direct,
    Reject,
    ProxyGroup(String),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OrderedRule {
    pub matcher: OrderedMatcher,
    pub target: RuleTarget,
    pub no_resolve: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OrderedRoutingProfile {
    pub rules: Vec<OrderedRule>,
    pub final_target: RuleTarget,
}
