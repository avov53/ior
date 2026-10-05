use crate::{
    ipc_contract::WireDns,
    native_router::{emit_decision, NativeRouter},
    tun_names::{self, NameCache, Question},
    udp_relay::DatagramRelay,
};
use std::{
    io,
    sync::{
        atomic::{AtomicUsize, Ordering},
        Arc,
    },
    time::Duration,
};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    sync::Mutex,
};

/// DNS sockets are pooled by route target, never returned through the TUN adapter.
pub struct TunDns {
    router: Arc<NativeRouter>,
    pub names: Arc<NameCache>,
    upstreams: Vec<String>,
    bootstrap: String,
    ipv4_only: bool,
    ipv6_only: bool,
    proxy: std::collections::HashMap<String, Vec<Mutex<Option<Box<dyn DatagramRelay>>>>>,
    direct: Vec<Mutex<Option<Box<dyn DatagramRelay>>>>,
    next: AtomicUsize,
    cache: crate::dns_response_cache::DnsResponseCache,
    inflight: std::sync::Mutex<
        std::collections::HashMap<(Vec<u8>, String, bool), std::sync::Weak<Mutex<()>>>,
    >,
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        ipc_contract::WireRule,
        proxy_inbound::{ConnectFuture, OutboundConnector},
        udp_relay::{ReceiveFuture, SendFuture, UdpFuture},
    };
    struct ReferenceDns;
    struct HedgedDns;
    struct StalledRelay;
    impl DatagramRelay for StalledRelay {
        fn send<'a>(&'a mut self, _: &'a [u8]) -> SendFuture<'a> {
            Box::pin(async { Ok(()) })
        }
        fn receive(&mut self) -> ReceiveFuture<'_> {
            Box::pin(std::future::pending())
        }
    }
    impl OutboundConnector for HedgedDns {
        fn dns_hedge_delay(&self) -> Duration {
            Duration::from_millis(150)
        }
        fn connect(&self, _: String, _: u16) -> ConnectFuture {
            Box::pin(async { Err(io::ErrorKind::Unsupported.into()) })
        }
        fn udp(&self, host: String, _: u16) -> UdpFuture {
            Box::pin(async move {
                Ok(if host == "8.8.8.8" {
                    Box::new(StalledRelay) as Box<dyn DatagramRelay>
                } else {
                    Box::new(ReferenceRelay(None))
                })
            })
        }
    }
    struct ReferenceRelay(Option<Vec<u8>>);
    impl DatagramRelay for ReferenceRelay {
        fn send<'a>(&'a mut self, packet: &'a [u8]) -> SendFuture<'a> {
            Box::pin(async move {
                let mut answer = packet.to_vec();
                answer[2] = 0x81;
                answer[3] = 0x80;
                answer[7] = 1;
                answer.extend([0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 10, 0, 4, 203, 0, 113, 7]);
                self.0 = Some(answer);
                Ok(())
            })
        }
        fn receive(&mut self) -> ReceiveFuture<'_> {
            Box::pin(async {
                self.0
                    .take()
                    .ok_or_else(|| io::ErrorKind::UnexpectedEof.into())
            })
        }
    }
    impl OutboundConnector for ReferenceDns {
        fn connect(&self, _: String, _: u16) -> ConnectFuture {
            Box::pin(async { Err(io::ErrorKind::Unsupported.into()) })
        }
        fn udp(&self, host: String, port: u16) -> UdpFuture {
            assert_eq!(host, "8.8.8.8");
            assert_eq!(port, 53);
            Box::pin(async { Ok(Box::new(ReferenceRelay(None)) as Box<dyn DatagramRelay>) })
        }
    }
    fn packet(kind: u16) -> Vec<u8> {
        let mut packet = vec![1, 2, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0];
        packet.extend([
            3, b'c', b'd', b'n', 7, b'e', b'x', b'a', b'm', b'p', b'l', b'e', 0,
        ]);
        packet.extend(kind.to_be_bytes());
        packet.extend([0, 1]);
        packet
    }
    fn resolver(target: &str) -> TunDns {
        let router = NativeRouter::new(
            Arc::new(ReferenceDns),
            vec![WireRule {
                kind: "MATCH".into(),
                value: "".into(),
                target: target.into(),
                no_resolve: true,
                source: "ior".into(),
            }],
            "127.0.0.1:9".parse().unwrap(),
            None,
        )
        .unwrap();
        TunDns::new(
            Arc::new(router),
            &WireDns {
                bootstrap: "127.0.0.1".into(),
                upstream: "8.8.8.8".into(),
                ipv4_only: true,
                upstreams: vec![],
            },
        )
        .unwrap()
    }
    #[tokio::test]
    async fn tun_dns_hedges_cancels_and_separates_resolver_cache() {
        let router = NativeRouter::new(
            Arc::new(HedgedDns),
            vec![WireRule {
                kind: "MATCH".into(),
                value: String::new(),
                target: "PROXY".into(),
                no_resolve: true,
                source: "ior".into(),
            }],
            "127.0.0.1:9".parse().unwrap(),
            None,
        )
        .unwrap();
        let dns = TunDns::new(
            Arc::new(router),
            &WireDns {
                bootstrap: "127.0.0.1".into(),
                upstream: "8.8.8.8".into(),
                upstreams: vec!["8.8.8.8".into(), "1.1.1.1".into()],
                ipv4_only: true,
            },
        )
        .unwrap();
        let start = tokio::time::Instant::now();
        let query = packet(1);
        let reply = dns.query(&query, false).await.unwrap();
        assert_eq!(reply[7], 1);
        assert!(start.elapsed() >= Duration::from_millis(150));
        assert!(start.elapsed() < Duration::from_millis(400));
        assert!(dns.proxy["8.8.8.8"]
            .iter()
            .all(|slot| slot.try_lock().unwrap().is_none()));
        assert!(dns.cache.get(&query, "PROXY|8.8.8.8", false).is_none());
        assert!(dns.cache.get(&query, "PROXY|1.1.1.1", false).is_some());
        assert!(dns.cache.get(&query, "DIRECT|1.1.1.1", false).is_none());
        assert!(dns.cache.get(&query, "PROXY|1.1.1.1", true).is_none());
        let start = tokio::time::Instant::now();
        assert_eq!(dns.query(&query, false).await.unwrap(), reply);
        assert!(start.elapsed() < Duration::from_millis(100));
    }
    #[tokio::test]
    async fn tun_dns_routes_proxy_answers_and_caches_the_name_before_reply() {
        let dns = resolver("PROXY");
        let answer = dns.query(&packet(1), false).await.unwrap();
        assert_eq!(answer[7], 1);
        assert_eq!(
            dns.names.lookup("203.0.113.7".parse().unwrap()),
            Some("cdn.example".into())
        );
        let mut query = packet(1);
        query[0] = 9;
        let answer = dns.query(&query, false).await.unwrap();
        assert_eq!(answer[0], 9);
    }
    #[tokio::test]
    async fn tun_dns_reject_and_ipv4_only_return_explicit_replies() {
        let denied = resolver("REJECT").query(&packet(1), false).await.unwrap();
        assert_eq!(denied[3] & 15, 5);
        assert_eq!(denied[7], 0);
        let nodata = resolver("PROXY").query(&packet(28), false).await.unwrap();
        assert_eq!(nodata[3] & 15, 0);
        assert_eq!(nodata[7], 0);
    }
    #[tokio::test]
    async fn dns_uses_free_carrier_instead_of_waiting_for_a_busy_one() {
        let dns = resolver("PROXY");
        let _busy = dns.proxy["8.8.8.8"][0].lock().await;
        let answer = tokio::time::timeout(Duration::from_millis(200), dns.query(&packet(1), false))
            .await
            .unwrap()
            .unwrap();
        assert_eq!(answer[7], 1);
    }
    struct CountRelay {
        count: Arc<std::sync::atomic::AtomicUsize>,
        inner: ReferenceRelay,
    }
    impl DatagramRelay for CountRelay {
        fn send<'a>(&'a mut self, packet: &'a [u8]) -> SendFuture<'a> {
            Box::pin(async move {
                self.count.fetch_add(1, Ordering::SeqCst);
                tokio::time::sleep(Duration::from_millis(20)).await;
                self.inner.send(packet).await
            })
        }
        fn receive(&mut self) -> ReceiveFuture<'_> {
            self.inner.receive()
        }
    }
    #[tokio::test]
    async fn duplicate_dns_requests_share_one_exchange_and_keep_their_transaction_ids() {
        let dns = Arc::new(resolver("PROXY"));
        let count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        *dns.proxy["8.8.8.8"][0].lock().await = Some(Box::new(CountRelay {
            count: count.clone(),
            inner: ReferenceRelay(None),
        }));
        let mut tasks = tokio::task::JoinSet::new();
        for id in 0..16u8 {
            let dns = dns.clone();
            tasks.spawn(async move {
                let mut query = packet(1);
                query[0] = id;
                let answer = dns.query(&query, false).await.unwrap();
                assert_eq!(answer[0], id);
            });
        }
        while let Some(result) = tasks.join_next().await {
            result.unwrap();
        }
        assert_eq!(count.load(Ordering::SeqCst), 1);
    }
    #[tokio::test]
    async fn ipv4_only_proxy_does_not_suppress_direct_aaaa() {
        let dns = resolver("DIRECT");
        *dns.direct[0].lock().await = Some(Box::new(ReferenceRelay(None)));
        let answer = dns.query(&packet(28), false).await.unwrap();
        assert_eq!(answer[7], 1);
    }
    #[test]
    fn ipv6_bootstrap_is_valid_without_global_ipv4_requirement() {
        let source = resolver("DIRECT");
        let dns = TunDns::new(
            source.router.clone(),
            &WireDns {
                bootstrap: "::1".into(),
                upstream: "2001:4860:4860::8888".into(),
                upstreams: vec![],
                ipv4_only: false,
            },
        )
        .unwrap();
        assert_eq!(dns.bootstrap, "::1");
        assert!(TunDns::new(
            source.router,
            &WireDns {
                bootstrap: "not-an-address".into(),
                upstream: "8.8.8.8".into(),
                upstreams: vec![],
                ipv4_only: false,
            }
        )
        .is_err());
    }
    struct Ipv4Reference;
    impl OutboundConnector for Ipv4Reference {
        fn ipv4_only(&self) -> bool {
            true
        }
        fn connect(&self, host: String, port: u16) -> ConnectFuture {
            ReferenceDns.connect(host, port)
        }
        fn udp(&self, host: String, port: u16) -> UdpFuture {
            ReferenceDns.udp(host, port)
        }
    }
    #[tokio::test]
    async fn transport_capability_prevents_unusable_aaaa_even_if_contract_flag_is_false() {
        let router = NativeRouter::new(
            Arc::new(Ipv4Reference),
            vec![WireRule {
                kind: "MATCH".into(),
                value: String::new(),
                target: "PROXY".into(),
                no_resolve: true,
                source: "ior".into(),
            }],
            "127.0.0.1:9".parse().unwrap(),
            None,
        )
        .unwrap();
        let dns = TunDns::new(
            Arc::new(router),
            &WireDns {
                bootstrap: "127.0.0.1".into(),
                upstream: "8.8.8.8".into(),
                upstreams: vec![],
                ipv4_only: false,
            },
        )
        .unwrap();
        let response = dns.query(&packet(28), false).await.unwrap();
        assert_eq!(response[7], 0);
        assert_eq!(response[3] & 15, 0);
        assert_eq!(dns.query(&packet(1), false).await.unwrap()[7], 1);
    }
}
impl TunDns {
    pub fn new(router: Arc<NativeRouter>, dns: &WireDns) -> io::Result<Self> {
        if dns.upstream.parse::<std::net::IpAddr>().is_err()
            || dns.bootstrap.parse::<std::net::IpAddr>().is_err()
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "TUN_DNS_IP_REQUIRED",
            ));
        }
        let upstreams = if dns.upstreams.is_empty() {
            vec![dns.upstream.clone()]
        } else {
            dns.upstreams.clone()
        };
        if upstreams.len() > 4
            || upstreams
                .iter()
                .any(|server| server.parse::<std::net::IpAddr>().is_err())
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "DNS_RESOLVER_LIST_INVALID",
            ));
        }
        Ok(Self {
            router: router.clone(),
            names: Arc::new(NameCache::default()),
            proxy: upstreams
                .iter()
                .map(|server| (server.clone(), (0..4).map(|_| Mutex::new(None)).collect()))
                .collect(),
            upstreams,
            bootstrap: dns.bootstrap.clone(),
            ipv4_only: dns.ipv4_only || router.proxy_ipv4_only(),
            ipv6_only: router.proxy_ipv6_only(),
            direct: (0..4).map(|_| Mutex::new(None)).collect(),
            next: AtomicUsize::new(0),
            cache: crate::dns_response_cache::DnsResponseCache::default(),
            inflight: std::sync::Mutex::new(std::collections::HashMap::new()),
        })
    }
    pub async fn query(&self, packet: &[u8], tcp: bool) -> io::Result<Vec<u8>> {
        let question = tun_names::question(packet)
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "TUN_DNS_QUERY_INVALID"))?;
        let decision = self.router.decide(&question.name, None).await?;
        emit_decision(
            &question.name,
            53,
            &decision,
            if tcp { "dns-tcp" } else { "dns-udp" },
        );
        if decision.target == "REJECT" {
            crate::trace::code("dns", "DNS_RULE_REJECTED");
            return Ok(tun_names::empty_response(packet, &question, 5));
        }
        if self.ipv4_only && decision.target == "PROXY" && question.kind == 28 {
            crate::trace::code("dns", "DNS_IPV6_NODATA");
            return Ok(tun_names::empty_response(packet, &question, 0));
        }
        if self.ipv6_only && decision.target == "PROXY" && question.kind == 1 {
            crate::trace::code("dns", "DNS_IPV4_NODATA");
            return Ok(tun_names::empty_response(packet, &question, 0));
        }
        let flight = {
            let mut flights = self.inflight.lock().map_err(|_| io::ErrorKind::Other)?;
            flights.retain(|_, flight| flight.strong_count() > 0);
            let key = (packet[2..].to_vec(), decision.target.clone(), tcp);
            if let Some(flight) = flights.get(&key).and_then(std::sync::Weak::upgrade) {
                flight
            } else {
                if flights.len() >= 256 {
                    return Err(io::Error::new(
                        io::ErrorKind::WouldBlock,
                        "TUN_DNS_INFLIGHT_LIMIT",
                    ));
                }
                let flight = Arc::new(Mutex::new(()));
                flights.insert(key, Arc::downgrade(&flight));
                flight
            }
        };
        let _flight = flight.lock().await;
        let endpoints = if decision.target == "DIRECT" {
            vec![self.bootstrap.clone()]
        } else {
            self.upstreams.clone()
        };
        for endpoint in &endpoints {
            if let Some(response) =
                self.cache
                    .get(packet, &format!("{}|{}", decision.target, endpoint), tcp)
            {
                let _ = self.names.observe(&response, &question);
                crate::trace::emit(
                    serde_json::json!({"stage":"dns","code":"DNS_CACHE_HIT","resolver":endpoint}),
                );
                return Ok(response);
            }
        }
        let target = &decision.target;
        let question_ref = &question;
        let response = tokio::time::timeout(
            Duration::from_secs(8),
            crate::dns_exchange::hedged_with_delay(
                &endpoints,
                if target == "PROXY" {
                    self.router.proxy_dns_hedge_delay()
                } else {
                    Duration::from_millis(500)
                },
                |endpoint| async move {
                    let response = if tcp {
                        let mut stream = self.router.connect_target(target, &endpoint, 53).await?;
                        let size =
                            u16::try_from(packet.len()).map_err(|_| io::ErrorKind::InvalidData)?;
                        stream.write_u16(size).await?;
                        stream.write_all(packet).await?;
                        stream.flush().await?;
                        let length = stream.read_u16().await? as usize;
                        let mut response = vec![0; length];
                        stream.read_exact(&mut response).await?;
                        if !tun_names::matches_response(&response, question_ref) {
                            return Err(io::Error::new(
                                io::ErrorKind::InvalidData,
                                "TUN_DNS_RESPONSE_MISMATCH",
                            ));
                        }
                        if matches!(response[3] & 15, 2 | 5) {
                            return Err(io::Error::new(
                                io::ErrorKind::InvalidData,
                                "TUN_DNS_SERVER_FAILURE",
                            ));
                        }
                        Ok(response)
                    } else {
                        self.query_udp(packet, question_ref, target, &endpoint)
                            .await
                    }?;
                    self.cache
                        .insert(packet, &format!("{target}|{endpoint}"), tcp, &response);
                    Ok(response)
                },
            ),
        )
        .await
        .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "TUN_DNS_TIMEOUT"))??;
        let _ = self.names.observe(&response, &question);
        crate::trace::emit(
            serde_json::json!({"stage":"dns","code":"DNS_RESPONSE_READY","target":decision.target,"sentBytes":packet.len(),"receivedBytes":response.len()}),
        );
        Ok(response)
    }
    async fn query_udp(
        &self,
        packet: &[u8],
        question: &Question,
        target: &str,
        endpoint: &str,
    ) -> io::Result<Vec<u8>> {
        let pool = if target == "DIRECT" {
            &self.direct
        } else {
            self.proxy
                .get(endpoint)
                .ok_or(io::ErrorKind::InvalidInput)?
        };
        let index = self.next.fetch_add(1, Ordering::Relaxed) % pool.len();
        // A slow exchange must not queue an unrelated DNS name behind it when
        // another carrier is idle. Keep the pool bounded and retain fair rotation.
        let available =
            (0..pool.len()).find_map(|offset| pool[(index + offset) % pool.len()].try_lock().ok());
        let mut slot = match available {
            Some(slot) => slot,
            None => pool[index].lock().await,
        };
        let mut relay = match slot.take() {
            Some(relay) => relay,
            None => self.router.udp_target(target, endpoint, 53).await?,
        };
        // Keep the deadline inside the lock so cancellation always discards a relay
        // which could still contain an answer belonging to the preceding request.
        let result = tokio::time::timeout(Duration::from_secs(6), async {
            relay.send(packet).await?;
            for _ in 0..16 {
                let response = relay.receive().await?;
                if tun_names::matches_response(&response, question) {
                    if matches!(response[3] & 15, 2 | 5) {
                        return Err(io::Error::new(
                            io::ErrorKind::InvalidData,
                            "TUN_DNS_SERVER_FAILURE",
                        ));
                    }
                    return Ok(response);
                }
            }
            Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "TUN_DNS_RESPONSE_MISMATCH",
            ))
        })
        .await
        .unwrap_or_else(|_| Err(io::Error::new(io::ErrorKind::TimedOut, "TUN_DNS_TIMEOUT")));
        if result.is_ok() {
            *slot = Some(relay);
        }
        result
    }
}
