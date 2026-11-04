//! Stateful packet forwarding. Routes are fixed before the first SYN/datagram.
//! TCP uses the Windows stack; UDP/QUIC is forwarded as original IP datagrams.
use crate::packet_budget::Packet;
use std::{
    collections::HashMap,
    future::Future,
    io,
    net::{IpAddr, SocketAddr},
    pin::Pin,
    sync::{
        atomic::{AtomicU64, Ordering},
        Arc,
    },
    time::{Duration, Instant},
};
pub type PacketFuture<'a> = Pin<Box<dyn Future<Output = io::Result<Packet>> + Send + 'a>>;
pub type SendFuture<'a> = Pin<Box<dyn Future<Output = io::Result<()>> + Send + 'a>>;
pub trait PacketRelay: Send + Sync {
    fn send<'a>(&'a self, packet: &'a [u8]) -> SendFuture<'a>;
    fn receive(&self) -> PacketFuture<'_>;
    fn delivery_snapshot(&self)->Option<[u64;6]>{None}
}
pub type OpenFuture = Pin<Box<dyn Future<Output = io::Result<Box<dyn PacketRelay>>> + Send>>;
#[derive(Clone, Copy, Debug)]
pub struct Tuple {
    pub source: SocketAddr,
    pub destination: SocketAddr,
    pub protocol: u8,
    pub flags: u8,
    pub payload: usize,
}
pub fn tuple(p: &[u8]) -> Option<Tuple> {
    let (h, protocol, source, destination) = match p.first()? >> 4 {
        4 if p.len() >= 20 => {
            let h = (p[0] & 15) as usize * 4;
            if h < 20
                || h > p.len()
                || u16::from_be_bytes([p[2], p[3]]) as usize != p.len()
                || u16::from_be_bytes([p[6], p[7]]) & 0x3fff != 0
            {
                return None;
            }
            (
                h,
                p[9],
                IpAddr::V4(<[u8; 4]>::try_from(&p[12..16]).ok()?.into()),
                IpAddr::V4(<[u8; 4]>::try_from(&p[16..20]).ok()?.into()),
            )
        }
        6 if p.len() >= 40 => {
            if 40 + u16::from_be_bytes([p[4], p[5]]) as usize != p.len() {
                return None;
            }
            (
                40,
                p[6],
                IpAddr::V6(<[u8; 16]>::try_from(&p[8..24]).ok()?.into()),
                IpAddr::V6(<[u8; 16]>::try_from(&p[24..40]).ok()?.into()),
            )
        }
        _ => return None,
    };
    let data = p.get(h..)?;
    let header = match protocol {
        6 if data.len() >= 20 => {
            let n = (data[12] >> 4) as usize * 4;
            if n < 20 || n > data.len() {
                return None;
            }
            n
        }
        17 if data.len() >= 8 => {
            if u16::from_be_bytes([data[4], data[5]]) as usize != data.len() {
                return None;
            }
            8
        }
        _ => return None,
    };
    Some(Tuple {
        source: SocketAddr::new(source, u16::from_be_bytes([data[0], data[1]])),
        destination: SocketAddr::new(destination, u16::from_be_bytes([data[2], data[3]])),
        protocol,
        flags: if protocol == 6 { data[13] } else { 0 },
        payload: data.len() - header,
    })
}
fn update(checksum: &mut [u8], old: &[u8], new: &[u8]) {
    let mut sum = (!u16::from_be_bytes([checksum[0], checksum[1]])) as u32;
    for (a, b) in old.chunks_exact(2).zip(new.chunks_exact(2)) {
        sum += (!u16::from_be_bytes([a[0], a[1]])) as u32 + u16::from_be_bytes([b[0], b[1]]) as u32;
        sum = (sum & 65535) + (sum >> 16);
    }
    while sum > 65535 {
        sum = (sum & 65535) + (sum >> 16);
    }
    checksum.copy_from_slice(&(!(sum as u16)).to_be_bytes());
}
fn checksum(bytes: &[u8]) -> u16 {
    let mut sum = 0u32;
    for pair in bytes.chunks(2) {
        sum += u16::from_be_bytes([pair[0], *pair.get(1).unwrap_or(&0)]) as u32;
    }
    while sum > 65535 {
        sum = (sum & 65535) + (sum >> 16);
    }
    !(sum as u16)
}
/// Incremental checksum updates also preserve checksums in short ICMP quotes.
pub fn translate(p: &mut [u8], endpoint: SocketAddr, source: bool) -> io::Result<()> {
    let bad = || io::Error::new(io::ErrorKind::InvalidData, "AWG_PACKET_TRANSLATION_INVALID");
    let (h, protocol, a, new_ip) = match (p.first().map(|v| v >> 4), endpoint.ip()) {
        (Some(4), IpAddr::V4(ip)) if p.len() >= 20 => {
            let h = (p[0] & 15) as usize * 4;
            if h < 20 || h > p.len() || u16::from_be_bytes([p[6], p[7]]) & 0x1fff != 0 {
                return Err(bad());
            }
            (h, p[9], if source { 12 } else { 16 }, ip.octets().to_vec())
        }
        (Some(6), IpAddr::V6(ip)) if p.len() >= 40 => {
            (40, p[6], if source { 8 } else { 24 }, ip.octets().to_vec())
        }
        _ => return Err(bad()),
    };
    if !matches!(protocol, 6 | 17) || p.len() < h + 4 {
        return Err(bad());
    }
    let old_ip = p[a..a + new_ip.len()].to_vec();
    let port = h + if source { 0 } else { 2 };
    let old_port: [u8; 2] = p[port..port + 2].try_into().unwrap();
    let new_port = endpoint.port().to_be_bytes();
    if h != 40 {
        update(&mut p[10..12], &old_ip, &new_ip);
    }
    let c = h + if protocol == 6 { 16 } else { 6 };
    if p.len() >= c + 2 && !(protocol == 17 && h != 40 && p[c..c + 2] == [0, 0]) {
        update(&mut p[c..c + 2], &old_ip, &new_ip);
        update(&mut p[c..c + 2], &old_port, &new_port);
        if protocol == 17 && p[c..c + 2] == [0, 0] {
            p[c..c + 2].copy_from_slice(&[255, 255]);
        }
    }
    p[a..a + new_ip.len()].copy_from_slice(&new_ip);
    p[port..port + 2].copy_from_slice(&new_port);
    Ok(())
}
pub fn translate_reply(p: &mut [u8], endpoint: SocketAddr) -> io::Result<()> {
    if tuple(p).is_some() {
        return translate(p, endpoint, false);
    }
    let (h, protocol) = match p.first().map(|v| v >> 4) {
        Some(4) if p.len() >= 20 => ((p[0] & 15) as usize * 4, p[9]),
        Some(6) if p.len() >= 40 => (40, p[6]),
        _ => return Err(io::ErrorKind::InvalidData.into()),
    };
    if p.len() < h + 8
        || !((protocol == 1 && matches!(p[h], 3 | 11 | 12)) || (protocol == 58 && p[h] < 128))
    {
        return Err(io::ErrorKind::InvalidData.into());
    }
    translate(&mut p[h + 8..], endpoint, true)?;
    match endpoint.ip() {
        IpAddr::V4(ip) if protocol == 1 => {
            let old = p[16..20].to_vec();
            update(&mut p[10..12], &old, &ip.octets());
            p[16..20].copy_from_slice(&ip.octets());
        }
        IpAddr::V6(ip) if protocol == 58 => p[24..40].copy_from_slice(&ip.octets()),
        _ => return Err(io::ErrorKind::InvalidData.into()),
    }
    p[h + 2..h + 4].fill(0);
    let c = if protocol == 1 {
        checksum(&p[h..])
    } else {
        let mut pseudo = p[8..40].to_vec();
        pseudo.extend_from_slice(&((p.len() - 40) as u32).to_be_bytes());
        pseudo.extend_from_slice(&[0, 0, 0, 58]);
        pseudo.extend_from_slice(&p[40..]);
        checksum(&pseudo)
    };
    p[h + 2..h + 4].copy_from_slice(&c.to_be_bytes());
    Ok(())
}
pub fn error_tuple(p: &[u8], local: IpAddr) -> Option<(u8, u16, SocketAddr)> {
    let (h, protocol, destination) = match p.first()? >> 4 {
        4 if p.len() >= 20 => {
            let h = (p[0] & 15) as usize * 4;
            if h < 20
                || h > p.len()
                || p.len() != u16::from_be_bytes([p[2], p[3]]) as usize
                || checksum(&p[..h]) != 0
            {
                return None;
            }
            (
                h,
                p[9],
                IpAddr::V4(<[u8; 4]>::try_from(&p[16..20]).ok()?.into()),
            )
        }
        6 if p.len() >= 40 => {
            if p.len() != 40 + u16::from_be_bytes([p[4], p[5]]) as usize {
                return None;
            }
            (
                40,
                p[6],
                IpAddr::V6(<[u8; 16]>::try_from(&p[24..40]).ok()?.into()),
            )
        }
        _ => return None,
    };
    if destination != local
        || p.len() < h + 8
        || !((protocol == 1 && matches!(p[h], 3 | 11 | 12))
            || (protocol == 58 && matches!(p[h], 1..=4)))
    {
        return None;
    }
    if protocol == 1 {
        if checksum(&p[h..]) != 0 {
            return None;
        }
    } else {
        let mut pseudo = p[8..40].to_vec();
        pseudo.extend_from_slice(&((p.len() - 40) as u32).to_be_bytes());
        pseudo.extend_from_slice(&[0, 0, 0, 58]);
        pseudo.extend_from_slice(&p[40..]);
        if checksum(&pseudo) != 0 {
            return None;
        }
    }
    let q = &p[h + 8..];
    let (qh, proto, source, remote) = match q.first()? >> 4 {
        4 if q.len() >= 20 => {
            let qh = (q[0] & 15) as usize * 4;
            if qh < 20
                || qh > q.len()
                || checksum(&q[..qh]) != 0
                || u16::from_be_bytes([q[6], q[7]]) & 0x1fff != 0
            {
                return None;
            }
            (
                qh,
                q[9],
                IpAddr::V4(<[u8; 4]>::try_from(&q[12..16]).ok()?.into()),
                IpAddr::V4(<[u8; 4]>::try_from(&q[16..20]).ok()?.into()),
            )
        }
        6 if q.len() >= 40 => (
            40,
            q[6],
            IpAddr::V6(<[u8; 16]>::try_from(&q[8..24]).ok()?.into()),
            IpAddr::V6(<[u8; 16]>::try_from(&q[24..40]).ok()?.into()),
        ),
        _ => return None,
    };
    if source != local || !matches!(proto, 6 | 17) || q.len() < qh + 4 {
        return None;
    }
    Some((
        proto,
        u16::from_be_bytes([q[qh], q[qh + 1]]),
        SocketAddr::new(remote, u16::from_be_bytes([q[qh + 2], q[qh + 3]])),
    ))
}
type Key = (u8, SocketAddr, SocketAddr);
#[derive(Default)]
struct Progress {
    sent: AtomicU64,
    received: AtomicU64,
    send_wait: AtomicU64,
    output_wait: AtomicU64,
    tun_delivered: AtomicU64,
    max_packet_bytes: AtomicU64,
    first_response_us: AtomicU64,
}
struct Entry {
    relay: Arc<dyn PacketRelay>,
    last: Arc<std::sync::Mutex<Instant>>,
    closed: Arc<std::sync::atomic::AtomicU8>,
    closed_at: Option<Instant>,
    task: tokio::task::JoinHandle<()>,
    flow: crate::trace::Flow,
    progress: Arc<Progress>,
    reported: (u64, u64),
    resource_names: Vec<String>,
    dns_name_count:usize,
    observed_name:Option<String>,
    diagnostic_flow:bool,
}
#[derive(Clone,Default)]
pub struct DeliveryLedger(Arc<std::sync::Mutex<HashMap<Key,std::sync::Weak<Progress>>>>);
impl DeliveryLedger{
    pub fn record(&self,packet:&[u8]){
        let Some(t)=tuple(packet) else{return;};
        let progress=self.0.lock().ok().and_then(|entries|entries.get(&(t.protocol,t.destination,t.source)).and_then(std::sync::Weak::upgrade));
        if let Some(progress)=progress{progress.tun_delivered.fetch_add(t.payload as u64,Ordering::Relaxed);}
    }
}
#[cfg(test)]
mod delivery_tests{
    use super::*;
    #[test]
    fn delivered_payload_is_counted_only_for_matching_reverse_flow(){
        for (source,remote) in [("172.19.0.1:5000","198.51.100.2:443"),("[fd00::1]:5000","[2001:db8::2]:443")]{
            let source=source.parse().unwrap();let remote=remote.parse().unwrap();let progress=Arc::new(Progress::default());let ledger=DeliveryLedger::default();
            ledger.0.lock().unwrap().insert((17,source,remote),Arc::downgrade(&progress));
            assert_eq!(progress.tun_delivered.load(Ordering::Relaxed),0);
            ledger.record(&udp_packet(source,remote,b"outgoing"));assert_eq!(progress.tun_delivered.load(Ordering::Relaxed),0);
            ledger.record(&udp_packet(remote,source,b"delivered"));assert_eq!(progress.tun_delivered.load(Ordering::Relaxed),9);
            ledger.record(&[0;8]);assert_eq!(progress.tun_delivered.load(Ordering::Relaxed),9);
        }
    }
}
impl Drop for Entry {
    fn drop(&mut self) {
        self.task.abort();
    }
}
pub struct Manager {
    delivery_ledger:Option<DeliveryLedger>,
    names: Option<Arc<crate::tun_names::NameCache>>,
    router: Arc<crate::native_router::NativeRouter>,
    output: tokio::sync::mpsc::Sender<Packet>,
    entries: HashMap<Key, Entry>,
    fallback: HashMap<Key, Instant>,
    retired: HashMap<Key, Instant>,
    reassembly: crate::tunnel_fragments::Reassembly,
    received: Arc<AtomicU64>,
    sent: u64,
    dropped: u64,
    last_report: Instant,
    last_maintenance: Instant,
    fallback_reasons: HashMap<&'static str, u64>,
    drop_reasons: HashMap<&'static str, u64>,
    progress_cursor: u64,
    family_rejected:u64,
    rejection_window:(Instant,u32),
}
impl Manager {
    pub fn new(
        router: Arc<crate::native_router::NativeRouter>,
        output: tokio::sync::mpsc::Sender<Packet>,
    ) -> Self {
        Self {
            delivery_ledger:crate::resource_diagnostics::active().then(DeliveryLedger::default),
            names: None,
            router,
            output,
            entries: HashMap::new(),
            fallback: HashMap::new(),
            retired: HashMap::new(),
            reassembly: Default::default(),
            received: Arc::new(AtomicU64::new(0)),
            sent: 0,
            dropped: 0,
            last_report: Instant::now(),
            last_maintenance: Instant::now(),
            fallback_reasons: HashMap::new(),
            drop_reasons: HashMap::new(),
            progress_cursor: 0,
            family_rejected:0,rejection_window:(Instant::now(),0),
        }
    }
    pub fn maintain(&mut self) {
        let now = Instant::now();
        if self.last_maintenance.elapsed() < Duration::from_secs(1) {
            return;
        }
        self.last_maintenance = now;
        self.fallback
            .retain(|_, at| now.duration_since(*at) < Duration::from_secs(7200));
        self.retired
            .retain(|_, at| now.duration_since(*at) < Duration::from_secs(120));
        let mut keys: Vec<_> = self
            .entries
            .iter_mut()
            .filter_map(|(key, e)| {
                let idle = e
                    .last
                    .lock()
                    .map(|at| at.elapsed())
                    .unwrap_or(Duration::MAX);
                let closing = e.closed.load(Ordering::Relaxed) >= 3;
                let expired = if closing {
                    e.closed_at.get_or_insert(now).elapsed() >= Duration::from_secs(120)
                } else {
                    idle >= Duration::from_secs(if key.0 == 17 { 60 } else { 7200 })
                };
                expired.then_some(*key)
            })
            .collect();
        let mut closing: Vec<_> = self
            .entries
            .iter()
            .filter_map(|(key, e)| e.closed_at.map(|at| (*key, at)))
            .collect();
        closing.sort_unstable_by_key(|(_, at)| *at);
        let excess = closing.len().saturating_sub(128);
        keys.extend(closing.into_iter().take(excess).map(|(key, _)| key));
        for key in keys {
            if let Some(entry)=self.entries.remove(&key){crate::trace::emit_with_flow(serde_json::json!({"stage":"relay","code":"AWG_PACKET_FLOW_CLOSED","path":"packet","transport":if key.0==6{"tcp"}else{"udp"},"source":key.1.to_string(),"destination":key.2.to_string(),"sentBytes":entry.progress.sent.load(Ordering::Relaxed),"receivedBytes":entry.progress.received.load(Ordering::Relaxed),"reason":"retired"}),Some(&entry.flow));}
            self.retired.insert(key, now);
        }
        if self.last_report.elapsed() >= Duration::from_secs(10) {
            self.last_report = now;
            if self.family_rejected>0 {crate::trace::emit(serde_json::json!({"stage":"tun","code":"AWG_PACKET_FAMILY_REJECTED","reason":"address_family","count":self.family_rejected}));}
            crate::trace::emit(
                serde_json::json!({"stage":"tun","code":"AWG_PACKET_METRICS","active":self.entries.len(),"fallback":self.fallback.len(),"forwardedPackets":self.sent,"receivedPackets":self.received.load(Ordering::Relaxed),"droppedPackets":self.dropped}),
            );
            for (reason, count) in &self.fallback_reasons {
                crate::trace::emit(
                    serde_json::json!({"stage":"routing","code":"AWG_PACKET_FALLBACK_METRICS","reason":reason,"count":count}),
                );
            }
            for (reason, count) in &self.drop_reasons {
                crate::trace::emit(
                    serde_json::json!({"stage":"tun","code":"AWG_PACKET_DROP_METRICS","reason":reason,"count":count}),
                );
            }
            let mut ordered: Vec<_> = self.entries.iter_mut().collect();
            ordered.sort_unstable_by_key(|(_, entry)| entry.flow.id);
            let pivot = ordered.partition_point(|(_, entry)| entry.flow.id <= self.progress_cursor);
            ordered.rotate_left(pivot);
            let mut emitted = 0;
            for ((protocol, source, destination), entry) in ordered {
                let bytes = (
                    entry.progress.sent.load(Ordering::Relaxed),
                    entry.progress.received.load(Ordering::Relaxed),
                );
                let selected=entry.diagnostic_flow && (!entry.resource_names.is_empty() || bytes.1>=1024*1024);
                let snapshot=if selected{entry.relay.delivery_snapshot()}else{None};
                let heartbeat=selected && entry.closed.load(Ordering::Relaxed)<3 && (bytes.1>65536 || snapshot.is_some_and(|s|s[2]>0));
                if bytes == entry.reported && !heartbeat {
                    continue;
                }
                entry.reported = bytes;
                if selected{let host=entry.resource_names.first().map(String::as_str);crate::trace::FLOW.sync_scope(entry.flow.clone(),||{
                    crate::resource_diagnostics::emit_optional(host,"RESOURCE_PACKET_PROGRESS",serde_json::json!({"stage":"relay","destination":destination.to_string(),"source":source.to_string(),"transport":if *protocol==6{"tcp"}else{"udp"},"dnsNameCount":entry.dns_name_count,"nameSource":if host.is_none(){"unknown_bulk_candidate"}else if entry.dns_name_count==1{"unique_dns"}else{"dns_candidates"},"sentBytes":bytes.0,"receivedBytes":bytes.1,"tunDeliveredBytes":entry.progress.tun_delivered.load(Ordering::Relaxed),"maxPacketBytes":entry.progress.max_packet_bytes.load(Ordering::Relaxed),"durationMs":entry.progress.first_response_us.load(Ordering::Relaxed)/1000,"sendWaitUs":entry.progress.send_wait.load(Ordering::Relaxed),"receiveWaitUs":entry.progress.output_wait.load(Ordering::Relaxed)}));
                    if let Some(s)=snapshot{crate::resource_diagnostics::emit_optional(host,"RESOURCE_NATIVE_DELIVERY",serde_json::json!({"stage":"relay","side":"native_ingress","receivedBytes":s[0],"sentBytes":s[1],"rxQueuedBytes":s[2],"droppedPackets":s[3],"receiveWaitUs":s[4],"readEof":s[5]!=0}));}
                });}
                crate::trace::FLOW.sync_scope(entry.flow.clone(),||crate::trace::emit(serde_json::json!({"stage":"relay","code":"AWG_PACKET_FLOW_PROGRESS","path":"packet","destinationName":entry.observed_name,"dnsNameCount":entry.dns_name_count,"source":source.to_string(),"destination":destination.to_string(),"transport":if *protocol==6{"tcp"}else{"udp"},"target":"PROXY","sentBytes":bytes.0,"receivedBytes":bytes.1,"sendWaitUs":entry.progress.send_wait.load(Ordering::Relaxed),"receiveWaitUs":entry.progress.output_wait.load(Ordering::Relaxed)})));
                self.progress_cursor = entry.flow.id;
                emitted += 1;
                if emitted >= 32 {
                    break;
                }
            }
        }
    }
    /// None means consumed; Some(packet) belongs to the existing terminating stack.
    pub fn with_names(mut self, names:Arc<crate::tun_names::NameCache>)->Self {self.names=Some(names);self}
    pub fn delivery_ledger(&self)->Option<DeliveryLedger>{self.delivery_ledger.clone()}
    pub async fn process(&mut self, p: &[u8]) -> io::Result<Option<Vec<u8>>> {
        self.maintain();
        let p = if tuple(p).is_some() {
            std::borrow::Cow::Borrowed(p)
        } else {
            match self.reassembly.accept(p) {
                Ok(Some(p)) => std::borrow::Cow::Owned(p),
                Ok(None) => return Ok(None),
                Err(error) => {
                    self.dropped += 1;
                    let reason=match error.kind(){io::ErrorKind::Unsupported=>"fragment_unsupported",io::ErrorKind::WouldBlock=>"fragment_capacity",_=>"fragment_invalid"};
                    *self.drop_reasons.entry(reason).or_default() += 1;
                    return Ok(None);
                }
            }
        };
        let Some(t) = tuple(&p) else {
            return Ok(Some(p.into_owned()));
        };
        if t.destination.port() == 53 {
            return Ok(Some(p.into_owned()));
        }
        let key = (t.protocol, t.source, t.destination);
        let syn = t.protocol == 6 && t.flags & 0x17 == 2;
        let new_flow=!self.entries.contains_key(&key) && !self.fallback.contains_key(&key) && (syn || t.protocol==17);
        let (names,name_state,name_ttl)=if new_flow {self.names.as_ref().map(|cache|cache.routing_observation(t.destination.ip())).unwrap_or((Vec::new(),"unavailable",0))} else {(Vec::new(),"flow_pinned",0)};
        if new_flow && self.router.reject_packet_family(t.destination.ip()) && self.router.packet_route_is_proxy(t.destination.ip(), &names){
            self.family_rejected+=1;
            if self.rejection_window.0.elapsed()>=Duration::from_secs(1){self.rejection_window=(Instant::now(),0);}
            if self.rejection_window.1<32 {
                self.rejection_window.1+=1;
                let reply=if syn {crate::tun_reject::tcp_syn_reset(&p)}else{crate::tun_reject::udp_unreachable_quote(&p)};
                if let Some(reply)=reply{let _=self.output.try_send(reply.into());}
            }
            return Ok(None);
        }
        if syn || t.protocol == 17 {
            self.retired.remove(&key);
        } else if self.retired.contains_key(&key) {
            self.dropped += 1;
            *self.drop_reasons.entry("retired_flow").or_default() += 1;
            return Ok(None);
        }
        if let Some(at) = self.fallback.get_mut(&key) {
            *at = Instant::now();
            return Ok(Some(p.into_owned()));
        }
        if syn
            && self
                .entries
                .get(&key)
                .is_some_and(|e| e.closed.load(Ordering::Relaxed) >= 3)
        {
            self.entries.remove(&key);
        }
        if !self.entries.contains_key(&key) {
            if !syn && t.protocol == 6 {
                return Ok(Some(p.into_owned()));
            }
            if self.entries.len() + self.fallback.len() + self.retired.len() >= 8192 {
                self.dropped += 1;
                *self
                    .drop_reasons
                    .entry("routing_table_capacity")
                    .or_default() += 1;
                return Ok(None);
            }
            let open = if self
                .entries
                .values()
                .filter(|e| e.closed.load(Ordering::Relaxed) < 3)
                .count()
                < 512
            {
                self.router.raw_packet_observed(t.source, t.destination, t.protocol, &names)
            } else {
                Err("capacity")
            };
            let open = match open {
                Ok(open) => open,
                Err(reason) => {
                    *self.fallback_reasons.entry(reason).or_default() += 1;
                    self.fallback.insert(key, Instant::now());
                    crate::trace::emit(
                        serde_json::json!({"stage":"routing","code":"AWG_PACKET_FALLBACK","path":"fallback","source":t.source.to_string(),"destination":t.destination.to_string(),"reason":reason,"addressFamily":if t.destination.is_ipv6(){"ipv6"}else{"ipv4"},"dnsNameCount":names.len(),"nameSource":name_state,"ttlSeconds":name_ttl}),
                    );
                    return Ok(Some(p.into_owned()));
                }
            };
            let flow = crate::trace::new_flow();
            let relay = match crate::trace::FLOW.scope(flow.clone(), open).await {
                Ok(relay) => Arc::<dyn PacketRelay>::from(relay),
                Err(error) => {
                    let reason = packet_error_reason(&error);
                    *self.fallback_reasons.entry(reason).or_default() += 1;
                    crate::trace::FLOW.sync_scope(flow,||crate::trace::emit(serde_json::json!({"stage":"routing","code":"AWG_PACKET_FALLBACK","path":"fallback","source":t.source.to_string(),"destination":t.destination.to_string(),"reason":reason})));
                    self.fallback.insert(key, Instant::now());
                    return Ok(Some(p.into_owned()));
                }
            };
            let last = Arc::new(std::sync::Mutex::new(Instant::now()));
            let closed = Arc::new(std::sync::atomic::AtomicU8::new(0));
            let progress = Arc::new(Progress::default());
            let received_progress = progress.clone();
            let entry_flow = flow.clone();
            let observed_name=if names.len()==1 {Some(names[0].clone())}else{None};
            let resource_names=crate::resource_diagnostics::packet_candidates(&names);
            let task_resource_names=resource_names.clone();let dns_name_count=names.len();
            let diagnostic_flow=!resource_names.is_empty() || (names.is_empty() && crate::resource_diagnostics::bulk_udp() && t.protocol==17 && t.destination.port()==443);
            if diagnostic_flow{if let Some(ledger)=&self.delivery_ledger{let mut entries=ledger.0.lock().map_err(|_|io::ErrorKind::Other)?;entries.retain(|_,entry|entry.strong_count()>0);entries.insert(key,Arc::downgrade(&progress));}}
            let name_source=if names.is_empty(){"unconditional_route"}else{"dns_observation"};
            let (inbound, output, activity, closing, count) = (
                relay.clone(),
                self.output.clone(),
                last.clone(),
                closed.clone(),
                self.received.clone(),
            );
            let opened=Instant::now();let task=tokio::spawn(crate::trace::FLOW.scope(flow,async move {
                for host in &task_resource_names{crate::resource_diagnostics::emit(host,"RESOURCE_PACKET_CANDIDATE",serde_json::json!({"stage":"routing","destination":t.destination.to_string(),"source":t.source.to_string(),"transport":if t.protocol==6{"tcp"}else{"udp"},"dnsNameCount":dns_name_count,"nameSource":if dns_name_count==1{"unique_dns"}else{"dns_candidates"}}));}
                if let Some(host)=observed_name.as_deref().filter(|name|crate::resource_diagnostics::enabled(name)){crate::resource_diagnostics::emit(host,"RESOURCE_PACKET_ROUTE",serde_json::json!({"stage":"routing","target":"PROXY","source":t.source.to_string(),"destination":t.destination.to_string(),"transport":if t.protocol==6{"tcp"}else{"udp"}}));}
                crate::trace::emit(serde_json::json!({"stage":"routing","code":"AWG_PACKET_ADMITTED","source":t.source.to_string(),"destination":t.destination.to_string(),"destinationName":observed_name,"nameSource":name_source,"target":"PROXY","transport":if t.protocol==6{"tcp"}else{"udp"},"addressFamily":if t.destination.is_ipv6(){"ipv6"}else{"ipv4"}}));
                let mut first=true;loop{match inbound.receive().await{Ok(packet)=>{
                    if let Ok(mut at)=activity.lock(){*at=Instant::now();}count.fetch_add(1,Ordering::Relaxed);if diagnostic_flow{received_progress.max_packet_bytes.fetch_max(packet.len() as u64,Ordering::Relaxed);let host=task_resource_names.first().map(String::as_str);if let Some((_,_,_,mtu))=crate::tunnel_pmtu::message(&packet,t.source.ip()){crate::resource_diagnostics::emit_optional(host,"RESOURCE_PMTU",serde_json::json!({"stage":"transport","destination":t.destination.to_string(),"mtu":mtu}));}let icmp=match packet.first().map(|b|b>>4){Some(4) if packet.get(9)==Some(&1)=>Some((packet[0]&15) as usize*4),Some(6) if packet.get(6)==Some(&58)=>Some(40),_=>None};if let Some(h)=icmp{if let(Some(kind),Some(code))=(packet.get(h),packet.get(h+1)){crate::resource_diagnostics::emit_optional(host,"RESOURCE_PACKET_CONTROL",serde_json::json!({"stage":"transport","destination":t.destination.to_string(),"icmpType":kind,"icmpCode":code}));}}}
                    if let Some(reply)=tuple(&packet){received_progress.received.fetch_add(reply.payload as u64,Ordering::Relaxed);if reply.flags&4!=0{closing.store(3,Ordering::Relaxed);}else if reply.flags&1!=0{closing.fetch_or(2,Ordering::Relaxed);}if first && reply.payload>0{first=false;if diagnostic_flow{received_progress.first_response_us.store(opened.elapsed().as_micros().max(1).min(u64::MAX as u128) as u64,Ordering::Relaxed);}if let Some(host)=task_resource_names.first(){crate::resource_diagnostics::emit(host,"RESOURCE_PACKET_FIRST_BYTE",serde_json::json!({"stage":"relay","destination":t.destination.to_string(),"dnsNameCount":dns_name_count,"nameSource":if dns_name_count==1{"unique_dns"}else{"dns_candidates"},"receivedBytes":reply.payload,"side":"transport"}));}crate::trace::emit(serde_json::json!({"stage":"relay","code":"AWG_PACKET_FIRST_REMOTE_BYTE","source":t.source.to_string(),"destination":t.destination.to_string(),"target":"PROXY"}));}}
                    let started=Instant::now();if output.send(packet).await.is_err(){break;}received_progress.output_wait.fetch_add(started.elapsed().as_micros().min(u64::MAX as u128) as u64,Ordering::Relaxed);
                },Err(_)=>{crate::trace::code("relay","AWG_PACKET_RECEIVE_FAILED");break;}}}
            }));
            self.entries.insert(
                key,
                Entry {
                    relay,
                    last,
                    closed,
                    closed_at: None,
                    task,
                    flow: entry_flow,
                    progress,
                    reported: (0, 0),
                    resource_names,
                    dns_name_count,
                    observed_name:names.first().filter(|_|names.len()==1).cloned(),
                    diagnostic_flow,
                },
            );
        }
        let e = &self.entries[&key];
        if let Ok(mut at) = e.last.lock() {
            *at = Instant::now();
        }
        if t.flags & 4 != 0 {
            e.closed.store(3, Ordering::Relaxed);
        } else if t.flags & 1 != 0 {
            e.closed.fetch_or(1, Ordering::Relaxed);
        }
        let started = Instant::now();
        let result = e.relay.send(&p).await;
        e.progress.send_wait.fetch_add(
            started.elapsed().as_micros().min(u64::MAX as u128) as u64,
            Ordering::Relaxed,
        );
        match result {
            Ok(()) => {
                self.sent += 1;
                e.progress
                    .sent
                    .fetch_add(t.payload as u64, Ordering::Relaxed);
            }
            Err(error) => {
                self.dropped += 1;
                *self
                    .drop_reasons
                    .entry(packet_error_reason(&error))
                    .or_default() += 1;
                if error.kind() == io::ErrorKind::BrokenPipe {
                    return Err(error);
                }
            }
        };
        Ok(None)
    }
}
fn packet_error_reason(error: &io::Error) -> &'static str {
    match error.to_string().as_str() {
        "TUNNEL_IPV4_LOCAL_ADDRESS_REQUIRED"
        | "TUNNEL_IPV6_LOCAL_ADDRESS_REQUIRED"
        | "AWG_PACKET_FAMILY_MISMATCH" => "address_family",
        "TUNNEL_DESTINATION_OUTSIDE_ALLOWED_IPS" => "allowed_ips",
        "AWG_RAW_PACKET_BUDGET_EXHAUSTED" => "budget",
        "AWG_PACKET_QUEUE_FULL" => "queue_full",
        "AWG_PACKET_ENGINE_STOPPED" => "closed_queue",
        "TUNNEL_FLOW_CAPACITY_REACHED" | "TUNNEL_PORTS_EXHAUSTED" => "capacity",
        _ => match error.kind() {
            io::ErrorKind::BrokenPipe => "closed_queue",
            io::ErrorKind::WouldBlock => "queue_full",
            io::ErrorKind::InvalidData => "packet_invalid",
            _ => "open_or_transport_failed",
        },
    }
}

