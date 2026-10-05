//! Authenticated inner ICMP still has to quote a live, owned outbound tuple.
use std::{
    collections::HashMap,
    net::{IpAddr, SocketAddr},
    sync::Mutex,
    time::{Duration, Instant},
};
#[derive(Default)]
pub struct Cache(Mutex<HashMap<SocketAddr, (usize, Instant)>>);
fn valid_checksum(bytes: &[u8], mut sum: u32) -> bool {
    for pair in bytes.chunks(2) {
        sum += u16::from_be_bytes([pair[0], *pair.get(1).unwrap_or(&0)]) as u32;
    }
    while sum > 65535 {
        sum = (sum & 65535) + (sum >> 16);
    }
    sum == 65535
}
pub fn message(packet: &[u8], local: IpAddr) -> Option<(u8, u16, SocketAddr, usize)> {
    let ipv6 = packet.first()? >> 4 == 6;
    let payload = if ipv6 {
        if packet.len() < 48
            || packet[6] != 58
            || packet.len() != 40 + u16::from_be_bytes([packet[4], packet[5]]) as usize
            || &packet[24..40]
                != local
                    .to_string()
                    .parse::<std::net::Ipv6Addr>()
                    .ok()?
                    .octets()
                    .as_slice()
        {
            return None;
        }
        let mut pseudo = packet[8..40].to_vec();
        pseudo.extend(((packet.len() - 40) as u32).to_be_bytes());
        pseudo.extend([0, 0, 0, 58]);
        pseudo.extend(&packet[40..]);
        if !valid_checksum(&pseudo, 0) {
            return None;
        }
        &packet[40..]
    } else {
        if packet.len() < 28 || packet[9] != 1 {
            return None;
        }
        let offset = (packet[0] & 15) as usize * 4;
        if offset < 20
            || packet.len() < offset + 8
            || packet.len() != u16::from_be_bytes([packet[2], packet[3]]) as usize
            || &packet[16..20]
                != local
                    .to_string()
                    .parse::<std::net::Ipv4Addr>()
                    .ok()?
                    .octets()
                    .as_slice()
            || !valid_checksum(&packet[..offset], 0)
            || !valid_checksum(&packet[offset..], 0)
        {
            return None;
        }
        &packet[offset..]
    };
    quoted_flow(payload, ipv6, local)
}
/// IPv4 ICMP errors for packet-preserving TCP, including unreachable/TTL.
pub fn error_flow(packet: &[u8], local: std::net::Ipv4Addr) -> Option<(u8, u16, SocketAddr)> {
    if packet.len() < 56
        || packet[0] >> 4 != 4
        || packet[9] != 1
        || packet.len() != u16::from_be_bytes([packet[2], packet[3]]) as usize
        || packet[16..20] != local.octets()
    {
        return None;
    }
    let h = (packet[0] & 15) as usize * 4;
    if h < 20
        || packet.len() < h + 8 + 28
        || !valid_checksum(&packet[..h], 0)
        || !valid_checksum(&packet[h..], 0)
        || !matches!(packet[h], 3 | 11 | 12)
    {
        return None;
    }
    let quote = &packet[h + 8..];
    let qh = (quote[0] & 15) as usize * 4;
    if quote[0] >> 4 != 4
        || quote[9] != 6
        || qh < 20
        || quote.len() < qh + 8
        || quote[12..16] != local.octets()
        || u16::from_be_bytes([quote[6], quote[7]]) & 0x1fff != 0
    {
        return None;
    }
    let port = u16::from_be_bytes([quote[qh], quote[qh + 1]]);
    let destination = std::net::Ipv4Addr::new(quote[16], quote[17], quote[18], quote[19]);
    Some((
        6,
        port,
        SocketAddr::new(
            destination.into(),
            u16::from_be_bytes([quote[qh + 2], quote[qh + 3]]),
        ),
    ))
}
impl Cache {
    pub fn get(&self, remote: SocketAddr, initial: usize) -> usize {
        let Ok(mut entries) = self.0.lock() else {
            return initial;
        };
        entries.retain(|_, (_, time)| time.elapsed() < Duration::from_secs(600));
        entries
            .get(&remote)
            .map_or(initial, |(mtu, _)| initial.min(*mtu))
    }
    pub fn update(&self, remote: SocketAddr, mtu: usize, initial: usize) -> bool {
        let floor = if remote.is_ipv6() { 1280 } else { 576 };
        if mtu < floor || mtu >= self.get(remote, initial) {
            return false;
        }
        let Ok(mut entries) = self.0.lock() else {
            return false;
        };
        if entries.len() >= 256 {
            if let Some(key) = entries
                .iter()
                .min_by_key(|(_, (_, time))| *time)
                .map(|(key, _)| *key)
            {
                entries.remove(&key);
            }
        }
        entries.insert(remote, (mtu, Instant::now()));
        true
    }
}
pub fn quoted_flow(
    payload: &[u8],
    ipv6: bool,
    local: IpAddr,
) -> Option<(u8, u16, SocketAddr, usize)> {
    if payload.len() < 8 {
        return None;
    }
    let mtu = if ipv6 {
        if payload[..2] != [2, 0] {
            return None;
        }
        u32::from_be_bytes(payload[4..8].try_into().ok()?) as usize
    } else {
        if payload[..2] != [3, 4] {
            return None;
        }
        u16::from_be_bytes(payload[6..8].try_into().ok()?) as usize
    };
    let quoted = &payload[8..];
    let (source, destination, protocol, offset) = match quoted.first()? >> 4 {
        4 => {
            if quoted.len() < 28 {
                return None;
            }
            let offset = (quoted[0] & 15) as usize * 4;
            if offset < 20 || quoted.len() < offset + 8 || quoted[6] & 0x1f != 0 || quoted[7] != 0 {
                return None;
            }
            (
                IpAddr::from(<[u8; 4]>::try_from(&quoted[12..16]).ok()?),
                IpAddr::from(<[u8; 4]>::try_from(&quoted[16..20]).ok()?),
                quoted[9],
                offset,
            )
        }
        6 => {
            if quoted.len() < 48 {
                return None;
            }
            (
                IpAddr::from(<[u8; 16]>::try_from(&quoted[8..24]).ok()?),
                IpAddr::from(<[u8; 16]>::try_from(&quoted[24..40]).ok()?),
                quoted[6],
                40,
            )
        }
        _ => return None,
    };
    if source != local || destination.is_ipv6() != ipv6 || !matches!(protocol, 6 | 17) {
        return None;
    }
    let port = u16::from_be_bytes(quoted[offset..offset + 2].try_into().ok()?);
    let remote = SocketAddr::new(
        destination,
        u16::from_be_bytes(quoted[offset + 2..offset + 4].try_into().ok()?),
    );
    Some((protocol, port, remote, mtu))
}
#[cfg(test)]
mod tests {
    use super::*;
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
    #[test]
    fn packet_too_big_requires_valid_checksum_and_local_destination() {
        let local = "fd00::2".parse().unwrap();
        let mut packet = vec![0u8; 96];
        packet[0] = 0x60;
        packet[4..6].copy_from_slice(&56u16.to_be_bytes());
        packet[6] = 58;
        packet[8..24].copy_from_slice(&"fd00::1".parse::<std::net::Ipv6Addr>().unwrap().octets());
        packet[24..40].copy_from_slice(&"fd00::2".parse::<std::net::Ipv6Addr>().unwrap().octets());
        packet[40] = 2;
        packet[44..48].copy_from_slice(&1280u32.to_be_bytes());
        packet[48] = 0x60;
        packet[54] = 17;
        packet[56..72].copy_from_slice(&"fd00::2".parse::<std::net::Ipv6Addr>().unwrap().octets());
        packet[72..88].copy_from_slice(&"fd00::3".parse::<std::net::Ipv6Addr>().unwrap().octets());
        packet[88..92].copy_from_slice(&[0x75, 0x30, 1, 0xbb]);
        let mut pseudo = packet[8..40].to_vec();
        pseudo.extend(56u32.to_be_bytes());
        pseudo.extend([0, 0, 0, 58]);
        pseudo.extend(&packet[40..]);
        packet[42..44].copy_from_slice(&checksum(&pseudo).to_be_bytes());
        assert_eq!(
            message(&packet, local).unwrap(),
            (17, 30000, "[fd00::3]:443".parse().unwrap(), 1280)
        );
        assert!(message(&packet, "fd00::4".parse().unwrap()).is_none());
        packet[95] ^= 1;
        assert!(message(&packet, local).is_none());
    }
    #[test]
    fn quotes_require_own_source_and_bounds() {
        let mut icmp = vec![0; 36];
        icmp[0] = 3;
        icmp[1] = 4;
        icmp[6..8].copy_from_slice(&1200u16.to_be_bytes());
        icmp[8] = 0x45;
        icmp[17] = 6;
        icmp[20..24].copy_from_slice(&[10, 0, 0, 2]);
        icmp[24..28].copy_from_slice(&[8, 8, 8, 8]);
        icmp[28..32].copy_from_slice(&[0x75, 0x30, 1, 0xbb]);
        let local = "10.0.0.2".parse().unwrap();
        assert_eq!(quoted_flow(&icmp, false, local).unwrap().1, 30000);
        assert!(quoted_flow(&icmp, false, "10.0.0.3".parse().unwrap()).is_none());
        assert!(quoted_flow(&icmp[..20], false, local).is_none());
        let cache = Cache::default();
        let remote = "8.8.8.8:443".parse().unwrap();
        assert!(cache.update(remote, 1200, 1280));
        assert!(!cache.update(remote, 1300, 1280));
        assert_eq!(cache.get(remote, 1280), 1200);
    }
}
