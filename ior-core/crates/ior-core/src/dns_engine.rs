use crate::core_api::DnsProfile;
use std::{
    io,
    net::{IpAddr, Ipv4Addr, SocketAddr},
    time::Duration,
};
use tokio::{net::UdpSocket, time::timeout};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BootstrapPlan {
    pub resolver: IpAddr,
    pub direct: bool,
    pub ipv4_only: bool,
}

pub fn bootstrap_plan(profile: &DnsProfile) -> Result<BootstrapPlan, String> {
    let resolver = profile
        .bootstrap
        .parse::<IpAddr>()
        .map_err(|_| "Bootstrap DNS must be an IP address".to_string())?;
    Ok(BootstrapPlan {
        resolver,
        direct: true,
        ipv4_only: profile.ipv4_only,
    })
}

/// Resolve a server name before starting the proxy. This direct UDP query is
/// intended for Proxy mode; TUN additionally needs a physical-interface bind.
pub async fn resolve_server_ipv4(host: &str, resolver: SocketAddr) -> io::Result<Ipv4Addr> {
    resolve_server_ipv4_bound(host, resolver, &None).await
}
pub async fn resolve_server_ipv4_bound(
    host: &str,
    resolver: SocketAddr,
    network: &Option<crate::ipc_contract::WireNetwork>,
) -> io::Result<Ipv4Addr> {
    if let Ok(address) = host.parse::<Ipv4Addr>() {
        return Ok(address);
    }
    if host.parse::<IpAddr>().is_ok() {
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "IPv6 bootstrap is not enabled",
        ));
    }
    let mut id = [0u8; 2];
    getrandom::getrandom(&mut id)
        .map_err(|_| io::Error::new(io::ErrorKind::Other, "DNS query ID unavailable"))?;
    let question = encode_ipv4_question(host, id)?;
    let socket = if network.is_some() {
        if !resolver.is_ipv4() {
            return Err(io::Error::new(
                io::ErrorKind::Unsupported,
                "BOOTSTRAP_IPV6_BIND_UNAVAILABLE",
            ));
        }
        UdpSocket::from_std(crate::native_router::bound_socket(true, network)?.into())?
    } else {
        UdpSocket::bind(if resolver.is_ipv4() {
            "0.0.0.0:0"
        } else {
            "[::]:0"
        })
        .await?
    };
    socket.connect(resolver).await?;
    let _guard = crate::egress_registry::register(socket.local_addr()?, resolver, true);
    socket.send(&question).await?;
    let mut response = [0u8; 1500];
    let length = timeout(Duration::from_secs(4), socket.recv(&mut response))
        .await
        .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "bootstrap DNS timed out"))??;
    decode_ipv4_answer(&response[..length], id)
}

