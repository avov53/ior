//! WireGuard IP tunnel outbound. BoringTun provides encryption; smoltcp
//! provides local TCP/UDP sockets without changing the OS routing table.
use crate::{
    ipc_contract::{WireNetwork, WireProfile},
    proxy_inbound::{ConnectFuture, OutboundConnector, RelayStream},
    udp_relay::{DatagramRelay, ReceiveFuture, SendFuture, UdpFuture},
};
use base64::Engine;
use boringtun::noise::{Tunn, TunnResult};
use smoltcp::{
    iface::{Config, Interface, SocketSet},
    phy::{Device, DeviceCapabilities, Medium, RxToken, TxToken},
    time::Instant,
    wire::{
        HardwareAddress, IpAddress, IpCidr, IpProtocol, Ipv4Packet, Ipv4Repr, Ipv6Packet, Ipv6Repr,
        UdpPacket, UdpRepr,
    },
};
use std::{
    collections::{HashMap, VecDeque},
    io,
    net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr},
    sync::{
        atomic::{AtomicU16, Ordering},
        Arc, Mutex,
    },
    time::Duration,
};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    sync::mpsc,
};
type Packet = crate::packet_budget::Packet;
pub(crate) type Flows = Arc<Mutex<HashMap<(u8, u16), (SocketAddr, PacketSender)>>>;
#[derive(Clone)]
pub(crate) struct PacketSender {
    counters:Option<Arc<IngressCounters>>,
    sender: mpsc::Sender<Packet>,
    budget: Option<Arc<crate::packet_budget::Budget>>,
    native_budget: Option<Arc<crate::packet_budget::Budget>>,
    native_ingress: Arc<std::sync::OnceLock<NativeIngress>>,
}
const NATIVE_PENDING_PACKETS:usize=2048;
#[derive(Default)]
struct IngressCounters{received:std::sync::atomic::AtomicU64,delivered:std::sync::atomic::AtomicU64,dropped_bytes:std::sync::atomic::AtomicU64,dropped:std::sync::atomic::AtomicU64,wait_us:std::sync::atomic::AtomicU64,closed:std::sync::atomic::AtomicBool}
impl IngressCounters{
    fn discard(&self,bytes:usize){self.dropped_bytes.fetch_add(bytes as u64,Ordering::Relaxed);self.dropped.fetch_add(1,Ordering::Relaxed);}
    fn snapshot(&self)->[u64;6]{let received=self.received.load(Ordering::Relaxed);let delivered=self.delivered.load(Ordering::Relaxed);let closed=self.closed.load(Ordering::Relaxed);[received,delivered,if closed{0}else{received.saturating_sub(delivered).saturating_sub(self.dropped_bytes.load(Ordering::Relaxed))},self.dropped.load(Ordering::Relaxed),self.wait_us.load(Ordering::Relaxed),closed as u64]}
}
struct NativeIngress {sender:mpsc::Sender<Packet>,task:tokio::task::AbortHandle,counters:Option<Arc<IngressCounters>>}
impl Drop for NativeIngress {fn drop(&mut self){if let Some(c)=&self.counters{c.closed.store(true,Ordering::Relaxed);}self.task.abort();}}
impl NativeIngress {
    fn start(output:mpsc::Sender<Packet>,counters:Option<Arc<IngressCounters>>)->Self{
        let(sender,mut input)=mpsc::channel::<Packet>(NATIVE_PENDING_PACKETS);
        let stats=counters.clone();
        let task=tokio::spawn(async move{while let Some(packet)=input.recv().await{let size=packet.len();let started=stats.as_ref().map(|_|std::time::Instant::now());let result=output.send(packet).await;if let Some(c)=&stats{c.wait_us.fetch_add(started.unwrap().elapsed().as_micros().min(u64::MAX as u128) as u64,Ordering::Relaxed);if result.is_ok(){c.delivered.fetch_add(size as u64,Ordering::Relaxed);}else{c.discard(size);c.closed.store(true,Ordering::Relaxed);}}if result.is_err(){break;}}});
        Self{sender,task:task.abort_handle(),counters}
    }
}
impl PacketSender {
    fn new(sender:mpsc::Sender<Packet>,budget:Option<Arc<crate::packet_budget::Budget>>,native_budget:Option<Arc<crate::packet_budget::Budget>>)->Self{let counters=(native_budget.is_some()&&crate::resource_diagnostics::active()).then(||Arc::new(IngressCounters::default()));Self{sender,budget,native_budget,native_ingress:Default::default(),counters}}
    async fn deliver_native(&self,bytes:&[u8]) {
        if let Some(budget)=&self.native_budget {
            if let Some(c)=&self.counters{c.received.fetch_add(bytes.len() as u64,Ordering::Relaxed);}
            // Never await a single consumer in the shared crypto/timer loop.
            // This FIFO is lazy, count-bounded and charged to the session budget.
            if self.native_ingress.get().is_none(){
                match self.sender.try_reserve(){
                    Ok(slot)=>{match budget.copy(bytes){Ok(packet)=>{slot.send(packet);if let Some(c)=&self.counters{c.delivered.fetch_add(bytes.len() as u64,Ordering::Relaxed);}},Err(_)=>if let Some(c)=&self.counters{c.discard(bytes.len());}}return;},
                    Err(mpsc::error::TrySendError::Closed(_))=>{if let Some(c)=&self.counters{c.discard(bytes.len());c.closed.store(true,Ordering::Relaxed);}return;},
                    Err(mpsc::error::TrySendError::Full(_))=>(),
                }
            }
            // Once staged, all following packets use the same FIFO until close.
            // Do not let a newly free output slot overtake retained packets.
            let ingress=self.native_ingress.get_or_init(||NativeIngress::start(self.sender.clone(),self.counters.clone()));
            match ingress.sender.try_reserve(){
                Ok(slot)=>match budget.copy(bytes){Ok(packet)=>slot.send(packet),Err(_)=>if let Some(c)=&self.counters{c.discard(bytes.len());}},
                Err(mpsc::error::TrySendError::Full(_))=>{if let Some(c)=&self.counters{c.discard(bytes.len());}budget.queue_drop();crate::tunnel_metrics::RX_DROPPED.fetch_add(1,Ordering::Relaxed);},
                Err(mpsc::error::TrySendError::Closed(_))=>if let Some(c)=&self.counters{c.discard(bytes.len());c.closed.store(true,Ordering::Relaxed);},
            }
        }else{self.deliver(bytes);}
    }
    fn deliver(&self, bytes: &[u8]) {
        if let Some(c)=&self.counters{c.received.fetch_add(bytes.len() as u64,Ordering::Relaxed);}
        if let Ok(slot) = self.sender.try_reserve() {
            let packet=match &self.budget {Some(budget)=>match budget.copy(bytes){Ok(packet)=>packet,Err(_)=>{if let Some(c)=&self.counters{c.discard(bytes.len());}return;}},None=>bytes.to_vec().into()};
            slot.send(packet);
            if let Some(c)=&self.counters{c.delivered.fetch_add(bytes.len() as u64,Ordering::Relaxed);}
        } else {
            if let Some(c)=&self.counters{c.discard(bytes.len());if self.sender.is_closed(){c.closed.store(true,Ordering::Relaxed);}}
            crate::tunnel_metrics::RX_DROPPED.fetch_add(1, Ordering::Relaxed);
            if let Some(budget)=&self.budget {budget.queue_drop();}
        }
    }
}
#[derive(Clone)]
pub struct TunnelConnector {
    names: Arc<crate::tun_names::NameCache>,
    session: Arc<Session>,
    upstreams: Vec<SocketAddr>,
    dns_cache: Arc<crate::dns_response_cache::DnsResponseCache>,
    dns_inflight: Arc<Mutex<HashMap<(String, u16), std::sync::Weak<tokio::sync::Mutex<()>>>>>,
}
struct Session {
    packet_budget: Arc<crate::packet_budget::Budget>,
    awg: bool,
    tcp_tuning: crate::tunnel_tcp::Tuning,
    receive_budget: Arc<tokio::sync::Semaphore>,
    ip: Ipv4Addr,
    ip6: Option<Ipv6Addr>,
    allowed: Vec<(u32, u32)>,
    allowed6: Vec<(u128, u128)>,
    mtu: usize,
    tx: mpsc::Sender<Packet>,
    flows: Flows,
    port: AtomicU16,
    task: tokio::task::AbortHandle,
    metrics_task: tokio::task::AbortHandle,
    pmtu: crate::tunnel_pmtu::Cache,
}
impl Drop for Session {
    fn drop(&mut self) {
        self.task.abort();
        self.metrics_task.abort();
    }
}
fn fail(code: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidInput, code)
}
pub(crate) fn key32(value: &str) -> io::Result<[u8; 32]> {
    base64::engine::general_purpose::STANDARD
        .decode(value)
        .map_err(|_| fail("TUNNEL_KEY_INVALID"))?
        .try_into()
        .map_err(|_| fail("TUNNEL_KEY_INVALID"))
}
impl TunnelConnector {
    /// Profile validation must not open sockets, resolve DNS or start an adapter.
    pub fn validate_profile(profile: &WireProfile) -> io::Result<()> {
        key32(&profile.credential)?;
        key32(
            profile
                .options
                .peer_public_key
                .as_deref()
                .ok_or_else(|| fail("TUNNEL_PEER_KEY_MISSING"))?,
        )?;
        if let Some(value) = profile
            .options
            .preshared_key
            .as_deref()
            .filter(|s| !s.is_empty())
        {
            key32(value)?;
        }
        let locals = profile
            .options
            .local_addresses
            .as_ref()
            .filter(|v| !v.is_empty())
            .ok_or_else(|| fail("TUNNEL_LOCAL_ADDRESS_MISSING"))?;
        let allowed = profile
            .options
            .allowed_ips
            .as_ref()
            .filter(|v| !v.is_empty())
            .ok_or_else(|| fail("TUNNEL_ALLOWED_IPS_MISSING"))?;
        let mut local4 = false;
        let mut local6 = false;
        let mut allowed4 = false;
        let mut allowed6 = false;
        for (local, list) in [(true, locals), (false, allowed)] {
            for value in list {
                let (address, bits) = value
                    .split_once('/')
                    .ok_or_else(|| fail("TUNNEL_ADDRESS_INVALID"))?;
                let ip: IpAddr = address
                    .parse()
                    .map_err(|_| fail("TUNNEL_ADDRESS_INVALID"))?;
                let bits: u16 = bits.parse().map_err(|_| fail("TUNNEL_ADDRESS_INVALID"))?;
                if bits > if ip.is_ipv6() { 128 } else { 32 }
                    || (local && (ip.is_unspecified() || ip.is_multicast()))
                {
                    return Err(fail("TUNNEL_ADDRESS_INVALID"));
                }
                match (local, ip.is_ipv6()) {
                    (true, false) => local4 = true,
                    (true, true) => local6 = true,
                    (false, false) => allowed4 = true,
                    (false, true) => allowed6 = true,
                }
            }
        }
        if !(local4 && allowed4 || local6 && allowed6) {
            return Err(fail("TUNNEL_ALLOWED_ADDRESS_FAMILY_MISSING"));
        }
        if !(1280..=1500).contains(&profile.options.mtu.unwrap_or(1380)) {
            return Err(fail("TUNNEL_MTU_INVALID"));
        }
        if profile.protocol == "amneziawg" {crate::awg_adapter::validate_options(profile)?;}
        else if profile.protocol != "wireguard" {
            return Err(fail("PROTOCOL_UNSUPPORTED"));
        }
        Ok(())
    }
    pub async fn new(
        profile: &WireProfile,
        bootstrap: SocketAddr,
        network: Option<WireNetwork>,
    ) -> io::Result<Self> {
        Self::validate_profile(profile)?;
        let addresses = profile
            .options
            .local_addresses
            .as_ref()
            .ok_or_else(|| fail("TUNNEL_LOCAL_ADDRESS_MISSING"))?;
        let ip = addresses
            .iter()
            .find_map(|value| value.split('/').next()?.parse::<Ipv4Addr>().ok())
            .unwrap_or(Ipv4Addr::UNSPECIFIED);
        if ip.is_multicast() {
            return Err(fail("TUNNEL_LOCAL_ADDRESS_INVALID"));
        }
        let mut allowed = Vec::new();
        let ip6 = addresses
            .iter()
            .filter_map(|value| value.split('/').next()?.parse::<Ipv6Addr>().ok())
            .find(|ip| !ip.is_unspecified() && !ip.is_multicast());
        if ip.is_unspecified() && ip6.is_none() {
            return Err(fail("TUNNEL_LOCAL_ADDRESS_MISSING"));
        }
        let mut allowed6 = Vec::new();
        for value in profile
            .options
            .allowed_ips
            .as_ref()
            .ok_or_else(|| fail("TUNNEL_ALLOWED_IPS_MISSING"))?
        {
            let (address, prefix) = value
                .split_once('/')
                .ok_or_else(|| fail("TUNNEL_ALLOWED_IPS_INVALID"))?;
            if address.contains(':') {
                let address = address
                    .parse::<Ipv6Addr>()
                    .map_err(|_| fail("TUNNEL_ALLOWED_IPS_INVALID"))?;
                let bits = prefix
                    .parse::<u32>()
                    .map_err(|_| fail("TUNNEL_ALLOWED_IPS_INVALID"))?;
                if bits > 128 {
                    return Err(fail("TUNNEL_ALLOWED_IPS_INVALID"));
                }
                let mask = if bits == 0 {
                    0
                } else {
                    u128::MAX << (128 - bits)
                };
                allowed6.push((u128::from(address) & mask, mask));
                continue;
            }
            let address = address
                .parse::<Ipv4Addr>()
                .map_err(|_| fail("TUNNEL_ALLOWED_IPS_INVALID"))?;
            let bits = prefix
                .parse::<u32>()
                .map_err(|_| fail("TUNNEL_ALLOWED_IPS_INVALID"))?;
            if bits > 32 {
                return Err(fail("TUNNEL_ALLOWED_IPS_INVALID"));
            }
            let mask = if bits == 0 {
                0
            } else {
                u32::MAX << (32 - bits)
            };
            allowed.push((u32::from(address) & mask, mask));
        }
        if (ip.is_unspecified() || allowed.is_empty()) && (ip6.is_none() || allowed6.is_empty()) {
            return Err(fail("TUNNEL_ALLOWED_ADDRESS_FAMILY_MISSING"));
        }
        let mtu = profile.options.mtu.unwrap_or(1380) as usize;
        if !(1280..=1500).contains(&mtu) {
            return Err(fail("TUNNEL_MTU_INVALID"));
        }
        let (tx, rx) = mpsc::channel(1024);
        let mut port_seed = [0; 2];
        getrandom::getrandom(&mut port_seed).map_err(io::Error::other)?;
        let initial_port = 1024 + u16::from_ne_bytes(port_seed) % 50000;
        let flows: Flows = Arc::new(Mutex::new(HashMap::new()));
        let packet_budget=Arc::new(crate::packet_budget::Budget::default());
        let metrics_budget=packet_budget.clone();
        let awg=profile.protocol == "amneziawg";
        let task = if profile.protocol == "amneziawg" {
            if profile.options.awg_engine.as_deref()==Some("rust"){crate::awg_rust::start(profile,bootstrap,network,rx,flows.clone(),ip,ip6,packet_budget.clone()).await?}else{crate::awg_adapter::start(profile,bootstrap,network,rx,flows.clone(),ip,ip6).await?}
        } else {
            let endpoint = SocketAddr::new(
                crate::dns_engine::resolve_server_ip_bound(&profile.server, bootstrap, &network)
                    .await?
                    .into(),
                profile.port,
            );
            let socket = tokio::net::UdpSocket::from_std(
                crate::native_router::bound_socket_family(true, &network, endpoint.is_ipv6())?
                    .into(),
            )?;
            socket.connect(endpoint).await?;
            let guard = crate::egress_registry::register(socket.local_addr()?, endpoint, true);
            let private = boringtun::x25519::StaticSecret::from(key32(&profile.credential)?);
            let public = boringtun::x25519::PublicKey::from(key32(
                profile
                    .options
                    .peer_public_key
                    .as_deref()
                    .ok_or_else(|| fail("TUNNEL_PEER_KEY_MISSING"))?,
            )?);
            let psk = profile
                .options
                .preshared_key
                .as_deref()
                .filter(|s| !s.is_empty())
                .map(key32)
                .transpose()?;
            let tun = Tunn::new(private, public, psk, profile.options.keepalive, 1, None);
            tokio::spawn(wireguard_driver(
                tun,
                socket,
                rx,
                flows.clone(),
                ip,
                ip6,
                guard,
            ))
        };
        crate::trace::code("transport", if awg {"AWG_PACKET_ENGINE_READY"}else{"WIREGUARD_ENGINE_READY"});
        if awg {crate::trace::emit(serde_json::json!({"stage":"transport","code":"AWG_FALLBACK_TCP_PARAMETERS","receiveBytes":crate::tunnel_tcp::LARGE_RECEIVE,"sendBytes":crate::tunnel_tcp::BASE_RECEIVE,"additionalBudgetBytes":crate::tunnel_tcp::EXTRA_BUDGET,"nagleEnabled":false}));}
        Ok(Self {
            names: Arc::new(crate::tun_names::NameCache::default()),
            session: Arc::new(Session {
                packet_budget,
                awg: profile.protocol == "amneziawg",
                tcp_tuning: crate::tunnel_tcp::Tuning {
                    receive_bytes: if awg {crate::tunnel_tcp::AWG_RECEIVE}else{crate::tunnel_tcp::BASE_RECEIVE},
                    cubic: false,
                    reassembly_segments: 4,
                },
                receive_budget: Arc::new(tokio::sync::Semaphore::new(
                    crate::tunnel_tcp::EXTRA_BUDGET,
                )),
                ip,
                ip6,
                allowed,
                allowed6,
                mtu,
                tx,
                flows,
                port: AtomicU16::new(initial_port),
                task: task.abort_handle(),
                metrics_task: tokio::spawn(async move {
                    let mut timer = tokio::time::interval(Duration::from_secs(5));
                    timer.tick().await;
                    loop {
                        timer.tick().await;
                        crate::tunnel_metrics::report();
                        if awg {metrics_budget.report();crate::tunnel_metrics::pipe_report();}
                    }
                })
                .abort_handle(),
                pmtu: crate::tunnel_pmtu::Cache::default(),
            }),
            upstreams: vec![bootstrap],
            dns_cache: Arc::new(if awg {crate::dns_response_cache::DnsResponseCache::shared_awg()}else{crate::dns_response_cache::DnsResponseCache::default()}),
            dns_inflight: Arc::new(Mutex::new(HashMap::new())),
        })
    }
    pub fn with_dns(mut self, upstream: SocketAddr) -> Self {
        self.upstreams = vec![upstream];
        self
    }
    pub fn with_upstreams(mut self, upstreams: Vec<SocketAddr>) -> io::Result<Self> {
        if upstreams.is_empty() || upstreams.len() > 4 {
            return Err(fail("DNS_RESOLVER_LIST_INVALID"));
        }
        for server in &upstreams {
            match server.ip() {
                IpAddr::V4(ip) => self.check_allowed(ip)?,
                IpAddr::V6(ip) => self.check_allowed6(ip)?,
            }
        }
        self.upstreams = upstreams;
        Ok(self)
    }
    async fn addresses(&self, host: &str, kind: u16, port: u16) -> io::Result<Vec<SocketAddr>> {
        let flight = {
            let mut flights = self.dns_inflight.lock().map_err(|_| io::ErrorKind::Other)?;
            flights.retain(|_, flight| flight.strong_count() > 0);
            let key = (host.trim_end_matches('.').to_ascii_lowercase(), kind);
            if let Some(flight) = flights.get(&key).and_then(std::sync::Weak::upgrade) {
                flight
            } else {
                if flights.len() >= 256 {
                    return Err(fail("TUNNEL_DNS_INFLIGHT_LIMIT"));
                }
                let flight = Arc::new(tokio::sync::Mutex::new(()));
                flights.insert(key, Arc::downgrade(&flight));
                flight
            }
        };
        let _flight = flight.lock().await;
        let mut id = [0; 2];
        getrandom::getrandom(&mut id).map_err(io::Error::other)?;
        let query = crate::dns_engine::encode_address_question(host, id, kind)?;
        let servers: Vec<String> = self.upstreams.iter().map(ToString::to_string).collect();
        let answer =
            crate::dns_exchange::hedged_with_delay(&servers, self.dns_hedge_delay(), |server| {
                let query = &query;
                async move {
                    let scope=if self.session.awg {format!("PROXY|{server}")}else{server.clone()};
                    if let Some(answer) = self.dns_cache.get(query,&scope,false) {
                        if self.session.awg {crate::trace::emit(serde_json::json!({"stage":"dns","code":"AWG_DNS_SHARED_CACHE_HIT","resolver":server,"queryType":kind}));}
                        return Ok(answer);
                    }
                    let mut relay =
                        self.datagram(server.parse().map_err(|_| fail("DNS_RESOLVER_INVALID"))?)?;
                    relay.send(query).await?;
                    let answer = tokio::time::timeout(Duration::from_secs(4), relay.receive())
                        .await
                        .map_err(|_| {
                            io::Error::new(io::ErrorKind::TimedOut, "TUNNEL_DNS_TIMEOUT")
                        })??;
                    crate::dns_engine::decode_address_answers(&answer, query)?;
                    self.dns_cache.insert(query, &scope, false, &answer);
                    Ok(answer)
                }
            })
        .await?;
        if self.session.awg {
            if let Ok(question)=crate::tun_names::question(&query) {
                if self.names.observe(&answer,&question).is_err(){crate::trace::code("dns","AWG_TRANSPORT_NAME_OBSERVATION_REJECTED");}
            }
        }
        let mut result = Vec::new();
        for ip in crate::dns_engine::decode_address_answers(&answer, &query)? {
            let allowed = match ip {
                IpAddr::V4(ip) => self.check_allowed(ip),
                IpAddr::V6(ip) => self.check_allowed6(ip),
            };
            if allowed.is_ok() {
                result.push(SocketAddr::new(ip, port));
            }
        }
        if result.is_empty() {
            return Err(fail("TUNNEL_DESTINATION_OUTSIDE_ALLOWED_IPS"));
        }
        Ok(result)
    }
    async fn destination(&self, host: &str, port: u16) -> io::Result<SocketAddr> {
        if let Ok(ip) = host.parse::<IpAddr>() {
            match ip {
                IpAddr::V4(ip) => self.check_allowed(ip)?,
                IpAddr::V6(ip) => self.check_allowed6(ip)?,
            }
            return Ok(SocketAddr::new(ip, port));
        }
        self.addresses(
            host,
            if self.session.ip.is_unspecified() {
                28
            } else {
                1
            },
            port,
        )
        .await?
        .into_iter()
        .next()
        .ok_or_else(|| fail("TUNNEL_DNS_NO_ADDRESS"))
    }
    fn check_allowed(&self, ip: Ipv4Addr) -> io::Result<()> {
        if self.session.ip.is_unspecified() {
            return Err(fail("TUNNEL_IPV4_LOCAL_ADDRESS_REQUIRED"));
        }
        if !self
            .session
            .allowed
            .iter()
            .any(|&(network, mask)| u32::from(ip) & mask == network)
        {
            return Err(fail("TUNNEL_DESTINATION_OUTSIDE_ALLOWED_IPS"));
        }
        Ok(())
    }
    fn check_allowed6(&self, ip: Ipv6Addr) -> io::Result<()> {
        if self.session.ip6.is_none() {
            return Err(fail("TUNNEL_IPV6_LOCAL_ADDRESS_REQUIRED"));
        }
        if !self
            .session
            .allowed6
            .iter()
            .any(|&(network, mask)| u128::from(ip) & mask == network)
        {
            return Err(fail("TUNNEL_DESTINATION_OUTSIDE_ALLOWED_IPS"));
        }
        Ok(())
    }
    #[cfg(test)]
    async fn destination6(&self, host: &str, port: u16) -> io::Result<SocketAddr> {
        self.addresses(host, 28, port)
            .await?
            .into_iter()
            .next()
            .ok_or_else(|| fail("TUNNEL_DNS_NO_IPV6_ADDRESS"))
    }
    async fn connect_ip(&self, remote: SocketAddr) -> io::Result<Box<dyn RelayStream>> {
        match remote.ip() {
            IpAddr::V4(ip) => self.check_allowed(ip)?,
            IpAddr::V6(ip) => self.check_allowed6(ip)?,
        }
        crate::trace::emit(
            serde_json::json!({"stage":"transport","code":"TUNNEL_CONNECT_ADDRESS","destination":remote.to_string(),"addressFamily":if remote.is_ipv6(){"ipv6"}else{"ipv4"},"transport":"tcp"}),
        );
        let (local, packets, lease) = self.register(6, remote)?;
        let (client, bridge) = tokio::io::duplex(128 * 1024);
        let (started, ready) = tokio::sync::oneshot::channel();
        let session = self.session.clone();
        let flow = crate::trace::current();
        let task = tokio::spawn(async move {
            let work = tcp_flow(session, local, remote, packets, bridge, started, lease);
            if let Some(flow) = flow {
                crate::trace::FLOW.scope(flow, work).await
            } else {
                work.await
            }
        });
        // Dropping a losing Happy Eyeballs attempt must terminate its packet flow.
        struct Pending(tokio::task::AbortHandle, bool);
        impl Drop for Pending {
            fn drop(&mut self) {
                if !self.1 {
                    self.0.abort();
                }
            }
        }
        let mut pending = Pending(task.abort_handle(), false);
        tokio::time::timeout(Duration::from_secs(15), ready)
            .await
            .map_err(|_| io::ErrorKind::TimedOut)?
            .map_err(|_| io::ErrorKind::ConnectionAborted)??;
        pending.1 = true;
        Ok(Box::new(TunnelTcpStream {
            inner: client,
            task: task.abort_handle(),
        }))
    }
    async fn connect_candidates(
        &self,
        addresses: Vec<SocketAddr>,
    ) -> io::Result<Box<dyn RelayStream>> {
        use futures::{stream::FuturesUnordered, StreamExt};
        let mut active = FuturesUnordered::new();
        for (index, address) in addresses.into_iter().take(16).enumerate() {
            active.push(async move {
                tokio::time::sleep(Duration::from_millis(index as u64*250)).await;
                crate::trace::emit(serde_json::json!({"stage":"transport","code":"TUNNEL_ADDRESS_ATTEMPT","attempt":index+1,"destination":address.to_string(),"addressFamily":if address.is_ipv6(){"ipv6"}else{"ipv4"},"reason":if index==0{"FIRST_CANDIDATE"}else{"STAGGERED_CANDIDATE"}}));
                let result=self.connect_ip(address).await;
                if let Err(error)=&result {crate::trace::emit(serde_json::json!({"stage":"transport","code":"TUNNEL_ADDRESS_ATTEMPT_FAILED","attempt":index+1,"kind":format!("{:?}",error.kind())}));}
                result
            });
        }
        let mut error = io::Error::new(io::ErrorKind::NotFound, "DNS_ADDRESS_NODATA");
        while let Some(result) = active.next().await {
            match result {
                Ok(stream) => return Ok(stream),
                Err(failure) => error = failure,
            }
        }
        Err(error)
    }
    fn datagram(&self, remote: SocketAddr) -> io::Result<TunnelDatagram> {
        match remote.ip() {
            IpAddr::V4(ip) => self.check_allowed(ip)?,
            IpAddr::V6(ip) => self.check_allowed6(ip)?,
        }
        crate::trace::emit(
            serde_json::json!({"stage":"transport","code":"TUNNEL_CONNECT_ADDRESS","destination":remote.to_string(),"addressFamily":if remote.is_ipv6(){"ipv6"}else{"ipv4"},"transport":"udp"}),
        );
        let (port, rx, lease) = self.register(17, remote)?;
        Ok(TunnelDatagram {
            session: self.session.clone(),
            port,
            remote,
            rx,
            _lease: lease,
        })
    }
    fn register(
        &self,
        protocol: u8,
        remote: SocketAddr,
    ) -> io::Result<(u16, mpsc::Receiver<Packet>, FlowLease)> {
        self.register_capacity(protocol, remote, 128, false)
    }
    fn register_capacity(
        &self,
        protocol: u8,
        remote: SocketAddr,
        capacity: usize,
        _raw: bool,
    ) -> io::Result<(u16, mpsc::Receiver<Packet>, FlowLease)> {
        let mut flows = self
            .session
            .flows
            .lock()
            .map_err(|_| io::ErrorKind::Other)?;
        if flows.len() >= 512 {
            return Err(fail("TUNNEL_FLOW_CAPACITY_REACHED"));
        }
        for _ in 0..32000 {
            let port = self.session.port.fetch_add(1, Ordering::Relaxed);
            if port < 1024 {
                continue;
            }
            if !flows.contains_key(&(protocol, port)) {
                let (tx, rx) = mpsc::channel(capacity);
                flows.insert((protocol, port), (remote, PacketSender::new(tx,if _raw {Some(self.session.packet_budget.clone())}else{None},if self.session.awg {Some(self.session.packet_budget.clone())}else{None})));
                return Ok((
                    port,
                    rx,
                    FlowLease {
                        key: (protocol, port),
                        flows: self.session.flows.clone(),
                    },
                ));
            }
        }
        Err(fail("TUNNEL_PORTS_EXHAUSTED"))
    }
}
pub(crate) async fn dispatch(
    flows: &Flows,
    local: Ipv4Addr,
    local6: Option<Ipv6Addr>,
    packet: &[u8],
) -> io::Result<()> {
    dispatch_impl(flows,local,local6,packet,false).await
}
pub(crate) async fn dispatch_native(flows:&Flows,local:Ipv4Addr,local6:Option<Ipv6Addr>,packet:&[u8])->io::Result<()>{dispatch_impl(flows,local,local6,packet,true).await}
async fn dispatch_impl(flows:&Flows,local:Ipv4Addr,local6:Option<Ipv6Addr>,packet:&[u8],native:bool)->io::Result<()> {
    let local_ip = if packet.first().is_some_and(|byte| byte >> 4 == 6) {
        local6.map(IpAddr::V6)
    } else {
        Some(local.into())
    };
    let quoted = local_ip
        .and_then(|local| crate::tunnel_pmtu::message(packet, local))
        .map(|(protocol, port, remote, _)| (protocol, port, remote))
        .or_else(||local_ip.and_then(|local|crate::awg_packets::error_tuple(packet,local)).filter(|(protocol,port,remote)|flows.lock().is_ok_and(|flows|flows.get(&(*protocol,*port)).is_some_and(|(address,sender)|address==remote&&sender.budget.is_some()))))
        .or_else(|| crate::tunnel_pmtu::error_flow(packet, local));
    if let Some((protocol, port, remote)) = quoted {
        let sender = flows
            .lock()
            .map_err(|_| io::ErrorKind::Other)?
            .get(&(protocol, port))
            .filter(|(address, _)| *address == remote)
            .map(|(_, sender)| sender.clone());
        if let Some(sender) = sender {
            if native{sender.deliver_native(packet).await;}else{sender.deliver(packet);}
        }
        return Ok(());
    }
    let (remote_ip, next_header, payload): (IpAddr, IpProtocol, &[u8]) =
        match packet.first().map(|b| b >> 4) {
            Some(4) => {
                let ip = Ipv4Packet::new_checked(packet).map_err(|_| io::ErrorKind::InvalidData)?;
                if ip.dst_addr() != local || ip.more_frags() || ip.frag_offset() != 0 {
                    return Ok(());
                }
                (
                    ip.src_addr().into(),
                    ip.next_header(),
                    &packet[ip.header_len() as usize..ip.total_len() as usize],
                )
            }
            Some(6) => {
                let ip = Ipv6Packet::new_checked(packet).map_err(|_| io::ErrorKind::InvalidData)?;
                if Some(ip.dst_addr()) != local6 {
                    return Ok(());
                }
                (
                    ip.src_addr().into(),
                    ip.next_header(),
                    &packet[40..40 + ip.payload_len() as usize],
                )
            }
            _ => return Err(fail("TUNNEL_IP_PACKET_INVALID")),
        };
    if payload.len() < 4 {
        return Ok(());
    }
    let source = u16::from_be_bytes(payload[..2].try_into().unwrap());
    let port = u16::from_be_bytes(payload[2..4].try_into().unwrap());
    let protocol = match next_header {
        IpProtocol::Tcp => 6,
        IpProtocol::Udp => 17,
        _ => return Ok(()),
    };
    let sender = flows
        .lock()
        .map_err(|_| io::ErrorKind::Other)?
        .get(&(protocol, port))
        .filter(|(remote, _)| remote.ip() == remote_ip && remote.port() == source)
        .map(|(_, sender)| sender.clone());
    if let Some(sender) = sender {
        if native{sender.deliver_native(packet).await;}else{sender.deliver(packet);}
    }
    Ok(())
}
async fn wireguard_driver(
    mut tun: Tunn,
    socket: tokio::net::UdpSocket,
    mut outgoing: mpsc::Receiver<Packet>,
    flows: Flows,
    local: Ipv4Addr,
    local6: Option<Ipv6Addr>,
    _guard: Option<crate::egress_registry::Guard>,
) -> io::Result<()> {
    let mut tick = tokio::time::interval(Duration::from_millis(100));
    let mut encrypted = vec![0; 65535];
    let mut output = vec![0; 65535];
    let mut authenticated = false;
    let mut reassembly = crate::tunnel_fragments::Reassembly::default();
    loop {
        let action = tokio::select! {
            packet=outgoing.recv()=>match packet{Some(packet)=>copy_action(tun.encapsulate(&packet,&mut output)),None=>return Ok(())},
            size=socket.recv(&mut encrypted)=>copy_action(tun.decapsulate(None,&encrypted[..size?],&mut output)),
            _=tick.tick()=>copy_action(tun.update_timers(&mut output)),
        };
        handle_action(action, &socket, &flows, local, local6, &mut reassembly).await?;
        if !authenticated && tun.stats().0.is_some() {
            authenticated = true;
            crate::trace::code("handshake", "WIREGUARD_HANDSHAKE_AUTHENTICATED");
        }
        for _ in 0..256 {
            let action = copy_action(tun.decapsulate(None, &[], &mut output));
            if matches!(action, Action::None) {
                break;
            }
            handle_action(action, &socket, &flows, local, local6, &mut reassembly).await?;
        }
    }
}
enum Action {
    None,
    Network(Vec<u8>),
    Tunnel(Vec<u8>),
}
fn copy_action(result: TunnResult<'_>) -> Action {
    match result {
        TunnResult::WriteToNetwork(data) => Action::Network(data.to_vec()),
        TunnResult::WriteToTunnelV4(data, _) => Action::Tunnel(data.to_vec()),
        TunnResult::WriteToTunnelV6(data, _) => Action::Tunnel(data.to_vec()),
        _ => Action::None,
    }
}
async fn handle_action(
    action: Action,
    socket: &tokio::net::UdpSocket,
    flows: &Flows,
    local: Ipv4Addr,
    local6: Option<Ipv6Addr>,
    reassembly: &mut crate::tunnel_fragments::Reassembly,
) -> io::Result<()> {
    match action {
        Action::Network(packet) => {
            socket.send(&packet).await?;
        }
        // Invalid or over-budget fragments must not tear down unrelated flows.
        Action::Tunnel(packet) => match reassembly.accept(&packet) {
            Ok(Some(packet)) => dispatch(flows, local, local6, &packet).await?,
            Ok(None) => (),
            Err(_) => crate::trace::code("tunnel", "TUNNEL_FRAGMENT_REJECTED"),
        },
        Action::None => (),
    }
    Ok(())
}
struct FlowLease {
    key: (u8, u16),
    flows: Flows,
}
impl Drop for FlowLease {
    fn drop(&mut self) {
        if let Ok(mut flows) = self.flows.lock() {
            flows.remove(&self.key);
        }
    }
}
impl OutboundConnector for TunnelConnector {
    fn named_tun_destination(&self)->bool {false}
    fn dns_response_cache(&self)->Option<Arc<crate::dns_response_cache::DnsResponseCache>> {self.session.awg.then(||self.dns_cache.clone())}
    fn dns_observations(&self)->Option<Arc<crate::tun_names::NameCache>> {self.session.awg.then(||self.names.clone())}
    fn packet_path_enabled(&self)->bool {self.session.awg}
    fn raw_packet(&self, source:SocketAddr,remote:SocketAddr,protocol:u8)->Option<crate::awg_packets::OpenFuture> {
        if !self.session.awg {return None;}
        let this=self.clone();
        Some(Box::pin(async move {
            match remote.ip(){IpAddr::V4(ip)=>this.check_allowed(ip)?,IpAddr::V6(ip)=>this.check_allowed6(ip)?};
            if source.is_ipv6()!=remote.is_ipv6(){return Err(fail("AWG_PACKET_FAMILY_MISMATCH"));}
            let (port,rx,lease)=this.register_capacity(protocol,remote,64,true)?;
            let ip=if remote.is_ipv6(){this.session.ip6.ok_or_else(||fail("TUNNEL_IPV6_LOCAL_ADDRESS_REQUIRED"))?.into()}else{this.session.ip.into()};
            Ok(Box::new(RawPacketRelay{session:this.session.clone(),source,local:SocketAddr::new(ip,port),remote,protocol,rx:tokio::sync::Mutex::new(rx),_lease:lease}) as Box<dyn crate::awg_packets::PacketRelay>)
        }))
    }
    fn dns_hedge_delay(&self) -> Duration {
        Duration::from_millis(if self.session.awg {150}else{500})
    }
    fn ipv4_only(&self) -> bool {
        self.session.ip6.is_none() || self.session.allowed6.is_empty()
    }
    fn ipv6_only(&self) -> bool {
        self.session.ip.is_unspecified() || self.session.allowed.is_empty()
    }
    fn resolve_candidates(&self, host: String) -> Option<crate::proxy_inbound::AddressFuture> {
        if host.parse::<IpAddr>().is_ok() {
            return None;
        }
        let this = self.clone();
        Some(Box::pin(async move {
            let addresses = if this.ipv4_only() {
                this.addresses(&host, 1, 0).await?
            } else if this.session.ip.is_unspecified() {
                this.addresses(&host, 28, 0).await?
            } else {
                let a = this.addresses(&host, 1, 0);
                let aaaa = this.addresses(&host, 28, 0);
                tokio::pin!(a, aaaa);
                tokio::select! {
                    result=&mut a=>match result {Ok(mut values)=>{if let Ok(Ok(other))=tokio::time::timeout(Duration::from_millis(250),&mut aaaa).await{values.extend(other);}values},Err(_)=>aaaa.await?},
                    result=&mut aaaa=>match result {Ok(mut values)=>{if let Ok(Ok(other))=tokio::time::timeout(Duration::from_millis(250),&mut a).await{values.extend(other);}values},Err(_)=>a.await?},
                }
            };
            Ok(addresses.into_iter().map(|address| address.ip()).collect())
        }))
    }
    fn connect(&self, host: String, port: u16) -> ConnectFuture {
        let this = self.clone();
        Box::pin(async move {
            if !this.ipv4_only()
                && !this.session.ip.is_unspecified()
                && host.parse::<IpAddr>().is_err()
            {
                let first = async {
                    this.connect_candidates(this.addresses(&host, 28, port).await?)
                        .await
                };
                let fallback = async {
                    tokio::time::sleep(Duration::from_millis(250)).await;
                    this.connect_candidates(this.addresses(&host, 1, port).await?)
                        .await
                };
                tokio::pin!(first, fallback);
                return tokio::select! {result=&mut first=>match result{Ok(stream)=>Ok(stream),Err(_)=>fallback.await},result=&mut fallback=>match result{Ok(stream)=>Ok(stream),Err(_)=>first.await}};
            }
            if host.parse::<IpAddr>().is_err() {
                this.connect_candidates(
                    this.addresses(
                        &host,
                        if this.session.ip.is_unspecified() {
                            28
                        } else {
                            1
                        },
                        port,
                    )
                    .await?,
                )
                .await
            } else {
                this.connect_ip(this.destination(&host, port).await?).await
            }
        })
    }
    fn udp(&self, host: String, port: u16) -> UdpFuture {
        let this = self.clone();
        Box::pin(async move {
            let remote = if host.parse::<IpAddr>().is_err() {
                this.addresses(
                    &host,
                    if this.session.ip.is_unspecified() {
                        28
                    } else {
                        1
                    },
                    port,
                )
                .await?[0]
            } else {
                this.destination(&host, port).await?
            };
            crate::trace::emit(
                serde_json::json!({"stage":"transport","code":"TUNNEL_CONNECT_ADDRESS","destination":remote.to_string(),"addressFamily":if remote.is_ipv6(){"ipv6"}else{"ipv4"},"transport":"udp"}),
            );
            Ok(Box::new(this.datagram(remote)?) as Box<dyn DatagramRelay>)
        })
    }
}

