//! Bounded DNS observations and non-destructive ClientHello/HTTP name inspection.
use std::{
    collections::HashMap,
    io,
    net::{IpAddr, Ipv4Addr, Ipv6Addr},
    sync::Mutex,
    time::{Duration, Instant},
};

#[derive(Clone)]
pub struct Question {
    pub id: [u8; 2],
    pub name: String,
    pub kind: u16,
    pub end: usize,
}
fn invalid() -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, "TUN_DNS_PACKET_INVALID")
}
fn word(packet: &[u8], offset: usize) -> io::Result<u16> {
    Ok(u16::from_be_bytes(
        packet
            .get(offset..offset + 2)
            .ok_or_else(invalid)?
            .try_into()
            .unwrap(),
    ))
}
pub(crate) fn name(packet: &[u8], start: usize) -> io::Result<(String, usize)> {
    let mut labels = Vec::new();
    let mut offset = start;
    let mut end = None;
    let mut total = 0;
    for _ in 0..64 {
        let size = *packet.get(offset).ok_or_else(invalid)?;
        if size & 0xc0 == 0xc0 {
            let pointer = (word(packet, offset)? & 0x3fff) as usize;
            if pointer >= offset {
                return Err(invalid());
            }
            end.get_or_insert(offset + 2);
            offset = pointer;
            continue;
        }
        if size & 0xc0 != 0 {
            return Err(invalid());
        }
        offset += 1;
        if size == 0 {
            return Ok((labels.join("."), end.unwrap_or(offset)));
        }
        let label = packet
            .get(offset..offset + size as usize)
            .ok_or_else(invalid)?;
        if !label
            .iter()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_'))
        {
            return Err(invalid());
        }
        total += label.len() + 1;
        if total > 254 {
            return Err(invalid());
        }
        labels.push(
            String::from_utf8(label.to_vec())
                .map_err(|_| invalid())?
                .to_ascii_lowercase(),
        );
        offset += label.len();
    }
    Err(invalid())
}
pub fn question(packet: &[u8]) -> io::Result<Question> {
    if packet.len() < 12 || word(packet, 4)? != 1 || packet[2] & 0xf8 != 0 {
        return Err(invalid());
    }
    let (name, end) = name(packet, 12)?;
    if name.is_empty() || word(packet, end + 2)? != 1 {
        return Err(invalid());
    }
    Ok(Question {
        id: [packet[0], packet[1]],
        name,
        kind: word(packet, end)?,
        end: end + 4,
    })
}
pub fn matches_response(packet: &[u8], query: &Question) -> bool {
    if packet.len() < 12
        || packet[..2] != query.id
        || packet[2] & 0xf8 != 0x80
        || word(packet, 4).ok() != Some(1)
    {
        return false;
    }
    let Ok((name, end)) = name(packet, 12) else {
        return false;
    };
    name == query.name
        && word(packet, end).ok() == Some(query.kind)
        && word(packet, end + 2).ok() == Some(1)
}
pub fn empty_response(packet: &[u8], query: &Question, rcode: u8) -> Vec<u8> {
    let mut response = packet[..query.end].to_vec();
    response[2] = (packet[2] & 1) | 0x80;
    response[3] = 0x80 | (rcode & 15);
    response[6..12].fill(0);
    response
}
struct NameEntry {
    name: String,
    expires: Instant,
    observed: Instant,
    dns_flow_id: Option<u64>,
    query_type: u16,
}
pub struct NameCache {
    entries: Mutex<HashMap<IpAddr, Vec<NameEntry>>>,
}
impl Default for NameCache {
    fn default() -> Self {
        Self {
            entries: Mutex::new(HashMap::new()),
        }
    }
}
impl NameCache {
    /// Only previously observed, unexpired DNS addresses; never invent an A record.
    pub fn ipv4_candidates(&self, name: &str) -> Vec<Ipv4Addr> {
        let Ok(entries) = self.entries.lock() else {
            return Vec::new();
        };
        let now = Instant::now();
        let mut addresses = entries
            .iter()
            .filter_map(|(ip, names)| {
                let IpAddr::V4(ip) = ip else {
                    return None;
                };
                names
                    .iter()
                    .any(|entry| entry.expires > now && entry.name == name)
                    .then_some(*ip)
            })
            .collect::<Vec<_>>();
        addresses.sort_unstable();
        addresses
    }
    pub fn link_flow(&self, ip: IpAddr, known_name: Option<&str>) {
        let Ok(entries) = self.entries.lock() else {
            return;
        };
        let now = Instant::now();
        let Some(values) = entries.get(&ip) else {
            return;
        };
        let candidates = values
            .iter()
            .filter(|value| {
                value.expires > now
                    && !value.name.is_empty()
                    && known_name.is_none_or(|name| name == value.name)
            })
            .collect::<Vec<_>>();
        if candidates.len() != 1 {
            return;
        }
        let entry = candidates[0];
        if let Some(id) = entry.dns_flow_id {
            crate::trace::emit(
                serde_json::json!({"stage":"routing","code":"DNS_FLOW_LINKED","parentFlowId":id,"durationMs":now.saturating_duration_since(entry.observed).as_millis() as u64,"queryType":entry.query_type,"addressFamily":if ip.is_ipv6(){"ipv6"}else{"ipv4"}}),
            );
        }
    }
    pub fn lookup(&self, ip: IpAddr) -> Option<String> {
        self.lookup_at(ip, Instant::now())
    }
    fn lookup_at(&self, ip: IpAddr, now: Instant) -> Option<String> {
        let mut entries = self.entries.lock().ok()?;
        let names = entries.get_mut(&ip)?;
        names.retain(|entry| entry.expires > now);
        // Shared CDN addresses cannot identify a domain without a ClientHello.
        if names.len() == 1 && !names[0].name.is_empty() {
            Some(names[0].name.clone())
        } else {
            None
        }
    }
    pub fn observe(&self, packet: &[u8], query: &Question) -> io::Result<()> {
        self.observe_at(packet, query, Instant::now())
    }
    fn observe_at(&self, packet: &[u8], query: &Question, now: Instant) -> io::Result<()> {
        if !matches_response(packet, query) || packet[2] & 2 != 0 || packet[3] & 15 != 0 {
            return Err(invalid());
        }
        let (_, question_end) = name(packet, 12)?;
        let mut offset = question_end + 4;
        let answers = word(packet, 6)? as usize;
        if answers > 512 {
            return Err(invalid());
        }
        let mut aliases = Vec::<(String, String, u32)>::new();
        let mut addresses = Vec::<(String, IpAddr, u32)>::new();
        for _ in 0..answers {
            let (owner, end) = name(packet, offset)?;
            offset = end;
            let kind = word(packet, offset)?;
            let class = word(packet, offset + 2)?;
            let ttl = u32::from_be_bytes(
                packet
                    .get(offset + 4..offset + 8)
                    .ok_or_else(invalid)?
                    .try_into()
                    .unwrap(),
            );
            let size = word(packet, offset + 8)? as usize;
            offset += 10;
            let data = packet.get(offset..offset + size).ok_or_else(invalid)?;
            if class == 1 {
                match (kind, size) {
                    (1, 4) => addresses.push((
                        owner,
                        Ipv4Addr::from(<[u8; 4]>::try_from(data).unwrap()).into(),
                        ttl,
                    )),
                    (28, 16) => addresses.push((
                        owner,
                        Ipv6Addr::from(<[u8; 16]>::try_from(data).unwrap()).into(),
                        ttl,
                    )),
                    (5, _) => {
                        let (alias, end) = name(packet, offset)?;
                        if end != offset + size {
                            return Err(invalid());
                        }
                        aliases.push((owner, alias, ttl));
                    }
                    (64 | 65, _) if size >= 3 => {
                        // HTTPS/SVCB hints are also addresses a browser can use.
                        // Observe them without rewriting DNSSEC/ECH/ALPN data.
                        let priority = word(packet, offset)?;
                        let (target, mut parameter) = name(packet, offset + 2)?;
                        let end = offset + size;
                        if parameter > end {
                            return Err(invalid());
                        }
                        if priority == 0 && !target.is_empty() {
                            aliases.push((owner, target, ttl));
                        } else if priority > 0 && (target.is_empty() || target == owner) {
                            let mut previous = None;
                            while parameter < end {
                                let key = word(packet, parameter)?;
                                let length = word(packet, parameter + 2)? as usize;
                                parameter += 4;
                                if previous.is_some_and(|old| old >= key)
                                    || parameter + length > end
                                {
                                    return Err(invalid());
                                }
                                previous = Some(key);
                                let hints = &packet[parameter..parameter + length];
                                match key {
                                    4 if length > 0 && length % 4 == 0 => {
                                        for address in hints.chunks_exact(4) {
                                            addresses.push((
                                                owner.clone(),
                                                Ipv4Addr::from(
                                                    <[u8; 4]>::try_from(address).unwrap(),
                                                )
                                                .into(),
                                                ttl,
                                            ));
                                        }
                                    }
                                    6 if length > 0 && length % 16 == 0 => {
                                        for address in hints.chunks_exact(16) {
                                            addresses.push((
                                                owner.clone(),
                                                Ipv6Addr::from(
                                                    <[u8; 16]>::try_from(address).unwrap(),
                                                )
                                                .into(),
                                                ttl,
                                            ));
                                        }
                                    }
                                    4 | 6 => return Err(invalid()),
                                    _ => (),
                                }
                                parameter += length;
                            }
                        }
                    }
                    _ => (),
                }
            }
            offset += size;
        }
        let mut accepted = HashMap::<String, u32>::from([(query.name.clone(), 600)]);
        for _ in 0..16 {
            let mut changed = false;
            for (from, to, ttl) in &aliases {
                if let Some(previous) = accepted.get(from).copied() {
                    if !accepted.contains_key(to) {
                        accepted.insert(to.clone(), previous.min(*ttl));
                        changed = true;
                    }
                }
            }
            if !changed {
                break;
            }
        }
        let mut entries = self.entries.lock().map_err(|_| invalid())?;
        entries.retain(|_, values| {
            values.retain(|value| value.expires > now);
            !values.is_empty()
        });
        for (owner, ip, ttl) in addresses {
            let Some(chain_ttl) = accepted.get(&owner) else {
                continue;
            };
            let ttl = ttl.min(*chain_ttl).min(600);
            if ttl == 0 {
                continue;
            }
            if entries.len() >= 4096 && !entries.contains_key(&ip) {
                continue;
            }
            let names = entries.entry(ip).or_default();
            if let Some(entry) = names.iter_mut().find(|entry| entry.name == query.name) {
                entry.expires = now + Duration::from_secs(ttl as u64);
                entry.observed = now;
                entry.dns_flow_id = crate::trace::current().map(|flow| flow.id);
                entry.query_type = query.kind;
            } else if names.len() < 16 {
                names.push(NameEntry {
                    name: query.name.clone(),
                    expires: now + Duration::from_secs(ttl as u64),
                    observed: now,
                    dns_flow_id: crate::trace::current().map(|flow| flow.id),
                    query_type: query.kind,
                });
            } else if let Some(overflow) = names.iter_mut().find(|entry| entry.name.is_empty()) {
                overflow.expires = now + Duration::from_secs(600);
            } else {
                names.push(NameEntry {
                    name: String::new(),
                    expires: now + Duration::from_secs(600),
                    observed: now,
                    dns_flow_id: None,
                    query_type: 0,
                });
            }
        }
        Ok(())
    }
}