pub async fn resolve_server_ip_bound(
    host: &str,
    resolver: SocketAddr,
    network: &Option<crate::ipc_contract::WireNetwork>,
) -> io::Result<IpAddr> {
    if let Ok(ip) = host.parse() {
        return Ok(ip);
    }
    let a = async {
        if network
            .as_ref()
            .is_some_and(|network| network.physical_address.is_empty())
        {
            return Err(io::Error::new(
                io::ErrorKind::AddrNotAvailable,
                "PHYSICAL_IPV4_ADDRESS_MISSING",
            ));
        }
        resolve_addresses(host, resolver, network, 1)
            .await?
            .into_iter()
            .next()
            .ok_or_else(|| io::ErrorKind::NotFound.into())
    };
    let aaaa = async {
        tokio::time::sleep(Duration::from_millis(250)).await;
        if network
            .as_ref()
            .is_some_and(|network| network.physical_address6.is_none())
        {
            return Err(io::Error::new(
                io::ErrorKind::AddrNotAvailable,
                "PHYSICAL_IPV6_ADDRESS_MISSING",
            ));
        }
        resolve_addresses(host, resolver, network, 28)
            .await?
            .into_iter()
            .next()
            .ok_or_else(|| io::ErrorKind::NotFound.into())
    };
    tokio::pin!(a, aaaa);
    tokio::select! {result=&mut a=>match result{Ok(ip)=>Ok(ip),Err(_)=>aaaa.await},result=&mut aaaa=>match result{Ok(ip)=>Ok(ip),Err(_)=>a.await}}
}
pub async fn resolve_addresses(
    host: &str,
    resolver: SocketAddr,
    network: &Option<crate::ipc_contract::WireNetwork>,
    kind: u16,
) -> io::Result<Vec<IpAddr>> {
    let mut id = [0; 2];
    getrandom::getrandom(&mut id).map_err(io::Error::other)?;
    let query = encode_address_question(host, id, kind)?;
    let socket = UdpSocket::from_std(
        crate::native_router::bound_socket_family(true, network, resolver.is_ipv6())?.into(),
    )?;
    socket.connect(resolver).await?;
    let _guard = crate::egress_registry::register(socket.local_addr()?, resolver, true);
    socket.send(&query).await?;
    let mut response = vec![0; 65535];
    let length = timeout(Duration::from_secs(4), socket.recv(&mut response))
        .await
        .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "BOOTSTRAP_DNS_TIMEOUT"))??;
    decode_address_answers(&response[..length], &query)
}
pub(crate) fn encode_address_question(host: &str, id: [u8; 2], kind: u16) -> io::Result<Vec<u8>> {
    let mut query = encode_ipv4_question(host, id)?;
    let length = query.len();
    query[length - 4..length - 2].copy_from_slice(&kind.to_be_bytes());
    Ok(query)
}
pub(crate) fn decode_address_answers(response: &[u8], query: &[u8]) -> io::Result<Vec<IpAddr>> {
    let question = crate::tun_names::question(query)?;
    if !crate::tun_names::matches_response(response, &question)
        || response[3] & 15 != 0
        || response[2] & 2 != 0
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "DNS_ADDRESS_RESPONSE_INVALID",
        ));
    }
    let mut offset = crate::tun_names::name(response, 12)?.1 + 4;
    let mut result = Vec::new();
    for _ in 0..u16::from_be_bytes([response[6], response[7]]) {
        offset = crate::tun_names::name(response, offset)?.1;
        let header = response
            .get(offset..offset + 10)
            .ok_or(io::ErrorKind::InvalidData)?;
        let kind = u16::from_be_bytes([header[0], header[1]]);
        let length = u16::from_be_bytes([header[8], header[9]]) as usize;
        let data = response
            .get(offset + 10..offset + 10 + length)
            .ok_or(io::ErrorKind::InvalidData)?;
        if header[2..4] == [0, 1] && kind == question.kind {
            match (kind, length) {
                (1, 4) => result.push(Ipv4Addr::from(<[u8; 4]>::try_from(data).unwrap()).into()),
                (28, 16) => result
                    .push(std::net::Ipv6Addr::from(<[u8; 16]>::try_from(data).unwrap()).into()),
                _ => (),
            }
        }
        offset += 10 + length;
    }
    result.truncate(16);
    if result.is_empty() {
        return Err(io::Error::new(
            io::ErrorKind::NotFound,
            "DNS_ADDRESS_NODATA",
        ));
    }
    Ok(result)
}
pub(crate) fn encode_ipv4_question(host: &str, id: [u8; 2]) -> io::Result<Vec<u8>> {
    let mut packet = vec![id[0], id[1], 1, 0, 0, 1, 0, 0, 0, 0, 0, 0];
    if host.is_empty() || host.len() > 253 || !host.is_ascii() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "invalid DNS host",
        ));
    }
    for label in host.trim_end_matches('.').split('.') {
        if label.is_empty()
            || label.len() > 63
            || !label
                .bytes()
                .all(|value| value.is_ascii_alphanumeric() || value == b'-')
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "invalid DNS label",
            ));
        }
        packet.push(label.len() as u8);
        packet.extend_from_slice(label.as_bytes());
    }
    packet.extend_from_slice(&[0, 0, 1, 0, 1]); // root, type A, class IN
    Ok(packet)
}

pub async fn probe_direct(router: &crate::native_router::NativeRouter) -> io::Result<()> {
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    timeout(Duration::from_secs(12),async {
        let tcp=async {
            let mut stream=router.connect_target("DIRECT","1.0.0.1",80).await?;
            stream.write_all(b"GET /cdn-cgi/trace HTTP/1.1\r\nHost: one.one.one.one\r\nConnection: close\r\n\r\n").await?;
            let mut header=[0;5];stream.read_exact(&mut header).await?;
            if &header!=b"HTTP/" {return Err(io::Error::new(io::ErrorKind::InvalidData,"DIRECT_TCP_PROBE_INVALID"));}Ok::<(),io::Error>(())
        };
        let udp=async {
            let mut id=[0;2];getrandom::getrandom(&mut id).map_err(io::Error::other)?;
            let packet=encode_ipv4_question("example.com",id)?;
            let mut relay=router.udp_target("DIRECT","8.8.4.4",53).await?;relay.send(&packet).await?;
            let response=relay.receive().await?;decode_ipv4_answer(&response,id)?;Ok::<(),io::Error>(())
        };
        tokio::try_join!(tcp,udp)?;Ok(())
    }).await.map_err(|_|io::Error::new(io::ErrorKind::TimedOut,"DIRECT_TCP_UDP_PROBE_TIMEOUT"))?
}

