use crate::core_api::{
    OrderedMatcher, OrderedRoutingProfile, RouteAction, RoutingProfile, RuleTarget,
};
use std::net::IpAddr;

pub fn classify(profile: &RoutingProfile, host: &str) -> RouteAction {
    let host = host.trim_end_matches('.').to_ascii_lowercase();
    for action in [RouteAction::Block, RouteAction::Proxy, RouteAction::Direct] {
        for rule in profile.rules.iter().filter(|rule| rule.action == action) {
            let pattern = rule.pattern.trim_start_matches("*.").to_ascii_lowercase();
            if host == pattern || host.ends_with(&format!(".{pattern}")) {
                return action;
            }
        }
    }
    profile.default_action
}

pub trait RuleDataSource {
    /// None means the required data set is unavailable, not "no match".
    fn geo_ip_contains(&self, country: &str, ip: IpAddr) -> Option<bool>;
    fn geo_site_contains(&self, category: &str, host: &str) -> Option<bool>;
    fn rule_set_contains(&self, name: &str, host: Option<&str>, ip: Option<IpAddr>)
        -> Option<bool>;
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RouteEvaluationError {
    MissingData(String),
    DnsRequired,
    InvalidCidr(String),
}

/// First matching rule wins. Missing GEO/rule-set data stops evaluation so a
/// later DIRECT/MATCH rule cannot silently leak traffic.
pub fn evaluate_ordered(
    profile: &OrderedRoutingProfile,
    host: Option<&str>,
    ip: Option<IpAddr>,
    data: &impl RuleDataSource,
) -> Result<RuleTarget, RouteEvaluationError> {
    let host = host.map(|value| value.trim_end_matches('.').to_ascii_lowercase());
    for rule in &profile.rules {
        let matched = match &rule.matcher {
            OrderedMatcher::Domain(value) => {
                host.as_deref() == Some(value.to_ascii_lowercase().as_str())
            }
            OrderedMatcher::DomainSuffix(value) => host.as_deref().is_some_and(|host| {
                let suffix = value.trim_start_matches('.').to_ascii_lowercase();
                host == suffix || host.ends_with(&format!(".{suffix}"))
            }),
            OrderedMatcher::DomainKeyword(value) => host
                .as_deref()
                .is_some_and(|host| host.contains(&value.to_ascii_lowercase())),
            OrderedMatcher::IpCidr(value) => match ip {
                Some(ip) => cidr_contains(value, ip)?,
                None if rule.no_resolve => false,
                None => return Err(RouteEvaluationError::DnsRequired),
            },
            OrderedMatcher::GeoIp(country) => match ip {
                Some(ip) => data
                    .geo_ip_contains(country, ip)
                    .ok_or_else(|| RouteEvaluationError::MissingData(format!("GEOIP:{country}")))?,
                None if rule.no_resolve => false,
                None => return Err(RouteEvaluationError::DnsRequired),
            },
            OrderedMatcher::GeoSite(category) => match host.as_deref() {
                Some(host) => data.geo_site_contains(category, host).ok_or_else(|| {
                    RouteEvaluationError::MissingData(format!("GEOSITE:{category}"))
                })?,
                None => false,
            },
            OrderedMatcher::RuleSet(name) => data
                .rule_set_contains(name, host.as_deref(), ip)
                .ok_or_else(|| RouteEvaluationError::MissingData(format!("RULE-SET:{name}")))?,
            OrderedMatcher::Match => true,
        };
        if matched {
            return Ok(rule.target.clone());
        }
    }
    Ok(profile.final_target.clone())
}

fn cidr_contains(cidr: &str, address: IpAddr) -> Result<bool, RouteEvaluationError> {
    let (network, prefix) = cidr
        .split_once('/')
        .ok_or_else(|| RouteEvaluationError::InvalidCidr(cidr.into()))?;
    let network: IpAddr = network
        .parse()
        .map_err(|_| RouteEvaluationError::InvalidCidr(cidr.into()))?;
    let prefix: u32 = prefix
        .parse()
        .map_err(|_| RouteEvaluationError::InvalidCidr(cidr.into()))?;
    match (network, address) {
        (IpAddr::V4(network), IpAddr::V4(address)) if prefix <= 32 => {
            let mask = if prefix == 0 {
                0
            } else {
                u32::MAX << (32 - prefix)
            };
            Ok(u32::from(network) & mask == u32::from(address) & mask)
        }
        (IpAddr::V6(network), IpAddr::V6(address)) if prefix <= 128 => {
            let mask = if prefix == 0 {
                0
            } else {
                u128::MAX << (128 - prefix)
            };
            Ok(u128::from(network) & mask == u128::from(address) & mask)
        }
        (IpAddr::V4(_), IpAddr::V4(_)) | (IpAddr::V6(_), IpAddr::V6(_)) => {
            Err(RouteEvaluationError::InvalidCidr(cidr.into()))
        }
        _ => Ok(false),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::core_api::RouteRule;

    struct NoData;
    impl RuleDataSource for NoData {
        fn geo_ip_contains(&self, _: &str, _: IpAddr) -> Option<bool> {
            None
        }
        fn geo_site_contains(&self, _: &str, _: &str) -> Option<bool> {
            None
        }
        fn rule_set_contains(&self, _: &str, _: Option<&str>, _: Option<IpAddr>) -> Option<bool> {
            None
        }
    }

    #[test]
    fn ordered_rules_keep_first_match_and_group() {
        use crate::core_api::OrderedRule;
        let route = OrderedRoutingProfile {
            rules: vec![
                OrderedRule {
                    matcher: OrderedMatcher::DomainSuffix("example.com".into()),
                    target: RuleTarget::Direct,
                    no_resolve: false,
                },
                OrderedRule {
                    matcher: OrderedMatcher::Domain("a.example.com".into()),
                    target: RuleTarget::ProxyGroup("Auto".into()),
                    no_resolve: false,
                },
            ],
            final_target: RuleTarget::Reject,
        };
        assert_eq!(
            evaluate_ordered(&route, Some("a.example.com"), None, &NoData),
            Ok(RuleTarget::Direct)
        );
    }

    #[test]
    fn missing_geo_data_fails_closed() {
        use crate::core_api::OrderedRule;
        let route = OrderedRoutingProfile {
            rules: vec![OrderedRule {
                matcher: OrderedMatcher::GeoSite("youtube".into()),
                target: RuleTarget::ProxyGroup("Auto".into()),
                no_resolve: false,
            }],
            final_target: RuleTarget::Direct,
        };
        assert_eq!(
            evaluate_ordered(&route, Some("youtube.com"), None, &NoData),
            Err(RouteEvaluationError::MissingData("GEOSITE:youtube".into()))
        );
    }

    #[test]
    fn cidr_matches_ipv4_and_ipv6() {
        assert_eq!(
            cidr_contains("10.0.0.0/8", "10.1.2.3".parse().unwrap()),
            Ok(true)
        );
        assert_eq!(
            cidr_contains("2001:db8::/32", "2001:db8::1".parse().unwrap()),
            Ok(true)
        );
        assert_eq!(
            cidr_contains("10.0.0.0/33", "10.1.2.3".parse().unwrap()),
            Err(RouteEvaluationError::InvalidCidr("10.0.0.0/33".into()))
        );
    }

    #[test]
    fn block_wins_before_proxy_and_direct() {
        let profile = RoutingProfile {
            name: "Test".into(),
            default_action: RouteAction::Direct,
            rules: vec![
                RouteRule {
                    pattern: "example.com".into(),
                    action: RouteAction::Direct,
                },
                RouteRule {
                    pattern: "example.com".into(),
                    action: RouteAction::Proxy,
                },
                RouteRule {
                    pattern: "example.com".into(),
                    action: RouteAction::Block,
                },
            ],
        };
        assert_eq!(classify(&profile, "sub.example.com"), RouteAction::Block);
        assert_eq!(classify(&profile, "other.example"), RouteAction::Direct);
    }
}