#[derive(Debug, PartialEq)]
pub enum Sniff {
    NeedMore,
    Name(String),
    Unknown,
}
fn hostname(bytes: &[u8]) -> Option<String> {
    if bytes.is_empty()
        || bytes.len() > 253
        || !bytes
            .iter()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'-'))
    {
        return None;
    }
    let name = std::str::from_utf8(bytes)
        .ok()?
        .trim_end_matches('.')
        .to_ascii_lowercase();
    if name
        .split('.')
        .any(|label| label.is_empty() || label.len() > 63)
        || name.parse::<IpAddr>().is_ok()
    {
        None
    } else {
        Some(name)
    }
}
pub fn sniff(bytes: &[u8]) -> Sniff {
    if bytes.is_empty() {
        return Sniff::NeedMore;
    }
    if bytes[0] == 22 {
        let mut records = Vec::new();
        let mut offset = 0;
        loop {
            if bytes.len() < offset + 5 {
                return Sniff::NeedMore;
            }
            if bytes[offset] != 22 || bytes[offset + 1] != 3 {
                return Sniff::Unknown;
            }
            let size = u16::from_be_bytes([bytes[offset + 3], bytes[offset + 4]]) as usize;
            if size > 18432 || bytes.len() < offset + 5 + size {
                return if size > 18432 {
                    Sniff::Unknown
                } else {
                    Sniff::NeedMore
                };
            }
            records.extend_from_slice(&bytes[offset + 5..offset + 5 + size]);
            offset += 5 + size;
            if records.len() >= 4 {
                if records[0] != 1 {
                    return Sniff::Unknown;
                }
                let hello_size = ((records[1] as usize) << 16)
                    | ((records[2] as usize) << 8)
                    | records[3] as usize;
                if hello_size > 32768 {
                    return Sniff::Unknown;
                }
                if records.len() >= hello_size + 4 {
                    return client_hello_name(&records[4..hello_size + 4])
                        .map(Sniff::Name)
                        .unwrap_or(Sniff::Unknown);
                }
            }
            if offset == bytes.len() {
                return Sniff::NeedMore;
            }
        }
    }
    if bytes[0].is_ascii_uppercase() {
        if let Some(end) = bytes.windows(4).position(|part| part == b"\r\n\r\n") {
            let Ok(headers) = std::str::from_utf8(&bytes[..end]) else {
                return Sniff::Unknown;
            };
            for line in headers.split("\r\n").skip(1) {
                if let Some((key, value)) = line.split_once(':') {
                    if key.eq_ignore_ascii_case("host") {
                        let host = value.trim().split(':').next().unwrap_or("");
                        return hostname(host.as_bytes())
                            .map(Sniff::Name)
                            .unwrap_or(Sniff::Unknown);
                    }
                }
            }
            return Sniff::Unknown;
        }
        return if bytes.len() < 16384 {
            Sniff::NeedMore
        } else {
            Sniff::Unknown
        };
    }
    Sniff::Unknown
}
fn client_hello_name(hello: &[u8]) -> Option<String> {
    let mut offset = 34;
    let sid = *hello.get(offset)? as usize;
    offset += 1 + sid;
    let ciphers = word(hello, offset).ok()? as usize;
    offset += 2 + ciphers;
    let compression = *hello.get(offset)? as usize;
    offset += 1 + compression;
    let extensions = word(hello, offset).ok()? as usize;
    offset += 2;
    let end = offset.checked_add(extensions)?;
    if end > hello.len() {
        return None;
    }
    while offset + 4 <= end {
        let kind = word(hello, offset).ok()?;
        let size = word(hello, offset + 2).ok()? as usize;
        offset += 4;
        let next = offset.checked_add(size)?;
        if next > end {
            return None;
        }
        if kind == 0 {
            let list = word(hello, offset).ok()? as usize;
            if list + 2 != size {
                return None;
            }
            let mut item = offset + 2;
            while item + 3 <= next {
                let name_kind = hello[item];
                let length = word(hello, item + 1).ok()? as usize;
                item += 3;
                if item + length > next {
                    return None;
                }
                if name_kind == 0 {
                    return hostname(&hello[item..item + length]);
                }
                item += length;
            }
        }
        offset = next;
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn https_hints_link_a_unique_name_without_mutating_the_dns_reply() {
        let mut query = vec![
            1, 2, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 3, b'c', b'd', b'n', 0, 0, 65, 0, 1,
        ];
        let question = question(&query).unwrap();
        query[2] = 0x81;
        query[3] = 0x80;
        query[7] = 1;
        let mut data = vec![0, 1, 0, 0, 4, 0, 4, 203, 0, 113, 7, 0, 6, 0, 16];
        data.extend("2001:db8::7".parse::<Ipv6Addr>().unwrap().octets());
        query.extend([0xc0, 0x0c, 0, 65, 0, 1, 0, 0, 0, 30]);
        query.extend((data.len() as u16).to_be_bytes());
        query.extend(data);
        let original = query.clone();
        let cache = NameCache::default();
        cache.observe(&query, &question).unwrap();
        assert_eq!(query, original);
        assert_eq!(
            cache.lookup("2001:db8::7".parse().unwrap()),
            Some("cdn".into())
        );
        assert_eq!(
            cache
                .entries
                .lock()
                .unwrap()
                .get(&"2001:db8::7".parse().unwrap())
                .unwrap()[0]
                .query_type,
            65
        );
        assert_eq!(
            cache.lookup("203.0.113.7".parse().unwrap()),
            Some("cdn".into())
        );
        assert_eq!(
            cache.ipv4_candidates("cdn"),
            vec!["203.0.113.7".parse::<Ipv4Addr>().unwrap()]
        );
        assert!(cache.ipv4_candidates("unknown").is_empty());
        let (other, reply) = dns("other.example", [203, 0, 113, 7], 30);
        cache.observe(&reply, &other).unwrap();
        assert!(cache.lookup("203.0.113.7".parse().unwrap()).is_none());
    }
    fn dns(host: &str, ip: [u8; 4], ttl: u32) -> (Question, Vec<u8>) {
        let mut packet = vec![1, 2, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0];
        for label in host.split('.') {
            packet.push(label.len() as u8);
            packet.extend(label.as_bytes());
        }
        packet.extend([0, 0, 1, 0, 1]);
        let q = question(&packet).unwrap();
        packet[2] = 0x81;
        packet[3] = 0x80;
        packet[7] = 1;
        packet.extend([0xc0, 0x0c, 0, 1, 0, 1]);
        packet.extend(ttl.to_be_bytes());
        packet.extend([0, 4]);
        packet.extend(ip);
        (q, packet)
    }
    #[test]
    fn names_expire_and_shared_addresses_are_ambiguous() {
        let cache = NameCache::default();
        let now = Instant::now();
        let ip = "203.0.113.7".parse().unwrap();
        let (q, p) = dns("one.example", [203, 0, 113, 7], 10);
        cache.observe_at(&p, &q, now).unwrap();
        assert_eq!(
            cache.ipv4_candidates("one.example"),
            vec!["203.0.113.7".parse::<Ipv4Addr>().unwrap()]
        );
        assert_eq!(cache.lookup_at(ip, now), Some("one.example".into()));
        let (q, p) = dns("two.example", [203, 0, 113, 7], 1);
        cache.observe_at(&p, &q, now).unwrap();
        assert_eq!(cache.lookup_at(ip, now), None);
        assert_eq!(
            cache.lookup_at(ip, now + Duration::from_secs(2)),
            Some("one.example".into())
        );
        assert_eq!(cache.lookup_at(ip, now + Duration::from_secs(11)), None);
    }
    #[test]
    fn dns_observation_rejects_wrong_identity_and_pointer_loops() {
        let (q, mut p) = dns("one.example", [203, 0, 113, 7], 10);
        p[0] ^= 1;
        assert!(NameCache::default().observe(&p, &q).is_err());
        let mut malformed = vec![0; 18];
        malformed[5] = 1;
        malformed[12] = 0xc0;
        malformed[13] = 12;
        assert!(question(&malformed).is_err());
    }
    #[test]
    fn fragmented_client_hello_and_http_names_are_inspected() {
        let host = b"cdn.example";
        let mut ext = vec![0, 0, 0, 0];
        let size = host.len() + 5;
        ext[2..4].copy_from_slice(&(size as u16).to_be_bytes());
        ext.extend(((host.len() + 3) as u16).to_be_bytes());
        ext.push(0);
        ext.extend((host.len() as u16).to_be_bytes());
        ext.extend(host);
        let mut hello = vec![3, 3];
        hello.extend([0; 32]);
        hello.extend([0, 0, 2, 0x13, 1, 1, 0]);
        hello.extend((ext.len() as u16).to_be_bytes());
        hello.extend(ext);
        let mut handshake = vec![1, 0, 0, hello.len() as u8];
        handshake.extend(hello);
        let cut = 22;
        let mut tls = vec![22, 3, 3, 0, cut as u8];
        tls.extend(&handshake[..cut]);
        tls.extend([22, 3, 3, 0, (handshake.len() - cut) as u8]);
        tls.extend(&handshake[cut..]);
        assert_eq!(sniff(&tls[..8]), Sniff::NeedMore);
        assert_eq!(sniff(&tls), Sniff::Name("cdn.example".into()));
        assert_eq!(
            sniff(b"GET / HTTP/1.1\r\nHost: CDN.Example:80\r\n\r\n"),
            Sniff::Name("cdn.example".into())
        );
        assert_eq!(sniff(b"SSH-2.0-server\r\n"), Sniff::NeedMore);
        assert_eq!(sniff(&[22, 3, 3, 0xff, 0xff]), Sniff::Unknown);
    }
}