struct RawPacketRelay {
    session: Arc<Session>,source:SocketAddr,local:SocketAddr,remote:SocketAddr,protocol:u8,
    rx:tokio::sync::Mutex<mpsc::Receiver<Packet>>,_lease:FlowLease,
}
impl crate::awg_packets::PacketRelay for RawPacketRelay {
    fn delivery_snapshot(&self)->Option<[u64;6]>{self.session.flows.lock().ok().and_then(|flows|flows.get(&(self.protocol,self.local.port())).and_then(|(_,sender)|sender.counters.as_ref().map(|c|c.snapshot())))}
    fn send<'a>(&'a self,bytes:&'a [u8])->crate::awg_packets::SendFuture<'a> {
        Box::pin(async move {
            let t=crate::awg_packets::tuple(bytes).ok_or(io::ErrorKind::InvalidData)?;
            if t.source!=self.source || t.destination!=self.remote || t.protocol!=self.protocol{return Err(fail("AWG_PACKET_TUPLE_MISMATCH"));}
            let mut packet=self.session.packet_budget.copy(bytes)?;
            crate::awg_packets::translate(&mut packet,self.local,true)?;
            let mtu=self.session.pmtu.get(self.remote,self.session.mtu);
            if packet.len()<=mtu {
                self.session.tx.try_send(packet).map_err(|error|match error {mpsc::error::TrySendError::Full(_)=>{self.session.packet_budget.queue_drop();io::Error::new(io::ErrorKind::WouldBlock,"AWG_PACKET_QUEUE_FULL")},mpsc::error::TrySendError::Closed(_)=>io::Error::new(io::ErrorKind::BrokenPipe,"AWG_PACKET_ENGINE_STOPPED")})?;
            } else {
                for bytes in crate::tunnel_fragments::fragment(&packet,mtu)? {
                    let packet=self.session.packet_budget.copy(&bytes)?;
                    self.session.tx.try_send(packet).map_err(|error|match error {mpsc::error::TrySendError::Full(_)=>{self.session.packet_budget.queue_drop();io::Error::new(io::ErrorKind::WouldBlock,"AWG_PACKET_QUEUE_FULL")},mpsc::error::TrySendError::Closed(_)=>io::Error::new(io::ErrorKind::BrokenPipe,"AWG_PACKET_ENGINE_STOPPED")})?;
                }
            }
            Ok(())
        })
    }
    fn receive(&self)->crate::awg_packets::PacketFuture<'_> {
        Box::pin(async move {
            let mut rx=self.rx.lock().await;
            let mut packet=rx.recv().await.ok_or(io::ErrorKind::BrokenPipe)?;
            if let Some((protocol,port,remote,mtu))=crate::tunnel_pmtu::message(&packet,self.local.ip()) {
                if protocol==self.protocol && port==self.local.port() && remote==self.remote {self.session.pmtu.update(remote,mtu,self.session.mtu);}
            }
            crate::awg_packets::translate_reply(&mut packet,self.source)?;
            Ok(packet)
        })
    }
}
struct TunnelDatagram {
    session: Arc<Session>,
    port: u16,
    remote: SocketAddr,
    rx: mpsc::Receiver<Packet>,
    _lease: FlowLease,
}
impl DatagramRelay for TunnelDatagram {
    fn send<'a>(&'a mut self, data: &'a [u8]) -> SendFuture<'a> {
        Box::pin(async move {
            if data.len() + if self.remote.is_ipv6() { 48 } else { 28 } > 65535 {
                return Err(fail("TUNNEL_UDP_DATAGRAM_TOO_LARGE"));
            }
            if let IpAddr::V6(remote) = self.remote.ip() {
                let local = self
                    .session
                    .ip6
                    .ok_or_else(|| fail("TUNNEL_IPV6_LOCAL_ADDRESS_REQUIRED"))?;
                let repr = Ipv6Repr {
                    src_addr: local,
                    dst_addr: remote,
                    next_header: IpProtocol::Udp,
                    payload_len: data.len() + 8,
                    hop_limit: 64,
                };
                let mut buf = vec![0; 40 + data.len() + 8];
                let mut packet = Ipv6Packet::new_unchecked(&mut buf);
                repr.emit(&mut packet);
                let mut udp = UdpPacket::new_unchecked(packet.payload_mut());
                UdpRepr {
                    src_port: self.port,
                    dst_port: self.remote.port(),
                }
                .emit(
                    &mut udp,
                    &local.into(),
                    &remote.into(),
                    data.len(),
                    |buffer| buffer.copy_from_slice(data),
                    &Default::default(),
                );
                for packet in crate::tunnel_fragments::fragment(
                    &buf,
                    self.session.pmtu.get(self.remote, self.session.mtu),
                )? {
                    self.session
                        .tx
                        .send(packet.into())
                        .await
                        .map_err(|_| io::ErrorKind::BrokenPipe)?;
                }
                return Ok(());
            }
            let IpAddr::V4(remote) = self.remote.ip() else {
                unreachable!()
            };
            let repr = Ipv4Repr {
                src_addr: self.session.ip,
                dst_addr: remote,
                next_header: IpProtocol::Udp,
                payload_len: data.len() + 8,
                hop_limit: 64,
            };
            let mut buf = vec![0; repr.buffer_len() + data.len() + 8];
            let mut packet = Ipv4Packet::new_unchecked(&mut buf);
            repr.emit(&mut packet, &Default::default());
            let mut udp = UdpPacket::new_unchecked(packet.payload_mut());
            UdpRepr {
                src_port: self.port,
                dst_port: self.remote.port(),
            }
            .emit(
                &mut udp,
                &self.session.ip.into(),
                &remote.into(),
                data.len(),
                |buffer| buffer.copy_from_slice(data),
                &Default::default(),
            );
            for packet in crate::tunnel_fragments::fragment(
                &buf,
                self.session.pmtu.get(self.remote, self.session.mtu),
            )? {
                self.session
                    .tx
                    .send(packet.into())
                    .await
                    .map_err(|_| io::ErrorKind::BrokenPipe)?;
            }
            Ok(())
        })
    }
    fn receive(&mut self) -> ReceiveFuture<'_> {
        Box::pin(async move {
            let buf = self.rx.recv().await.ok_or(io::ErrorKind::BrokenPipe)?;
            let local = if self.remote.is_ipv6() {
                self.session
                    .ip6
                    .ok_or(io::ErrorKind::AddrNotAvailable)?
                    .into()
            } else {
                self.session.ip.into()
            };
            if let Some((_, _, remote, mtu)) = crate::tunnel_pmtu::message(&buf, local) {
                if self.session.pmtu.update(remote, mtu, self.session.mtu) {
                    crate::trace::code("tunnel", "TUNNEL_PATH_MTU_UPDATED");
                }
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "TUNNEL_PATH_MTU_CHANGED",
                ));
            }
            if buf.first().is_some_and(|byte| byte >> 4 == 6) {
                let packet =
                    Ipv6Packet::new_checked(&buf).map_err(|_| io::ErrorKind::InvalidData)?;
                let udp = UdpPacket::new_checked(packet.payload())
                    .map_err(|_| io::ErrorKind::InvalidData)?;
                UdpRepr::parse(
                    &udp,
                    &packet.src_addr().into(),
                    &packet.dst_addr().into(),
                    &Default::default(),
                )
                .map_err(|_| io::ErrorKind::InvalidData)?;
                return Ok(udp.payload().to_vec());
            }
            let packet = Ipv4Packet::new_checked(&buf).map_err(|_| io::ErrorKind::InvalidData)?;
            let udp =
                UdpPacket::new_checked(packet.payload()).map_err(|_| io::ErrorKind::InvalidData)?;
            UdpRepr::parse(
                &udp,
                &packet.src_addr().into(),
                &packet.dst_addr().into(),
                &Default::default(),
            )
            .map_err(|_| io::ErrorKind::InvalidData)?;
            Ok(udp.payload().to_vec())
        })
    }
}
struct IpDevice {
    rx: VecDeque<Vec<u8>>,
    tx: mpsc::Sender<Packet>,
    mtu: usize,
}
struct Rx(Vec<u8>);
struct Tx(mpsc::OwnedPermit<Packet>);
impl RxToken for Rx {
    fn consume<R, F: FnOnce(&[u8]) -> R>(self, f: F) -> R {
        f(&self.0)
    }
}
impl TxToken for Tx {
    fn consume<R, F: FnOnce(&mut [u8]) -> R>(self, len: usize, f: F) -> R {
        let mut buf = vec![0; len];
        let result = f(&mut buf);
        self.0.send(buf.into());
        result
    }
}
impl Device for IpDevice {
    type RxToken<'a> = Rx;
    type TxToken<'a> = Tx;
    fn receive(&mut self, _: Instant) -> Option<(Rx, Tx)> {
        if self.rx.is_empty() {
            return None;
        }
        let permit = self.tx.clone().try_reserve_owned().ok()?;
        self.rx.pop_front().map(|packet| (Rx(packet), Tx(permit)))
    }
    fn transmit(&mut self, _: Instant) -> Option<Tx> {
        self.tx.clone().try_reserve_owned().ok().map(Tx)
    }
    fn capabilities(&self) -> DeviceCapabilities {
        let mut cap = DeviceCapabilities::default();
        cap.medium = Medium::Ip;
        cap.max_transmission_unit = self.mtu;
        cap
    }
}
// A write half-close keeps the read half alive. Dropping the whole stream
// cancels its detached flow and releases its per-session buffer permit.
struct TunnelTcpStream {
    inner: tokio::io::DuplexStream,
    task: tokio::task::AbortHandle,
}
impl Drop for TunnelTcpStream {
    fn drop(&mut self) {
        self.task.abort();
    }
}
impl tokio::io::AsyncRead for TunnelTcpStream {
    fn poll_read(
        mut self: std::pin::Pin<&mut Self>,
        cx: &mut std::task::Context<'_>,
        buf: &mut tokio::io::ReadBuf<'_>,
    ) -> std::task::Poll<io::Result<()>> {
        std::pin::Pin::new(&mut self.inner).poll_read(cx, buf)
    }
}
impl tokio::io::AsyncWrite for TunnelTcpStream {
    fn poll_write(
        mut self: std::pin::Pin<&mut Self>,
        cx: &mut std::task::Context<'_>,
        buf: &[u8],
    ) -> std::task::Poll<io::Result<usize>> {
        std::pin::Pin::new(&mut self.inner).poll_write(cx, buf)
    }
    fn poll_flush(
        mut self: std::pin::Pin<&mut Self>,
        cx: &mut std::task::Context<'_>,
    ) -> std::task::Poll<io::Result<()>> {
        std::pin::Pin::new(&mut self.inner).poll_flush(cx)
    }
    fn poll_shutdown(
        mut self: std::pin::Pin<&mut Self>,
        cx: &mut std::task::Context<'_>,
    ) -> std::task::Poll<io::Result<()>> {
        std::pin::Pin::new(&mut self.inner).poll_shutdown(cx)
    }
}
async fn tcp_flow(
    session: Arc<Session>,
    port: u16,
    remote: SocketAddr,
    mut packets: mpsc::Receiver<Packet>,
    bridge: tokio::io::DuplexStream,
    ready: tokio::sync::oneshot::Sender<io::Result<()>>,
    _lease: FlowLease,
) -> io::Result<()> {
    let mut device = IpDevice {
        rx: VecDeque::new(),
        tx: session.tx.clone(),
        mtu: session.pmtu.get(remote, session.mtu),
    };
    let mut config = Config::new(HardwareAddress::Ip);
    let mut seed = [0; 8];
    getrandom::getrandom(&mut seed).map_err(io::Error::other)?;
    config.random_seed = u64::from_ne_bytes(seed);
    let mut iface = Interface::new(config, &mut device, Instant::from_millis(0));
    let local: IpAddr = if remote.is_ipv6() {
        session
            .ip6
            .ok_or_else(|| fail("TUNNEL_IPV6_LOCAL_ADDRESS_REQUIRED"))?
            .into()
    } else {
        session.ip.into()
    };
    iface.update_ip_addrs(|addresses| {
        if !session.ip.is_unspecified() {
            addresses.push(IpCidr::new(session.ip.into(), 32)).unwrap();
        }
        if let Some(ip6) = session.ip6 {
            addresses.push(IpCidr::new(ip6.into(), 128)).unwrap();
        }
    });
    if !session.ip.is_unspecified() {
        iface
            .routes_mut()
            .add_default_ipv4_route(session.ip)
            .map_err(|_| io::ErrorKind::InvalidInput)?;
    }
    if let Some(ip6) = session.ip6 {
        iface
            .routes_mut()
            .add_default_ipv6_route(ip6)
            .map_err(|_| io::ErrorKind::InvalidInput)?;
    }
    let mut sockets = SocketSet::new(Vec::new());
    let (receive_size, _receive_permit) = session
        .tcp_tuning
        .receive_allocation(&session.receive_budget);
    let mut socket = smoltcp::socket::tcp::Socket::new(
        smoltcp::socket::tcp::SocketBuffer::new(vec![0; receive_size]),
        smoltcp::socket::tcp::SocketBuffer::new(vec![0; 128 * 1024]),
    );
    // Preserve the accepted AWG fallback tuning; other tunnels keep Nagle.
    socket.set_nagle_enabled(!session.awg);
    assert!(socket.set_reassembly_limit(session.tcp_tuning.reassembly_segments));
    socket.set_congestion_control(session.tcp_tuning.congestion());
    let handle = sockets.add(socket);
    sockets
        .get_mut::<smoltcp::socket::tcp::Socket>(handle)
        .connect(
            iface.context(),
            (IpAddress::from(remote.ip()), remote.port()),
            (IpAddress::from(local), port),
        )
        .map_err(|_| io::ErrorKind::InvalidInput)?;
    let start = std::time::Instant::now();
    let mut ready = Some(ready);
    let (mut app_read, mut app_write) = tokio::io::split(bridge);
    let (app_output, mut app_packets) = mpsc::channel::<Vec<u8>>(8);
    let mut writer = tokio::spawn(async move {
        while let Some(data) = app_packets.recv().await {
            app_write.write_all(&data).await?;
        }
        app_write.shutdown().await
    });
    struct WriterGuard(tokio::task::AbortHandle);
    impl Drop for WriterGuard {
        fn drop(&mut self) {
            self.0.abort();
        }
    }
    let _writer_guard = WriterGuard(writer.abort_handle());
    let mut writer_done = false;
    let mut app_output = Some(app_output);
    let mut buffer = vec![0; 16384];
    let mut pending = VecDeque::new();
    let mut eof = false;
    let mut transport_wait =
        crate::tunnel_metrics::QueueWait::new(&crate::tunnel_metrics::TRANSPORT_QUEUE);
    let mut application_wait =
        crate::tunnel_metrics::QueueWait::new(&crate::tunnel_metrics::APPLICATION_QUEUE);
    let mut observation = crate::tunnel_metrics::TcpObservation::new(remote);
    loop {
        iface.poll(
            Instant::from_millis(start.elapsed().as_millis() as i64),
            &mut device,
            &mut sockets,
        );
        let socket = sockets.get_mut::<smoltcp::socket::tcp::Socket>(handle);
        if socket.state() == smoltcp::socket::tcp::State::Established {
            if let Some(ready) = ready.take() {
                let _ = ready.send(Ok(()));
            }
        }
        if socket.state() == smoltcp::socket::tcp::State::Closed
            || (socket.state() == smoltcp::socket::tcp::State::TimeWait && !socket.can_recv())
        {
            observation.sample(
                socket.diagnostics(),
                app_output
                    .as_ref()
                    .is_some_and(|output| output.capacity() == 0),
            );
            if let Some(ready) = ready.take() {
                let _ = ready.send(Err(io::ErrorKind::ConnectionAborted.into()));
            }
            app_output.take();
            if writer_done {
                return Ok(());
            }
            return tokio::time::timeout(Duration::from_secs(15), &mut writer)
                .await
                .map_err(|_| io::ErrorKind::TimedOut)?
                .map_err(io::Error::other)?;
        }
        let mut socket_changed = false;
        while socket.can_send() && !pending.is_empty() {
            let data = pending.make_contiguous();
            let sent = socket
                .send_slice(data)
                .map_err(|_| io::ErrorKind::BrokenPipe)?;
            pending.drain(..sent);
            observation.sent += sent as u64;
            socket_changed |= sent > 0;
            if sent == 0 {
                break;
            }
        }
        if eof && pending.is_empty() && socket.may_send() {
            socket.close();
            socket_changed = true;
        }
        while socket.can_recv()
            && app_output
                .as_ref()
                .is_some_and(|output| output.capacity() > 0)
        {
            let n = socket
                .recv_slice(&mut buffer)
                .map_err(|_| io::ErrorKind::ConnectionReset)?;
            app_output
                .as_ref()
                .unwrap()
                .try_send(buffer[..n].to_vec())
                .map_err(|_| io::ErrorKind::BrokenPipe)?;
            observation.received += n as u64;
            socket_changed |= n > 0;
        }
        if !socket.may_recv() && ready.is_none() {
            // FIN closes only the peer's sending half. Continue draining local
            // writes, pending TCP bytes and FIN/ACK before ending this flow.
            app_output.take();
        }
        // send_slice/recv_slice/close change deadlines after the first poll.
        // Flush data, window updates and FIN before sleeping on those deadlines.
        if socket_changed {
            iface.poll(
                Instant::from_millis(start.elapsed().as_millis() as i64),
                &mut device,
                &mut sockets,
            );
        }
        transport_wait.update(session.tx.capacity() == 0);
        application_wait.update(
            app_output
                .as_ref()
                .is_some_and(|output| output.capacity() == 0),
        );
        observation.sample(
            sockets
                .get::<smoltcp::socket::tcp::Socket>(handle)
                .diagnostics(),
            app_output
                .as_ref()
                .is_some_and(|output| output.capacity() == 0),
        );
        let now = Instant::from_millis(start.elapsed().as_millis() as i64);
        let wait = iface
            .poll_delay(now, &sockets)
            .map(|delay| Duration::from_millis(delay.total_millis()))
            .unwrap_or(Duration::from_secs(1));
        let wait = if session.tx.capacity() == 0 {
            wait.max(Duration::from_millis(1))
        } else {
            wait
        };
        let output = app_output.clone();
        tokio::select! {
            packet=packets.recv()=>match packet{Some(packet)=>{
                if let Some((_,_,address,mtu))=crate::tunnel_pmtu::message(&packet,local){
                    if address==remote && session.pmtu.update(remote,mtu,session.mtu){device.mtu=session.pmtu.get(remote,session.mtu);crate::trace::code("tunnel","TUNNEL_PATH_MTU_UPDATED");}
                }else{device.rx.push_back(packet.into_vec());}
            },None=>return Err(io::ErrorKind::BrokenPipe.into())},
            size=app_read.read(&mut buffer),if !eof && ready.is_none() && pending.len()<128*1024=>{let n=size?;if n==0{eof=true;}else{pending.extend(&buffer[..n]);}},
            result=&mut writer,if !writer_done=>{result.map_err(io::Error::other)??;writer_done=true;},
            _=session.tx.reserve(),if session.tx.capacity()==0=>(),
            _=async {output.as_ref().unwrap().reserve().await},if output.as_ref().is_some_and(|sender|sender.capacity()==0)=>(),
            _=tokio::time::sleep(wait)=>{if start.elapsed()>Duration::from_secs(15) && ready.is_some(){return Err(io::ErrorKind::TimedOut.into());}}
        }
    }
}
#[cfg(test)]
mod tests {
    #[tokio::test]
    async fn diagnostic_ingress_counters_distinguish_waiting_from_delivered_data(){
        let budget=Arc::new(crate::packet_budget::Budget::default());let(tx,mut rx)=mpsc::channel(1);
        let mut sender=PacketSender::new(tx,Some(budget.clone()),Some(budget.clone()));let counters=Arc::new(IngressCounters::default());sender.counters=Some(counters.clone());
        sender.deliver_native(b"one").await;assert_eq!(&counters.snapshot()[..3],&[3,3,0]);
        sender.deliver_native(b"two").await;assert_eq!(&counters.snapshot()[..3],&[6,3,3]);
        drop(rx.recv().await.unwrap());let packet=tokio::time::timeout(Duration::from_millis(100),rx.recv()).await.unwrap().unwrap();assert_eq!(&packet[..],b"two");drop(packet);
        assert_eq!(&counters.snapshot()[..3],&[6,6,0]);drop(sender);drop(rx);assert_eq!(counters.snapshot()[5],1);
        tokio::time::timeout(Duration::from_secs(1),async{while budget.used()!=0{tokio::task::yield_now().await;}}).await.unwrap();
    }
    #[tokio::test]
    async fn native_queue_pressure_is_bounded_and_does_not_stop_another_receiver(){
        let budget=Arc::new(crate::packet_budget::Budget::default());
        let (slow,slow_rx)=mpsc::channel(1);let (fast,mut fast_rx)=mpsc::channel(1);
        let slow=PacketSender::new(slow,Some(budget.clone()),Some(budget.clone()));let fast=PacketSender::new(fast,None,Some(budget.clone()));
        slow.deliver_native(b"held").await;
        let before=crate::tunnel_metrics::RX_DROPPED.load(Ordering::Relaxed);
        for _ in 0..10000{slow.deliver_native(&[7;1024]).await;}
        assert!(budget.used()<=crate::packet_budget::LIMIT);
        assert!(crate::tunnel_metrics::RX_DROPPED.load(Ordering::Relaxed)>before,"overflow must be counted");
        fast.deliver_native(b"still alive").await;
        let packet=tokio::time::timeout(Duration::from_millis(100),fast_rx.recv()).await.unwrap().unwrap();assert_eq!(&packet[..],b"still alive");drop(packet);
        drop(slow);drop(fast);drop(slow_rx);drop(fast_rx);
        tokio::time::timeout(Duration::from_secs(1),async{while budget.used()!=0{tokio::task::yield_now().await;}}).await.unwrap();
    }
    #[tokio::test]
    async fn native_slow_reader_does_not_block_other_flows_and_releases_pending_packets(){
        let budget=Arc::new(crate::packet_budget::Budget::default());
        let (tx_a,mut rx_a)=mpsc::channel(1);let (tx_b,mut rx_b)=mpsc::channel(1);
        let a=PacketSender::new(tx_a,Some(budget.clone()),Some(budget.clone()));let b=PacketSender::new(tx_b,None,Some(budget.clone()));
        a.deliver_native(b"initial").await;
        tokio::time::timeout(Duration::from_millis(100),async{
            for index in 0..128u8{a.deliver_native(&[index]).await;}
            b.deliver_native(b"independent").await;
        }).await.expect("native engine must not wait on one paused receiver");
        let received=tokio::time::timeout(Duration::from_millis(100),rx_b.recv()).await.unwrap().unwrap();assert_eq!(&received[..],b"independent");drop(received);
        let received=rx_a.recv().await.unwrap();assert_eq!(&received[..],b"initial");drop(received);
        for index in 0..128u8{let received=rx_a.recv().await.unwrap();assert_eq!(&received[..],&[index]);}
        a.deliver_native(b"cancelled").await;drop(a);drop(b);drop(rx_a);drop(rx_b);
        tokio::time::timeout(Duration::from_secs(1),async{while budget.used()!=0{tokio::task::yield_now().await;}}).await.expect("cancel must free all pending packet storage");
    }
    use super::*;
    use futures::{SinkExt, StreamExt};
    #[tokio::test]
    async fn stream_drop_cancels_budget_owner_but_shutdown_preserves_half_close() {
        let budget = Arc::new(tokio::sync::Semaphore::new(crate::tunnel_tcp::EXTRA_BUDGET));
        let other = budget.clone();
        let (ready, started) = tokio::sync::oneshot::channel();
        let task = tokio::spawn(async move {
            let (_size, _permit) = crate::tunnel_tcp::Tuning {
                receive_bytes: crate::tunnel_tcp::LARGE_RECEIVE,
                cubic: false,
                reassembly_segments: 4,
            }
            .receive_allocation(&other);
            ready.send(()).unwrap();
            std::future::pending::<()>().await;
        });
        started.await.unwrap();
        let (client, _peer) = tokio::io::duplex(1024);
        let mut stream = TunnelTcpStream {
            inner: client,
            task: task.abort_handle(),
        };
        stream.shutdown().await.unwrap();
        assert!(!task.is_finished());
        assert!(budget.available_permits() < crate::tunnel_tcp::EXTRA_BUDGET);
        drop(stream);
        assert!(task.await.unwrap_err().is_cancelled());
        assert_eq!(budget.available_permits(), crate::tunnel_tcp::EXTRA_BUDGET);
    }
    #[tokio::test]
    async fn cancelled_pending_connect_releases_flow_and_receive_budget() {
        let (tx, _rx) = mpsc::channel(1024);
        let driver = tokio::spawn(std::future::pending::<()>());
        let metrics = tokio::spawn(std::future::pending::<()>());
        let budget = Arc::new(tokio::sync::Semaphore::new(crate::tunnel_tcp::EXTRA_BUDGET));
        let flows = Arc::new(Mutex::new(HashMap::new()));
        let connector = TunnelConnector {
            names: Arc::new(crate::tun_names::NameCache::default()),
            session: Arc::new(Session {
                packet_budget:Arc::new(crate::packet_budget::Budget::default()),
                awg:false,
                tcp_tuning: crate::tunnel_tcp::Tuning {
                    receive_bytes: crate::tunnel_tcp::LARGE_RECEIVE,
                    cubic: false,
                    reassembly_segments: 4,
                },
                receive_budget: budget.clone(),
                ip: "10.7.0.2".parse().unwrap(),
                ip6: None,
                allowed: vec![(0, 0)],
                allowed6: vec![],
                mtu: 1280,
                tx,
                flows: flows.clone(),
                port: AtomicU16::new(20000),
                task: driver.abort_handle(),
                metrics_task: metrics.abort_handle(),
                pmtu: crate::tunnel_pmtu::Cache::default(),
            }),
            upstreams: vec!["8.8.8.8:53".parse().unwrap()],
            dns_cache: Arc::new(crate::dns_response_cache::DnsResponseCache::default()),
            dns_inflight: Arc::new(Mutex::new(HashMap::new())),
        };
        let pending = tokio::spawn(async move { connector.connect("10.7.0.1".into(), 443).await });
        tokio::time::timeout(Duration::from_secs(1), async {
            while budget.available_permits() == crate::tunnel_tcp::EXTRA_BUDGET {
                tokio::task::yield_now().await;
            }
        })
        .await
        .unwrap();
        assert_eq!(flows.lock().unwrap().len(), 1);
        pending.abort();
        let _ = pending.await;
        tokio::time::timeout(Duration::from_secs(1), async {
            while budget.available_permits() != crate::tunnel_tcp::EXTRA_BUDGET {
                tokio::task::yield_now().await;
            }
        })
        .await
        .unwrap();
        assert!(flows.lock().unwrap().is_empty());
        let _ = driver.await;
        let _ = metrics.await;
    }
    #[tokio::test]
    async fn full_transmit_queue_preserves_received_packet_and_reserves_output() {
        let (tx, mut rx) = mpsc::channel(1);
        tx.send(vec![1].into()).await.unwrap();
        let mut device = IpDevice {
            rx: VecDeque::from([vec![2]]),
            tx,
            mtu: 1280,
        };
        assert!(device.transmit(Instant::from_millis(0)).is_none());
        assert!(device.receive(Instant::from_millis(0)).is_none());
        assert_eq!(device.rx.len(), 1);
        assert_eq!(rx.recv().await.unwrap().as_slice(), &[1]);
        let (input, output) = device.receive(Instant::from_millis(0)).unwrap();
        input.consume(|packet| assert_eq!(packet, &[2]));
        assert!(device.transmit(Instant::from_millis(0)).is_none());
        output.consume(1, |packet| packet[0] = 3);
        assert_eq!(rx.recv().await.unwrap().as_slice(), &[3]);
    }
    #[tokio::test]
    async fn wireguard_authenticated_tcp_udp_and_allowed_ips() {
        authenticated_tunnel().await;
    }
    async fn authenticated_tunnel() {
        let client_secret = boringtun::x25519::StaticSecret::from([7; 32]);
        let server_secret = boringtun::x25519::StaticSecret::from([9; 32]);
        let server_public = boringtun::x25519::PublicKey::from(&server_secret);
        let client_public = boringtun::x25519::PublicKey::from(&client_secret);
        let udp = tokio::net::UdpSocket::bind("127.0.0.1:0").await.unwrap();
        let endpoint = udp.local_addr().unwrap();
        let (stack, runner, datagrams, listener) = netstack_smoltcp::StackBuilder::default()
            .enable_tcp(true)
            .enable_udp(true)
            .mtu(1380)
            .build()
            .unwrap();
        let mut tasks = tokio::task::JoinSet::new();
        if let Some(runner) = runner {
            tasks.spawn(async move {
                let _ = runner.await;
            });
        }
        let (mut sink, mut stream) = stack.split();
        {
            tasks.spawn(async move {let mut tun=Tunn::new(server_secret,client_public,None,None,2,None);let mut buffer=vec![0;65535];let mut output=vec![0;65535];let mut peer=None;loop {
            let action=tokio::select! {result=udp.recv_from(&mut buffer)=>{let(n,address)=result.unwrap();peer=Some(address);copy_action(tun.decapsulate(None,&buffer[..n],&mut output))},packet=stream.next()=>{let packet=packet.unwrap().unwrap();copy_action(tun.encapsulate(&packet,&mut output))}};
            match action {Action::Network(packet)=>{udp.send_to(&packet,peer.unwrap()).await.unwrap();},Action::Tunnel(packet)=>{sink.send(packet).await.unwrap();},Action::None=>()}
            for _ in 0..256 {match copy_action(tun.decapsulate(None,&[],&mut output)){Action::Network(packet)=>{udp.send_to(&packet,peer.unwrap()).await.unwrap();},Action::Tunnel(packet)=>{sink.send(packet).await.unwrap();},Action::None=>break}}
        }});
        }
        let (half_sent, mut half_received) = mpsc::channel(1);
        tasks.spawn(async move {
            let mut listener = listener.unwrap();
            while let Some((mut tcp, _, destination)) = listener.next().await {
                let half_sent = half_sent.clone();
                tokio::spawn(async move {
                    if destination.port() == 444 {
                        let (mut read, mut write) = tokio::io::split(tcp);
                        write.write_all(b"half").await.unwrap();
                        let closing = async {
                            let _ = write.shutdown().await;
                        };
                        let receiving = async {
                            let mut reply = Vec::new();
                            read.read_to_end(&mut reply).await.unwrap();
                            half_sent.send(reply).await.unwrap();
                        };
                        tokio::join!(closing, receiving);
                        return;
                    }
                    let mut data = vec![0; 32768];
                    loop {
                        let n = match tcp.read(&mut data).await {
                            Ok(0) | Err(_) => break,
                            Ok(n) => n,
                        };
                        if tcp.write_all(&data[..n]).await.is_err() {
                            break;
                        }
                    }
                    let _ = tcp.shutdown().await;
                });
            }
        });
        tasks.spawn(async move {
            let (mut incoming, mut outgoing) = datagrams.unwrap().split();
            while let Some((mut data, source, destination)) = incoming.next().await {
                if destination.port() == 53 && data.len() > 12 && data[2..6] == [1, 0, 0, 1] {
                    data[2] = 0x81;
                    data[3] = 0x80;
                    data[6] = 0;
                    data[7] = 1;
                    let ipv6 = data.get(data.len() - 4..data.len() - 2) == Some(&[0, 28]);
                    if ipv6 {
                        data.extend([0xc0, 0x0c, 0, 28, 0, 1, 0, 0, 0, 30, 0, 16]);
                        data.extend("fd00:7::1".parse::<Ipv6Addr>().unwrap().octets());
                    } else {
                        data.extend([0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 30, 0, 4, 10, 7, 0, 1]);
                    }
                }
                outgoing.send((data, destination, source)).await.unwrap();
            }
        });
        let p:WireProfile=serde_json::from_value(serde_json::json!({"protocol":"wireguard","server":endpoint.ip().to_string(),"port":endpoint.port(),"serverName":"localhost","credential":base64::engine::general_purpose::STANDARD.encode(client_secret.to_bytes()),"publicKey":null,"shortId":null,"options":{"peerPublicKey":base64::engine::general_purpose::STANDARD.encode(server_public.as_bytes()),"localAddresses":["10.7.0.2/32","fd00:7::2/128"],"allowedIps":["10.7.0.0/24","fd00:7::/64"],"mtu":1380}})).unwrap();
        let network = None;
        let connector = TunnelConnector::new(&p, "1.1.1.1:53".parse().unwrap(), network.clone())
            .await
            .unwrap()
            .with_dns("10.7.0.1:53".parse().unwrap());
        assert!(connector.connect("192.0.2.1".into(), 80).await.is_err());
        let mut tcp = connector.connect("echo.test".into(), 443).await.unwrap();
        assert_eq!(
            connector.destination("echo.test", 443).await.unwrap(),
            "10.7.0.1:443".parse::<SocketAddr>().unwrap()
        );
        let query = crate::dns_engine::encode_ipv4_question("echo.test", [7, 8]).unwrap();
        assert!(connector
            .dns_cache
            .get(&query, "10.7.0.1:53", false)
            .is_some());
        assert_eq!(
            connector.destination("echo.test", 443).await.unwrap(),
            "10.7.0.1:443".parse::<SocketAddr>().unwrap()
        );
        assert_eq!(
            connector
                .destination("2001:db8::1", 443)
                .await
                .unwrap_err()
                .to_string(),
            "TUNNEL_DESTINATION_OUTSIDE_ALLOWED_IPS"
        );
        assert!(!connector.ipv4_only());
        let data = vec![5; 32768];
        tcp.write_all(&data).await.unwrap();
        let mut result = vec![0; data.len()];
        tokio::time::timeout(Duration::from_secs(10), tcp.read_exact(&mut result))
            .await
            .unwrap()
            .unwrap();
        assert_eq!(data, result);
        // Interactive writes must not wait for a stale one-second poll deadline.
        // Exercise both directions repeatedly on an established encrypted flow.
        for byte in 0..8u8 {
            tcp.write_all(&[byte]).await.unwrap();
            let mut response = [0];
            tokio::time::timeout(Duration::from_millis(700), tcp.read_exact(&mut response))
                .await
                .expect("small TCP response waited for stale poll deadline")
                .unwrap();
            assert_eq!(response, [byte]);
        }
        // A response larger than all local queues must survive backpressure.
        let large = vec![3u8; 2 * 1024 * 1024];
        let (mut read, mut write) = tokio::io::split(tcp);
        let sending = async {
            write.write_all(&large).await.unwrap();
            write.shutdown().await.unwrap();
        };
        let receiving = async {
            tokio::time::sleep(Duration::from_millis(100)).await;
            let mut response = Vec::new();
            read.read_to_end(&mut response).await.unwrap();
            assert_eq!(response, large);
        };
        tokio::time::timeout(Duration::from_secs(15), async {
            tokio::join!(sending, receiving);
        })
        .await
        .unwrap();
        let mut parallel = futures::stream::FuturesUnordered::new();
        for value in 0..4u8 {
            let connector = connector.clone();
            parallel.push(async move {
                let mut stream = connector.connect("10.7.0.1".into(), 443).await.unwrap();
                stream.write_all(&[value; 1024]).await.unwrap();
                let mut reply = [0; 1024];
                stream.read_exact(&mut reply).await.unwrap();
                assert_eq!(reply, [value; 1024]);
            });
        }
        tokio::time::timeout(Duration::from_secs(5), async {
            while parallel.next().await.is_some() {}
        })
        .await
        .unwrap();
        // Receiving FIN closes only the read half; writes must still reach peer.
        let mut half = connector.connect("10.7.0.1".into(), 444).await.unwrap();
        let mut greeting = Vec::new();
        half.read_to_end(&mut greeting).await.unwrap();
        assert_eq!(greeting, b"half");
        half.write_all(&[11; 32768]).await.unwrap();
        half.shutdown().await.unwrap();
        assert_eq!(
            tokio::time::timeout(Duration::from_secs(5), half_received.recv())
                .await
                .unwrap()
                .unwrap(),
            vec![11; 32768]
        );
        drop(half);
        let mut datagram = connector.udp("10.7.0.1".into(), 53).await.unwrap();
        for len in [32, 1200] {
            let data = vec![8; len];
            datagram.send(&data).await.unwrap();
            assert_eq!(
                tokio::time::timeout(Duration::from_secs(5), datagram.receive())
                    .await
                    .unwrap()
                    .unwrap(),
                data
            );
        }
        assert!(datagram.send(&vec![0; 65536]).await.is_err());
        let mut tcp6 = connector.connect("fd00:7::1".into(), 443).await.unwrap();
        tcp6.write_all(&data).await.unwrap();
        let mut result6 = vec![0; data.len()];
        tokio::time::timeout(Duration::from_secs(5), tcp6.read_exact(&mut result6))
            .await
            .unwrap()
            .unwrap();
        assert_eq!(data, result6);
        let mut udp6 = connector.udp("fd00:7::1".into(), 443).await.unwrap();
        udp6.send(b"ipv6-udp").await.unwrap();
        assert_eq!(
            tokio::time::timeout(Duration::from_secs(5), udp6.receive())
                .await
                .unwrap()
                .unwrap(),
            b"ipv6-udp"
        );
        drop(udp6);
        drop(tcp6);
        let connector = connector.with_dns("[fd00:7::1]:53".parse().unwrap());
        assert_eq!(
            connector.destination6("echo.test", 443).await.unwrap(),
            "[fd00:7::1]:443".parse::<SocketAddr>().unwrap()
        );
        let mut named6 = connector.connect("echo.test".into(), 443).await.unwrap();
        named6.write_all(&data).await.unwrap();
        let mut named_result = vec![0; data.len()];
        tokio::time::timeout(Duration::from_secs(5), named6.read_exact(&mut named_result))
            .await
            .unwrap()
            .unwrap();
        assert_eq!(data, named_result);
        drop(named6);
        drop(datagram);
        drop(connector);
        let mut ipv4_profile = p.clone();
        ipv4_profile.options.local_addresses = Some(vec!["10.7.0.2/32".into()]);
        let ipv4 = TunnelConnector::new(
            &ipv4_profile,
            "10.7.0.1:53".parse().unwrap(),
            network.clone(),
        )
        .await
        .unwrap()
        .with_dns("10.7.0.1:53".parse().unwrap());
        assert!(ipv4.ipv4_only());
        assert_eq!(
            ipv4.resolve_candidates("echo.test".into())
                .unwrap()
                .await
                .unwrap(),
            vec!["10.7.0.1".parse::<IpAddr>().unwrap()]
        );
        assert_eq!(
            ipv4.udp("fd00:7::1".into(), 443)
                .await
                .err()
                .unwrap()
                .to_string(),
            "TUNNEL_IPV6_LOCAL_ADDRESS_REQUIRED"
        );
        assert_eq!(
            ipv4.connect("fd00:7::1".into(), 443)
                .await
                .err()
                .unwrap()
                .to_string(),
            "TUNNEL_IPV6_LOCAL_ADDRESS_REQUIRED"
        );
        drop(ipv4);
        let mut ipv6_profile = p.clone();
        ipv6_profile.options.local_addresses = Some(vec!["fd00:7::2/128".into()]);
        ipv6_profile.options.allowed_ips = Some(vec!["fd00:7::/64".into()]);
        let ipv6 = TunnelConnector::new(&ipv6_profile, "1.1.1.1:53".parse().unwrap(), network)
            .await
            .unwrap()
            .with_upstreams(vec!["[fd00:7::1]:53".parse().unwrap()])
            .unwrap();
        assert!(ipv6.ipv6_only());
        assert!(ipv6.destination("10.7.0.1", 443).await.is_err());
        let mut named = ipv6.connect("echo.test".into(), 443).await.unwrap();
        named.write_all(&data).await.unwrap();
        let mut result = vec![0; data.len()];
        tokio::time::timeout(Duration::from_secs(5), named.read_exact(&mut result))
            .await
            .unwrap()
            .unwrap();
        assert_eq!(data, result);
        let mut datagram = ipv6.udp("fd00:7::1".into(), 443).await.unwrap();
        datagram.send(b"ipv6-only").await.unwrap();
        assert_eq!(
            tokio::time::timeout(Duration::from_secs(5), datagram.receive())
                .await
                .unwrap()
                .unwrap(),
            b"ipv6-only"
        );
        drop(named);
        drop(datagram);
        drop(ipv6);
        tasks.abort_all();
    }
}

#[cfg(test)]
#[path="awg_packet_lab.rs"]
mod awg_packet_lab;