#[cfg(test)]
pub(crate) fn udp_packet(source: SocketAddr, remote: SocketAddr, data: &[u8]) -> Vec<u8> {
    use smoltcp::wire::*;
    let h = if source.is_ipv6() { 40 } else { 20 };
    let mut p = vec![0; h + 8 + data.len()];
    match (source.ip(), remote.ip()) {
        (IpAddr::V4(a), IpAddr::V4(b)) => Ipv4Repr {
            src_addr: a,
            dst_addr: b,
            next_header: IpProtocol::Udp,
            payload_len: 8 + data.len(),
            hop_limit: 64,
        }
        .emit(&mut Ipv4Packet::new_unchecked(&mut p), &Default::default()),
        (IpAddr::V6(a), IpAddr::V6(b)) => Ipv6Repr {
            src_addr: a,
            dst_addr: b,
            next_header: IpProtocol::Udp,
            payload_len: 8 + data.len(),
            hop_limit: 64,
        }
        .emit(&mut Ipv6Packet::new_unchecked(&mut p)),
        _ => panic!("family mismatch"),
    }
    UdpRepr {
        src_port: source.port(),
        dst_port: remote.port(),
    }
    .emit(
        &mut UdpPacket::new_unchecked(&mut p[h..]),
        &source.ip().into(),
        &remote.ip().into(),
        data.len(),
        |buf| buf.copy_from_slice(data),
        &Default::default(),
    );
    p
}
#[cfg(test)]
mod tests {
    struct Ipv4Only;
    impl crate::proxy_inbound::OutboundConnector for Ipv4Only {
        fn ipv4_only(&self)->bool{true}
        fn connect(&self,_:String,_:u16)->crate::proxy_inbound::ConnectFuture{panic!("unsupported IPv6 must not create TCP")}
    }
    #[tokio::test]
    async fn full_proxy_refuses_unsupported_ipv6_before_stack_but_rules_keep_fallback(){
        let source="[fdfe::2]:4000".parse().unwrap();let remote="[2001:db8::1]:443".parse().unwrap();
        for full in [false,true]{
            let router=crate::native_router::NativeRouter::new(Arc::new(Ipv4Only),vec![crate::ipc_contract::WireRule{kind:"MATCH".into(),value:String::new(),target:"PROXY".into(),no_resolve:false,source:"test".into()}],"1.1.1.1:53".parse().unwrap(),None).unwrap().with_full_awg_family_policy(full);
            let(tx,mut rx)=tokio::sync::mpsc::channel(8);let mut manager=Manager::new(Arc::new(router),tx);
            let udp=udp_packet(source,remote,b"fixture");let result=manager.process(&udp).await.unwrap();
            assert_eq!(result.is_none(),full);if full{let reply=rx.recv().await.unwrap();assert_eq!(reply[6],58);assert_eq!(&reply[40..42],&[1,1]);assert!(manager.fallback.is_empty());}
            let mut syn=udp_packet(source,remote,&[0;12]);syn[6]=6;syn[44..60].fill(0);syn[44..48].copy_from_slice(&123u32.to_be_bytes());syn[52]=0x50;syn[53]=2;
            let mut pseudo=syn[8..40].to_vec();pseudo.extend(20u32.to_be_bytes());pseudo.extend([0,0,0,6]);pseudo.extend(&syn[40..]);syn[56..58].copy_from_slice(&checksum(&pseudo).to_be_bytes());
            // New port avoids the pinned UDP tuple and tests a fresh SYN.
            syn[40..42].copy_from_slice(&4001u16.to_be_bytes());
            let result=manager.process(&syn).await.unwrap();assert_eq!(result.is_none(),full);
            if full{let reply=rx.recv().await.unwrap();assert_eq!(reply[53],0x14);assert_eq!(&reply[48..52],&124u32.to_be_bytes());let mut pseudo=reply[8..40].to_vec();pseudo.extend(20u32.to_be_bytes());pseudo.extend([0,0,0,6]);pseudo.extend(&reply[40..]);assert_eq!(checksum(&pseudo),0);assert!(manager.entries.is_empty());}
        }
    }
    use super::*;
    struct Ready(Arc<AtomicU64>);
    impl PacketRelay for Ready {
        fn send<'a>(&'a self,_:&'a [u8])->SendFuture<'a>{Box::pin(async move{self.0.fetch_add(1,Ordering::Relaxed);Ok(())})}
        fn receive(&self)->PacketFuture<'_>{Box::pin(std::future::pending())}
    }
    impl crate::proxy_inbound::OutboundConnector for Ready {
        fn connect(&self,_:String,_:u16)->crate::proxy_inbound::ConnectFuture{panic!("proxy should preserve Windows TCP")}
        fn raw_packet(&self,_:SocketAddr,_:SocketAddr,_:u8)->Option<OpenFuture>{let count=self.0.clone();Some(Box::pin(async move{Ok(Box::new(Ready(count)) as Box<dyn PacketRelay>)}))}
    }
    #[tokio::test]
    async fn awg_dns_selected_tcp_path_is_pinned_and_direct_stays_on_stack() {
        let count=Arc::new(AtomicU64::new(0));let names=Arc::new(crate::tun_names::NameCache::default());
        fn observe(cache:&crate::tun_names::NameCache,host:&str){
            let mut p=vec![1,2,1,0,0,1,0,0,0,0,0,0];for part in host.split('.') {p.push(part.len() as u8);p.extend(part.as_bytes());}p.extend([0,0,1,0,1]);
            let q=crate::tun_names::question(&p).unwrap();p[2]=0x81;p[3]=0x80;p[7]=1;p.extend([0xc0,0x0c,0,1,0,1,0,0,0,30,0,4,198,51,100,2]);cache.observe(&p,&q).unwrap();
        }
        observe(&names,"video.test");
        let router=crate::native_router::NativeRouter::new(Arc::new(Ready(count.clone())),vec![
            crate::ipc_contract::WireRule{kind:"DOMAIN".into(),value:"private.test".into(),target:"DIRECT".into(),no_resolve:true,source:"test".into()},
            crate::ipc_contract::WireRule{kind:"MATCH".into(),value:String::new(),target:"PROXY".into(),no_resolve:true,source:"test".into()}
        ],"1.1.1.1:53".parse().unwrap(),None).unwrap();
        let(tx,_rx)=tokio::sync::mpsc::channel(8);let mut manager=Manager::new(Arc::new(router),tx).with_names(names.clone());
        let mut syn=udp_packet("172.19.0.1:4000".parse().unwrap(),"198.51.100.2:443".parse().unwrap(),&[0;12]);
        syn[9]=6;syn[24..40].fill(0);syn[32]=0x50;syn[33]=2;syn[10..12].fill(0);let sum=checksum(&syn[..20]);syn[10..12].copy_from_slice(&sum.to_be_bytes());
        let mut pseudo=syn[12..20].to_vec();pseudo.extend([0,6,0,20]);pseudo.extend(&syn[20..]);let sum=checksum(&pseudo);syn[36..38].copy_from_slice(&sum.to_be_bytes());
        assert!(manager.process(&syn).await.unwrap().is_none());assert_eq!(manager.entries.len(),1);
        observe(&names,"private.test"); // A later DNS observation cannot reroute an established connection.
        assert!(manager.process(&syn).await.unwrap().is_none());assert_eq!(count.load(Ordering::Relaxed),2);
        let udp=udp_packet("172.19.0.1:4001".parse().unwrap(),"198.51.100.2:443".parse().unwrap(),b"new");
        assert!(manager.process(&udp).await.unwrap().is_some());assert_eq!(count.load(Ordering::Relaxed),2);
        assert!(manager.fallback_reasons.contains_key("dns_route_conflict_or_nonproxy"));
    }
    #[test]
    fn udp_translation_preserves_payload_and_checksums() {
        for (source, remote, local) in [
            ("172.19.0.1:4000", "203.0.113.1:443", "10.7.0.2:9000"),
            ("[fdfe::2]:4000", "[fd00:7::1]:443", "[fd00:7::2]:9000"),
        ] {
            let (source, remote, local) = (
                source.parse().unwrap(),
                remote.parse().unwrap(),
                local.parse().unwrap(),
            );
            let mut p = udp_packet(source, remote, &vec![9; 1200]);
            translate(&mut p, local, true).unwrap();
            assert_eq!(p, udp_packet(local, remote, &vec![9; 1200]));
            let mut p = udp_packet(remote, local, &vec![7; 1200]);
            translate_reply(&mut p, source).unwrap();
            assert_eq!(p, udp_packet(remote, source, &vec![7; 1200]));
        }
    }
    #[test]
    fn icmp_udp_quotes_restore_both_families_and_reject_foreign_or_corrupt_errors() {
        for (source, remote, local) in [
            ("172.19.0.1:4000", "203.0.113.1:443", "10.7.0.2:9000"),
            ("[fdfe::2]:4000", "[fd00:7::1]:443", "[fd00:7::2]:9000"),
        ] {
            let (source, remote, local): (SocketAddr, SocketAddr, SocketAddr) = (
                source.parse().unwrap(),
                remote.parse().unwrap(),
                local.parse().unwrap(),
            );
            let quote = crate::tun_reject::udp_quote(local, remote, &vec![42; 1200]).unwrap();
            let mut error = crate::tun_reject::udp_unreachable_quote(&quote).unwrap();
            assert_eq!(
                error_tuple(&error, local.ip()),
                Some((17, local.port(), remote))
            );
            assert!(error_tuple(&error, source.ip()).is_none());
            translate_reply(&mut error, source).unwrap();
            assert_eq!(
                error_tuple(&error, source.ip()),
                Some((17, source.port(), remote))
            );
            let n = error.len() - 1;
            error[n] ^= 1;
            assert!(error_tuple(&error, source.ip()).is_none());
        }
    }
    struct Fake;
    #[test]
    fn awg_observed_routes_preserve_order_and_conflicts() {
        let router=crate::native_router::NativeRouter::new(Arc::new(Fake), vec![
            ("IP-CIDR","203.0.113.0/24","REJECT"),
            ("DOMAIN-SUFFIX","example.ru","DIRECT"),
            ("DOMAIN","blocked.test","REJECT"),
            ("MATCH","","PROXY")
        ].into_iter().map(|(kind,value,target)|crate::ipc_contract::WireRule{kind:kind.into(),value:value.into(),target:target.into(),source:"test".into(),no_resolve:true}).collect(),"1.1.1.1:53".parse().unwrap(),None).unwrap();
        let ip="198.51.100.2".parse().unwrap();
        assert!(!router.packet_route_is_proxy(ip,&[]));
        assert!(router.packet_route_is_proxy(ip,&["video.test".into()]));
        assert!(router.packet_route_is_proxy(ip,&["video.test".into(),"images.test".into()]));
        assert!(!router.packet_route_is_proxy(ip,&["www.example.ru".into()]));
        assert!(!router.packet_route_is_proxy(ip,&["blocked.test".into()]));
        assert!(!router.packet_route_is_proxy(ip,&["video.test".into(),"www.example.ru".into()]));
        assert!(!router.packet_route_is_proxy("203.0.113.2".parse().unwrap(),&["video.test".into()]));
    }
    impl crate::proxy_inbound::OutboundConnector for Fake {
        fn connect(&self, _: String, _: u16) -> crate::proxy_inbound::ConnectFuture {
            panic!("must not terminate raw TCP")
        }
        fn raw_packet(&self, _: SocketAddr, _: SocketAddr, _: u8) -> Option<OpenFuture> {
            Some(Box::pin(async { Err(io::ErrorKind::NotConnected.into()) }))
        }
    }
    #[test]
    fn ambiguous_domain_routes_cannot_bypass_direct_or_reject() {
        for (rules, allowed) in [
            (vec![("MATCH", "", "PROXY")], true),
            (
                vec![("DOMAIN", "private.test", "DIRECT"), ("MATCH", "", "PROXY")],
                false,
            ),
            (
                vec![("DOMAIN", "private.test", "REJECT"), ("MATCH", "", "PROXY")],
                false,
            ),
            (
                vec![("DOMAIN", "cdn.test", "PROXY"), ("MATCH", "", "PROXY")],
                true,
            ),
            (
                vec![
                    ("IP-CIDR", "203.0.113.0/24", "DIRECT"),
                    ("MATCH", "", "PROXY"),
                ],
                false,
            ),
        ] {
            let router = crate::native_router::NativeRouter::new(
                Arc::new(Fake),
                rules
                    .into_iter()
                    .map(|(kind, value, target)| crate::ipc_contract::WireRule {
                        kind: kind.into(),
                        value: value.into(),
                        target: target.into(),
                        source: "test".into(),
                        no_resolve: true,
                    })
                    .collect(),
                "1.1.1.1:53".parse().unwrap(),
                None,
            )
            .unwrap();
            assert_eq!(
                router
                    .raw_packet(
                        "172.19.0.1:4000".parse().unwrap(),
                        "203.0.113.1:443".parse().unwrap(),
                        6
                    )
                    .is_ok(),
                allowed
            );
        }
    }
}
