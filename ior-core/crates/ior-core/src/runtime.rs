use crate::{
    core_api::{DnsProfile, ProxyGroupMode, RouteAction, RoutingProfile, ServerProfile},
    dns_engine::{bootstrap_plan, BootstrapPlan},
    protocols,
};

#[derive(Debug, Clone)]
pub struct RuntimePlan {
    pub profile: ServerProfile,
    pub route: RoutingProfile,
    pub bootstrap: BootstrapPlan,
    pub native_ready: bool,
}

impl RuntimePlan {
    pub fn for_profile(profile: ServerProfile) -> Self {
        let dns = DnsProfile {
            server: "1.1.1.1".into(),
            bootstrap: "1.1.1.1".into(),
            ipv4_only: true,
        };
        let native_ready = protocols::capability(profile.protocol).native_transport;
        Self {
            profile,
            route: RoutingProfile {
                name: "Default".into(),
                default_action: RouteAction::Proxy,
                rules: vec![],
            },
            bootstrap: bootstrap_plan(&dns).expect("default bootstrap is valid"),
            native_ready,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MemberHealth {
    pub id: String,
    pub latency_ms: Option<u32>,
    pub healthy: bool,
}

/// Statistics from a bounded set of probes. Missing replies count as loss;
/// successful replies are summarized by the median rather than one lucky RTT.
#[derive(Debug, Clone, PartialEq)]
pub struct LinkQuality {
    pub median_rtt_ms: f64,
    pub jitter_ms: f64,
    pub loss_ratio: f64,
}

pub fn measure_link(probes: &[Option<u32>]) -> Option<LinkQuality> {
    if probes.is_empty() {
        return None;
    }
    let mut replies: Vec<u32> = probes.iter().flatten().copied().collect();
    if replies.is_empty() {
        return None;
    }
    replies.sort_unstable();
    let middle = replies.len() / 2;
    let median = if replies.len() % 2 == 0 {
        (f64::from(replies[middle - 1]) + f64::from(replies[middle])) / 2.0
    } else {
        f64::from(replies[middle])
    };
    let successful: Vec<f64> = probes
        .iter()
        .flatten()
        .map(|value| f64::from(*value))
        .collect();
    let jitter = if successful.len() < 2 {
        0.0
    } else {
        successful
            .windows(2)
            .map(|pair| (pair[1] - pair[0]).abs())
            .sum::<f64>()
            / (successful.len() - 1) as f64
    };
    Some(LinkQuality {
        median_rtt_ms: median,
        jitter_ms: jitter,
        loss_ratio: 1.0 - successful.len() as f64 / probes.len() as f64,
    })
}

/// Plans a server for *new* flows only. Keep the current server unless another
/// healthy one is materially better; this avoids flapping or tearing down flows.
pub fn choose_gaming_member<'a>(
    members: &'a [(String, LinkQuality)],
    current_id: Option<&str>,
) -> Option<&'a str> {
    let score = |quality: &LinkQuality| {
        quality.median_rtt_ms + quality.jitter_ms * 2.0 + quality.loss_ratio * 500.0
    };
    let best = members
        .iter()
        .filter(|(_, quality)| quality.loss_ratio <= 0.1)
        .min_by(|a, b| score(&a.1).total_cmp(&score(&b.1)))?;
    if let Some(current) = current_id.and_then(|id| members.iter().find(|(name, _)| name == id)) {
        if current.1.loss_ratio <= 0.1 && score(&best.1) >= score(&current.1) * 0.9 {
            return Some(&current.0);
        }
    }
    Some(&best.0)
}

pub fn choose_group_member<'a>(
    mode: ProxyGroupMode,
    members: &'a [MemberHealth],
    manual_id: Option<&str>,
    rotation: usize,
) -> Option<&'a MemberHealth> {
    match mode {
        ProxyGroupMode::Manual => {
            manual_id.and_then(|id| members.iter().find(|member| member.id == id))
        }
        ProxyGroupMode::Auto => members
            .iter()
            .filter(|member| member.healthy)
            .min_by_key(|member| member.latency_ms.unwrap_or(u32::MAX)),
        ProxyGroupMode::Fallback => members.iter().find(|member| member.healthy),
        ProxyGroupMode::LoadBalance => {
            let healthy: Vec<_> = members.iter().filter(|member| member.healthy).collect();
            healthy.get(rotation % healthy.len().max(1)).copied()
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn auto_prefers_lowest_healthy_latency() {
        let members = [
            MemberHealth {
                id: "a".into(),
                latency_ms: Some(15),
                healthy: false,
            },
            MemberHealth {
                id: "b".into(),
                latency_ms: Some(80),
                healthy: true,
            },
            MemberHealth {
                id: "c".into(),
                latency_ms: Some(30),
                healthy: true,
            },
        ];
        assert_eq!(
            choose_group_member(ProxyGroupMode::Auto, &members, None, 0)
                .unwrap()
                .id,
            "c"
        );
        assert_eq!(
            choose_group_member(ProxyGroupMode::Fallback, &members, None, 0)
                .unwrap()
                .id,
            "b"
        );
        assert_eq!(
            choose_group_member(ProxyGroupMode::LoadBalance, &members, None, 1)
                .unwrap()
                .id,
            "c"
        );
    }

    #[test]
    fn gaming_quality_uses_median_jitter_and_loss() {
        let quality = measure_link(&[Some(20), Some(22), None, Some(100)]).unwrap();
        assert_eq!(quality.median_rtt_ms, 22.0);
        assert_eq!(quality.jitter_ms, 40.0);
        assert_eq!(quality.loss_ratio, 0.25);
    }

    #[test]
    fn gaming_selection_avoids_flapping_and_loss() {
        let members = vec![
            (
                "current".into(),
                LinkQuality {
                    median_rtt_ms: 40.0,
                    jitter_ms: 2.0,
                    loss_ratio: 0.0,
                },
            ),
            (
                "slightly-faster".into(),
                LinkQuality {
                    median_rtt_ms: 38.0,
                    jitter_ms: 2.0,
                    loss_ratio: 0.0,
                },
            ),
            (
                "lossy".into(),
                LinkQuality {
                    median_rtt_ms: 8.0,
                    jitter_ms: 2.0,
                    loss_ratio: 0.4,
                },
            ),
        ];
        assert_eq!(
            choose_gaming_member(&members, Some("current")),
            Some("current")
        );
        assert_eq!(
            choose_gaming_member(&members, None),
            Some("slightly-faster")
        );
    }
}
