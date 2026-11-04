//! Explicit, bounded UDP network rejection for unsupported destinations.
use std::net::{IpAddr, SocketAddr};
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
fn header(source: IpAddr, destination: IpAddr, protocol: u8, length: usize) -> Option<Vec<u8>> {
    match (source, destination) {
        (IpAddr::V4(source), IpAddr::V4(destination)) => {
            let mut packet = vec![0; 20];
            packet[0] = 0x45;
            packet[2..4].copy_from_slice(&((length + 20) as u16).to_be_bytes());
            packet[8] = 64;
            packet[9] = protocol;
            packet[12..16].copy_from_slice(&source.octets());
            packet[16..20].copy_from_slice(&destination.octets());
            let sum = checksum(&packet);
            packet[10..12].copy_from_slice(&sum.to_be_bytes());
            Some(packet)
        }
        (IpAddr::V6(source), IpAddr::V6(destination)) => {
            let mut packet = vec![0; 40];
            packet[0] = 0x60;
            packet[4..6].copy_from_slice(&(length as u16).to_be_bytes());
            packet[6] = protocol;
            packet[7] = 64;
            packet[8..24].copy_from_slice(&source.octets());
            packet[24..40].copy_from_slice(&destination.octets());
            Some(packet)
        }
        _ => None,
    }
}
pub fn udp_unreachable(
    source: SocketAddr,
    destination: SocketAddr,
    data: &[u8],
) -> Option<Vec<u8>> {
    udp_unreachable_quote(&udp_quote(source, destination, data)?)
}
/// Refuse a new unsupported TCP SYN before creating any TCP stack state.
pub fn tcp_syn_reset(packet:&[u8])->Option<Vec<u8>> {
    let tuple=crate::awg_packets::tuple(packet)?;
    if tuple.protocol!=6 || tuple.flags&0x17!=2{return None;}
    let offset=if tuple.source.is_ipv6(){40}else{(packet[0]&15) as usize*4};
    let seq=u32::from_be_bytes(packet.get(offset+4..offset+8)?.try_into().ok()?);
    let mut reply=header(tuple.destination.ip(),tuple.source.ip(),6,20)?;
    let mut tcp=vec![0;20];tcp[..2].copy_from_slice(&tuple.destination.port().to_be_bytes());tcp[2..4].copy_from_slice(&tuple.source.port().to_be_bytes());
    tcp[8..12].copy_from_slice(&seq.wrapping_add(1).to_be_bytes());tcp[12]=0x50;tcp[13]=0x14;
    let mut pseudo=if tuple.source.is_ipv6(){let mut p=reply[8..40].to_vec();p.extend(20u32.to_be_bytes());p.extend([0,0,0,6]);p}else{let mut p=reply[12..20].to_vec();p.extend([0,6]);p.extend(20u16.to_be_bytes());p};
    pseudo.extend(&tcp);tcp[16..18].copy_from_slice(&checksum(&pseudo).to_be_bytes());reply.extend(tcp);Some(reply)
}
/// Keep the original datagram length and checksum in the ICMP quotation.
/// Windows uses them to associate a rejection with the outstanding UDP flow.
pub fn udp_quote(source: SocketAddr, destination: SocketAddr, data: &[u8]) -> Option<Vec<u8>> {
    let length = data.len().checked_add(8)?;
    if length > 65535 || (source.is_ipv4() && length > 65515) {
        return None;
    }
    let mut quoted = header(source.ip(), destination.ip(), 17, length)?;
    let mut datagram = Vec::with_capacity(length);
    datagram.extend(source.port().to_be_bytes());
    datagram.extend(destination.port().to_be_bytes());
    datagram.extend((length as u16).to_be_bytes());
    datagram.extend([0, 0]);
    datagram.extend(data);
    let mut pseudo = if source.is_ipv6() {
        let mut p = quoted[8..40].to_vec();
        p.extend((length as u32).to_be_bytes());
        p.extend([0, 0, 0, 17]);
        p
    } else {
        let mut p = quoted[12..20].to_vec();
        p.extend([0, 17]);
        p.extend((length as u16).to_be_bytes());
        p
    };
    pseudo.extend(&datagram);
    let sum = checksum(&pseudo);
    datagram[6..8].copy_from_slice(&(if sum == 0 { 65535 } else { sum }).to_be_bytes());
    quoted.extend(&datagram[..datagram.len().min(16)]);
    Some(quoted)
}
pub fn udp_unreachable_quote(quoted: &[u8]) -> Option<Vec<u8>> {
    let (source, destination) = match quoted.first()? >> 4 {
        4 if quoted.len() >= 28 && quoted[0] == 0x45 && quoted[9] == 17 => (
            IpAddr::from(<[u8; 4]>::try_from(&quoted[12..16]).ok()?),
            IpAddr::from(<[u8; 4]>::try_from(&quoted[16..20]).ok()?),
        ),
        6 if quoted.len() >= 48 && quoted[6] == 17 => (
            IpAddr::from(<[u8; 16]>::try_from(&quoted[8..24]).ok()?),
            IpAddr::from(<[u8; 16]>::try_from(&quoted[24..40]).ok()?),
        ),
        _ => return None,
    };
    let mut message = vec![0; 8];
    message[0] = if source.is_ipv6() { 1 } else { 3 };
    message[1] = 1;
    message.extend(quoted);
    let mut reply = header(
        destination,
        source,
        if source.is_ipv6() { 58 } else { 1 },
        message.len(),
    )?;
    let sum = if source.is_ipv6() {
        let mut pseudo = reply[8..40].to_vec();
        pseudo.extend((message.len() as u32).to_be_bytes());
        pseudo.extend([0, 0, 0, 58]);
        pseudo.extend(&message);
        checksum(&pseudo)
    } else {
        checksum(&message)
    };
    message[2..4].copy_from_slice(&sum.to_be_bytes());
    reply.extend(message);
    Some(reply)
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn valid_icmp_checksums_and_reverse_endpoints() {
        let packet = udp_unreachable(
            "192.0.2.1:3000".parse().unwrap(),
            "198.51.100.1:443".parse().unwrap(),
            b"test",
        )
        .unwrap();
        assert_eq!(checksum(&packet[..20]), 0);
        assert_eq!(checksum(&packet[20..]), 0);
        assert_eq!(&packet[12..16], &[198, 51, 100, 1]);
        let packet = udp_unreachable(
            "[fd00::2]:3000".parse().unwrap(),
            "[fd00::1]:443".parse().unwrap(),
            b"test",
        )
        .unwrap();
        let mut pseudo = packet[8..40].to_vec();
        pseudo.extend(((packet.len() - 40) as u32).to_be_bytes());
        pseudo.extend([0, 0, 0, 58]);
        pseudo.extend(&packet[40..]);
        assert_eq!(checksum(&pseudo), 0);
        assert_eq!(&packet[40..42], &[1, 1]);
    }
    #[test]
    fn quote_keeps_original_udp_length_checksum_and_bounded_payload() {
        for (source, destination) in [
            ("192.0.2.1:3000", "198.51.100.1:443"),
            ("[fd00::2]:3000", "[fd00::1]:443"),
        ] {
            let source: SocketAddr = source.parse().unwrap();
            let destination: SocketAddr = destination.parse().unwrap();
            let data = vec![7; 1200];
            let quote = udp_quote(source, destination, &data).unwrap();
            let offset = if source.is_ipv6() { 40 } else { 20 };
            assert_eq!(quote.len(), offset + 16);
            assert_eq!(
                u16::from_be_bytes(quote[offset + 4..offset + 6].try_into().unwrap()),
                1208
            );
            assert_ne!(&quote[offset + 6..offset + 8], &[0, 0]);
            let mut pseudo = if source.is_ipv6() {
                let mut p = quote[8..40].to_vec();
                p.extend(1208u32.to_be_bytes());
                p.extend([0, 0, 0, 17]);
                p
            } else {
                let mut p = quote[12..20].to_vec();
                p.extend([0, 17]);
                p.extend(1208u16.to_be_bytes());
                p
            };
            pseudo.extend(&quote[offset..offset + 8]);
            pseudo.extend(&data);
            assert_eq!(checksum(&pseudo), 0);
            assert!(udp_unreachable_quote(&quote).is_some());
        }
    }
}