fn skip_dns_name(packet: &[u8], mut offset: usize) -> io::Result<usize> {
    loop {
        let length = *packet
            .get(offset)
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "truncated DNS name"))?;
        if length & 0xc0 == 0xc0 {
            if packet.get(offset + 1).is_none() {
                break;
            }
            return Ok(offset + 2);
        }
        if length & 0xc0 != 0 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "unsupported DNS name encoding",
            ));
        }
        offset += 1;
        if length == 0 {
            return Ok(offset);
        }
        offset = offset
            .checked_add(length as usize)
            .filter(|end| *end <= packet.len())
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "truncated DNS label"))?;
    }
    Err(io::Error::new(
        io::ErrorKind::InvalidData,
        "truncated DNS pointer",
    ))
}

pub(crate) fn decode_ipv4_answer(packet: &[u8], id: [u8; 2]) -> io::Result<Ipv4Addr> {
    if packet.len() < 12
        || packet[..2] != id
        || packet[2] & 0x80 == 0
        || packet[3] & 0x0f != 0
        || packet[2] & 0x02 != 0
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "invalid bootstrap DNS response",
        ));
    }
    let questions = u16::from_be_bytes([packet[4], packet[5]]) as usize;
    let answers = u16::from_be_bytes([packet[6], packet[7]]) as usize;
    let mut offset = 12;
    for _ in 0..questions {
        offset = skip_dns_name(packet, offset)?;
        offset = offset
            .checked_add(4)
            .filter(|end| *end <= packet.len())
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "truncated DNS question"))?;
    }
    for _ in 0..answers {
        offset = skip_dns_name(packet, offset)?;
        if offset + 10 > packet.len() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "truncated DNS answer",
            ));
        }
        let record_type = u16::from_be_bytes([packet[offset], packet[offset + 1]]);
        let class = u16::from_be_bytes([packet[offset + 2], packet[offset + 3]]);
        let size = u16::from_be_bytes([packet[offset + 8], packet[offset + 9]]) as usize;
        offset += 10;
        if offset
            .checked_add(size)
            .is_none_or(|end| end > packet.len())
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "truncated DNS record",
            ));
        }
        if record_type == 1 && class == 1 && size == 4 {
            return Ok(Ipv4Addr::new(
                packet[offset],
                packet[offset + 1],
                packet[offset + 2],
                packet[offset + 3],
            ));
        }
        offset += size;
    }
    Err(io::Error::new(
        io::ErrorKind::NotFound,
        "bootstrap DNS returned no IPv4 address",
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_a_record_after_cname_and_rejects_wrong_id() {
        let id = [0x12, 0x34];
        let query = encode_ipv4_question("example.com", id).unwrap();
        let mut packet = query.clone();
        packet[2] = 0x81;
        packet[3] = 0x80;
        packet[7] = 2;
        packet.extend_from_slice(&[0xc0, 0x0c, 0, 5, 0, 1, 0, 0, 0, 30, 0, 2, 0xc0, 0x0c]);
        packet.extend_from_slice(&[0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 30, 0, 4, 203, 0, 113, 7]);
        assert_eq!(
            decode_ipv4_answer(&packet, id).unwrap(),
            Ipv4Addr::new(203, 0, 113, 7)
        );
        assert!(decode_ipv4_answer(&packet, [1, 2]).is_err());
        assert!(encode_ipv4_question("bad..name", id).is_err());
    }

    #[tokio::test]
    async fn queries_direct_loopback_resolver() {
        let resolver = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        let endpoint = resolver.local_addr().unwrap();
        let server = tokio::spawn(async move {
            let mut question = [0; 512];
            let (len, remote) = resolver.recv_from(&mut question).await.unwrap();
            let mut answer = question[..len].to_vec();
            answer[2] = 0x81;
            answer[3] = 0x80;
            answer[7] = 1;
            answer.extend_from_slice(&[0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 30, 0, 4, 203, 0, 113, 8]);
            resolver.send_to(&answer, remote).await.unwrap();
        });
        assert_eq!(
            resolve_server_ipv4("example.com", endpoint).await.unwrap(),
            Ipv4Addr::new(203, 0, 113, 8)
        );
        server.await.unwrap();
    }
}
