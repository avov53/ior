//! Bounded IP fragment handling for the inner WireGuard packet channel.
use std::{
    collections::HashMap,
    io,
    time::{Duration, Instant},
};
const LIMIT: usize = 64;
const BYTES: usize = 1024 * 1024;
#[derive(Clone, Hash, Eq, PartialEq)]
struct Key {
    family: u8,
    source: Vec<u8>,
    destination: Vec<u8>,
    id: u32,
    protocol: u8,
}
struct Entry {
    header: Vec<u8>,
    pieces: Vec<(usize, Vec<u8>)>,
    end: Option<usize>,
    created: Instant,
}
#[derive(Default)]
pub struct Reassembly {
    entries: HashMap<Key, Entry>,
}
fn bad() -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, "TUNNEL_FRAGMENT_INVALID")
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
/// Normalize only headers whose semantics can be preserved by this socket stack.
/// Active routing, authentication headers and discard-required options fail closed.
fn normalize_ipv6(packet: &[u8]) -> io::Result<Vec<u8>> {
    if packet.len() < 40 || packet.len() != 40 + u16::from_be_bytes([packet[4], packet[5]]) as usize
    {
        return Err(bad());
    }
    let mut protocol = packet[6];
    let mut offset = 40;
    let mut headers = 0;
    while matches!(protocol, 0 | 43 | 60) {
        headers += 1;
        if headers > 8 {
            return Err(bad());
        }
        let header = packet.get(offset..offset + 2).ok_or_else(bad)?;
        let length = (header[1] as usize + 1) * 8;
        let data = packet.get(offset..offset + length).ok_or_else(bad)?;
        if protocol == 43 {
            if data[3] != 0 {
                return Err(bad());
            }
        } else {
            let mut option = 2;
            while option < data.len() {
                let kind = data[option];
                if kind == 0 {
                    option += 1;
                    continue;
                }
                let size = *data.get(option + 1).ok_or_else(bad)? as usize;
                if kind >> 6 != 0 || option + 2 + size > data.len() {
                    return Err(bad());
                }
                option += 2 + size;
            }
        }
        protocol = data[0];
        offset += length;
    }
    if matches!(protocol, 50 | 51) {
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "TUNNEL_IPV6_EXTENSION_UNSUPPORTED",
        ));
    }
    let mut result = packet[..40].to_vec();
    result[6] = protocol;
    result.extend_from_slice(&packet[offset..]);
    let size = (result.len() - 40) as u16;
    result[4..6].copy_from_slice(&size.to_be_bytes());
    Ok(result)
}
pub fn fragment(packet: &[u8], mtu: usize) -> io::Result<Vec<Vec<u8>>> {
    if packet.len() <= mtu {
        return Ok(vec![packet.to_vec()]);
    }
    let version = packet.first().ok_or_else(bad)? >> 4;
    let header = match version {
        4 if packet[0] & 15 == 5 => 20,
        6 => 40,
        _ => return Err(bad()),
    };
    let extra = if version == 6 { 8 } else { 0 };
    if packet.len() > 65535 || mtu <= header + extra + 8 || packet.len() < header {
        return Err(bad());
    }
    let chunk = (mtu - header - extra) & !7;
    let mut id = [0; 4];
    getrandom::getrandom(&mut id).map_err(io::Error::other)?;
    let payload = &packet[header..];
    let mut output = Vec::new();
    for (index, part) in payload.chunks(chunk).enumerate() {
        let offset = index * chunk;
        let more = offset + part.len() < payload.len();
        let mut fragment = packet[..header].to_vec();
        if version == 4 {
            fragment[2..4].copy_from_slice(&((header + part.len()) as u16).to_be_bytes());
            fragment[4..6].copy_from_slice(&id[..2]);
            fragment[6..8].copy_from_slice(
                &(((offset / 8) as u16) | if more { 0x2000 } else { 0 }).to_be_bytes(),
            );
            fragment[10..12].fill(0);
            let value = checksum(&fragment);
            fragment[10..12].copy_from_slice(&value.to_be_bytes());
        } else {
            fragment[4..6].copy_from_slice(&((part.len() + 8) as u16).to_be_bytes());
            fragment[6] = 44;
            fragment.extend([packet[6], 0]);
            fragment.extend(((offset as u16) | u16::from(more)).to_be_bytes());
            fragment.extend(id);
        }
        fragment.extend(part);
        output.push(fragment);
    }
    Ok(output)
}
impl Reassembly {
    pub fn accept(&mut self, packet: &[u8]) -> io::Result<Option<Vec<u8>>> {
        self.entries
            .retain(|_, entry| entry.created.elapsed() < Duration::from_secs(5));
        let version = packet.first().ok_or_else(bad)? >> 4;
        let normalized;
        let packet = if version == 6 {
            normalized = normalize_ipv6(packet)?;
            &normalized[..]
        } else {
            packet
        };
        let (key, offset, more, header, payload) = match version {
            4 => {
                if packet.len() < 20 || packet[0] & 15 != 5 {
                    return Err(bad());
                }
                let length = u16::from_be_bytes([packet[2], packet[3]]) as usize;
                if length != packet.len() || checksum(&packet[..20]) != 0 {
                    return Err(bad());
                }
                let flags = u16::from_be_bytes([packet[6], packet[7]]);
                if flags & 0x3fff == 0 {
                    return Ok(Some(packet.to_vec()));
                }
                (
                    Key {
                        family: 4,
                        source: packet[12..16].to_vec(),
                        destination: packet[16..20].to_vec(),
                        id: u16::from_be_bytes([packet[4], packet[5]]) as u32,
                        protocol: packet[9],
                    },
                    (flags as usize & 8191) * 8,
                    flags & 0x2000 != 0,
                    packet[..20].to_vec(),
                    &packet[20..],
                )
            }
            6 => {
                if packet.len() < 40
                    || u16::from_be_bytes([packet[4], packet[5]]) as usize + 40 != packet.len()
                {
                    return Err(bad());
                }
                if packet[6] != 44 {
                    return Ok(Some(packet.to_vec()));
                }
                if packet.len() < 48 || packet[41] != 0 {
                    return Err(bad());
                }
                let flags = u16::from_be_bytes([packet[42], packet[43]]);
                if flags & 6 != 0 {
                    return Err(bad());
                }
                let mut header = packet[..40].to_vec();
                header[6] = packet[40];
                (
                    Key {
                        family: 6,
                        source: packet[8..24].to_vec(),
                        destination: packet[24..40].to_vec(),
                        id: u32::from_be_bytes(packet[44..48].try_into().unwrap()),
                        protocol: packet[40],
                    },
                    flags as usize & !7,
                    flags & 1 != 0,
                    header,
                    &packet[48..],
                )
            }
            _ => return Err(bad()),
        };
        let end = offset.checked_add(payload.len()).ok_or_else(bad)?;
        if payload.is_empty() || end + header.len() > 65535 || more && payload.len() % 8 != 0 {
            return Err(bad());
        }
        if !self.entries.contains_key(&key) && self.entries.len() >= LIMIT {
            return Err(io::Error::new(
                io::ErrorKind::WouldBlock,
                "TUNNEL_FRAGMENT_LIMIT",
            ));
        }
        let entry = self.entries.entry(key.clone()).or_insert_with(|| Entry {
            header,
            pieces: Vec::new(),
            end: None,
            created: Instant::now(),
        });
        for (start, data) in &entry.pieces {
            if *start == offset && data == payload {
                return Ok(None);
            }
            if offset < start + data.len() && *start < end {
                return Err(bad());
            }
        }
        if entry.pieces.len() >= 128 || entry.end.is_some_and(|limit| end > limit) {
            return Err(bad());
        }
        if !more {
            if entry.end.is_some_and(|limit| limit != end) {
                return Err(bad());
            }
            entry.end = Some(end);
        }
        entry.pieces.push((offset, payload.to_vec()));
        entry.pieces.sort_by_key(|(start, _)| *start);
        let mut cursor = 0;
        for (start, data) in &entry.pieces {
            if *start != cursor {
                break;
            }
            cursor += data.len();
        }
        let complete = entry.end == Some(cursor);
        let bytes: usize = self
            .entries
            .values()
            .flat_map(|entry| entry.pieces.iter())
            .map(|(_, data)| data.len())
            .sum();
        if bytes > BYTES {
            self.entries.clear();
            return Err(io::Error::new(
                io::ErrorKind::WouldBlock,
                "TUNNEL_FRAGMENT_MEMORY_LIMIT",
            ));
        }
        if !complete {
            return Ok(None);
        }
        let entry = self.entries.remove(&key).unwrap();
        let mut result = entry.header;
        for (_, data) in entry.pieces {
            result.extend(data);
        }
        if version == 4 {
            let length = result.len() as u16;
            result[2..4].copy_from_slice(&length.to_be_bytes());
            result[6..8].fill(0);
            result[10..12].fill(0);
            let value = checksum(&result[..20]);
            result[10..12].copy_from_slice(&value.to_be_bytes());
        } else {
            let length = (result.len() - 40) as u16;
            result[4..6].copy_from_slice(&length.to_be_bytes());
        }
        Ok(Some(if version == 6 {
            normalize_ipv6(&result)?
        } else {
            result
        }))
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn ipv6_extensions_are_bounded_and_checked() {
        let mut packet = vec![0; 52];
        packet[0] = 0x60;
        packet[4..6].copy_from_slice(&12u16.to_be_bytes());
        packet[6] = 60;
        packet[40] = 17;
        packet[48..].copy_from_slice(&[1, 2, 3, 4]);
        let result = Reassembly::default().accept(&packet).unwrap().unwrap();
        assert_eq!(result[6], 17);
        assert_eq!(&result[40..], &[1, 2, 3, 4]);
        packet[42] = 0x80;
        assert!(Reassembly::default().accept(&packet).is_err());
        packet[42] = 0;
        packet[41] = 255;
        assert!(Reassembly::default().accept(&packet).is_err());
    }
    #[test]
    fn reorders_fragments_and_bounds_overlaps() {
        for family in [4, 6] {
            let header = if family == 4 { 20 } else { 40 };
            let mut packet = vec![0; header + 4096];
            packet[0] = if family == 4 { 0x45 } else { 0x60 };
            if family == 4 {
                let length = packet.len() as u16;
                packet[2..4].copy_from_slice(&length.to_be_bytes());
                packet[9] = 17;
            } else {
                packet[4..6].copy_from_slice(&4096u16.to_be_bytes());
                packet[6] = 17;
            }
            packet[header..].fill(7);
            let pieces = fragment(&packet, 1280).unwrap();
            assert!(pieces.iter().all(|piece| piece.len() <= 1280));
            let mut reassembly = Reassembly::default();
            let mut result = None;
            for piece in pieces.iter().rev() {
                if let Some(packet) = reassembly.accept(piece).unwrap() {
                    result = Some(packet);
                }
            }
            assert_eq!(&result.unwrap()[header..], &packet[header..]);
            let mut reassembly = Reassembly::default();
            assert!(reassembly.accept(&pieces[0]).unwrap().is_none());
            assert!(reassembly.accept(&pieces[0]).unwrap().is_none());
            let mut overlapping = pieces[0].clone();
            *overlapping.last_mut().unwrap() ^= 1;
            assert!(reassembly.accept(&overlapping).is_err());
        }
    }
}
