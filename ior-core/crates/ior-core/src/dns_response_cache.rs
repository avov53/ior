//! Positive DNS replies only; preserve route, query options, transaction ID and TTL.
use crate::tun_names::{matches_response, name, question};
use std::{
    collections::HashMap,
    sync::Mutex,
    time::{Duration, Instant},
};

const MAX_ENTRIES: usize = 256;
const MAX_BYTES: usize = 512 * 1024;

#[derive(Hash, Eq, PartialEq)]
struct Key {
    query: Vec<u8>,
    target: String,
    tcp: bool,
}
struct Entry {
    response: Vec<u8>,
    ttls: Vec<(usize, u32)>,
    created: Instant,
    expires: Instant,
}
#[derive(Default)]
pub struct DnsResponseCache {
    entries: Mutex<HashMap<Key, Entry>>,
}

fn key(packet: &[u8], target: &str, tcp: bool) -> Option<Key> {
    if !(12..=4096).contains(&packet.len()) {
        return None;
    }
    Some(Key {
        query: packet[2..].to_vec(),
        target: target.into(),
        tcp,
    })
}
fn word(packet: &[u8], offset: usize) -> Option<u16> {
    Some(u16::from_be_bytes(
        packet.get(offset..offset + 2)?.try_into().ok()?,
    ))
}
fn ttl_fields(response: &[u8]) -> Option<(Vec<(usize, u32)>, u32)> {
    if response.len() > 8192
        || response.len() < 12
        || response[2] & 2 != 0
        || response[3] & 15 != 0
        || word(response, 6)? == 0
    {
        return None;
    }
    let mut offset = name(response, 12).ok()?.1 + 4;
    let count =
        word(response, 6)? as usize + word(response, 8)? as usize + word(response, 10)? as usize;
    if count > 512 {
        return None;
    }
    let mut fields = Vec::new();
    let mut lifetime = 300;
    for _ in 0..count {
        offset = name(response, offset).ok()?.1;
        let kind = word(response, offset)?;
        let ttl = u32::from_be_bytes(response.get(offset + 4..offset + 8)?.try_into().ok()?);
        let size = word(response, offset + 8)? as usize;
        response.get(offset + 10..offset + 10 + size)?;
        // OPT uses these bytes for EDNS flags, not a TTL.
        if kind != 41 {
            if ttl == 0 {
                return None;
            }
            lifetime = lifetime.min(ttl);
            fields.push((offset + 4, ttl));
        }
        offset += 10 + size;
    }
    if fields.is_empty() || offset != response.len() {
        return None;
    }
    Some((fields, lifetime))
}
impl DnsResponseCache {
    pub fn get(&self, query: &[u8], target: &str, tcp: bool) -> Option<Vec<u8>> {
        self.get_at(query, target, tcp, Instant::now())
    }
    fn get_at(&self, query: &[u8], target: &str, tcp: bool, now: Instant) -> Option<Vec<u8>> {
        let key = key(query, target, tcp)?;
        let mut entries = self.entries.lock().ok()?;
        entries.retain(|_, entry| entry.expires > now);
        let entry = entries.get(&key)?;
        let mut response = entry.response.clone();
        response[..2].copy_from_slice(&query[..2]);
        let elapsed = now
            .saturating_duration_since(entry.created)
            .as_secs()
            .min(u32::MAX as u64) as u32;
        for &(position, ttl) in &entry.ttls {
            response[position..position + 4]
                .copy_from_slice(&ttl.saturating_sub(elapsed).to_be_bytes());
        }
        Some(response)
    }
    pub fn insert(&self, query: &[u8], target: &str, tcp: bool, response: &[u8]) {
        self.insert_at(query, target, tcp, response, Instant::now());
    }
    fn insert_at(&self, query: &[u8], target: &str, tcp: bool, response: &[u8], now: Instant) {
        let Some(key) = key(query, target, tcp) else {
            return;
        };
        let Ok(question) = question(query) else {
            return;
        };
        if !matches_response(response, &question) {
            return;
        }
        let Some((ttls, lifetime)) = ttl_fields(response) else {
            return;
        };
        let Ok(mut entries) = self.entries.lock() else {
            return;
        };
        entries.retain(|_, entry| entry.expires > now);
        entries.insert(
            key,
            Entry {
                response: response.to_vec(),
                ttls,
                created: now,
                expires: now + Duration::from_secs(lifetime as u64),
            },
        );
        while entries.len() > MAX_ENTRIES
            || entries
                .iter()
                .map(|(key, entry)| key.query.len() + key.target.len() + entry.response.len())
                .sum::<usize>()
                > MAX_BYTES
        {
            let oldest = entries
                .iter()
                .min_by_key(|(_, entry)| entry.expires)
                .map(|(key, _)| Key {
                    query: key.query.clone(),
                    target: key.target.clone(),
                    tcp: key.tcp,
                });
            if let Some(key) = oldest {
                entries.remove(&key);
            } else {
                break;
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn packets(ttl: u32) -> (Vec<u8>, Vec<u8>) {
        let query = vec![
            1, 2, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 3, b'c', b'd', b'n', 0, 0, 1, 0, 1,
        ];
        let mut reply = query.clone();
        reply[2] = 0x81;
        reply[3] = 0x80;
        reply[7] = 1;
        reply.extend([0xc0, 0x0c, 0, 1, 0, 1]);
        reply.extend(ttl.to_be_bytes());
        reply.extend([0, 4, 203, 0, 113, 7]);
        (query, reply)
    }
    #[test]
    fn cache_preserves_route_and_options_changes_id_ages_ttl_and_expires() {
        let cache = DnsResponseCache::default();
        let now = Instant::now();
        let (mut query, reply) = packets(10);
        cache.insert_at(&query, "PROXY", false, &reply, now);
        query[..2].copy_from_slice(&[7, 9]);
        let hit = cache
            .get_at(&query, "PROXY", false, now + Duration::from_secs(3))
            .unwrap();
        assert_eq!(&hit[..2], &[7, 9]);
        assert_eq!(u32::from_be_bytes(hit[27..31].try_into().unwrap()), 7);
        assert!(cache.get_at(&query, "DIRECT", false, now).is_none());
        assert!(cache.get_at(&query, "PROXY", true, now).is_none());
        let mut changed = query.clone();
        changed[3] |= 0x10;
        assert!(cache.get_at(&changed, "PROXY", false, now).is_none());
        assert!(cache
            .get_at(&query, "PROXY", false, now + Duration::from_secs(10))
            .is_none());
    }
    #[test]
    fn invalid_truncated_error_and_zero_ttl_replies_are_not_cached() {
        let cache = DnsResponseCache::default();
        let (query, reply) = packets(10);
        for variant in 0..4 {
            let mut bad = reply.clone();
            match variant {
                0 => bad[2] |= 2,
                1 => bad[3] |= 2,
                2 => bad[0] ^= 1,
                _ => bad.truncate(25),
            }
            cache.insert(&query, "PROXY", false, &bad);
            assert!(cache.get(&query, "PROXY", false).is_none());
        }
        let (query, reply) = packets(0);
        cache.insert(&query, "PROXY", false, &reply);
        assert!(cache.get(&query, "PROXY", false).is_none());
    }
}
