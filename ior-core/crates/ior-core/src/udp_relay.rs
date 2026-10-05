use std::{future::Future, io, pin::Pin};

pub type UdpFuture = Pin<Box<dyn Future<Output = io::Result<Box<dyn DatagramRelay>>> + Send>>;
pub type SendFuture<'a> = Pin<Box<dyn Future<Output = io::Result<()>> + Send + 'a>>;
pub type ReceiveFuture<'a> = Pin<Box<dyn Future<Output = io::Result<Vec<u8>>> + Send + 'a>>;

/// A connected destination flow; each relay belongs to one ingress association.
pub trait DatagramRelay: Send {
    fn send<'a>(&'a mut self, data: &'a [u8]) -> SendFuture<'a>;
    fn receive(&mut self) -> ReceiveFuture<'_>;
}

pub struct HysteriaDatagram {
    pub session: rsteria2::UdpSession,
    pub destination: String,
}
impl DatagramRelay for HysteriaDatagram {
    fn send<'a>(&'a mut self, data: &'a [u8]) -> SendFuture<'a> {
        Box::pin(async move {
            self.session.send(data, &self.destination).map_err(|_| {
                io::Error::new(io::ErrorKind::ConnectionAborted, "HY2_UDP_SEND_FAILED")
            })
        })
    }
    fn receive(&mut self) -> ReceiveFuture<'_> {
        Box::pin(async move {
            self.session
                .recv()
                .await
                .map(|(data, _)| data)
                .map_err(|_| {
                    io::Error::new(io::ErrorKind::ConnectionAborted, "HY2_UDP_RECEIVE_FAILED")
                })
        })
    }
}

/// Encodes RFC 1928 address headers, shared by ingress replies and tests.
pub fn encode_address(host: &str, port: u16) -> io::Result<Vec<u8>> {
    use std::net::IpAddr;
    let mut result = Vec::new();
    match host.parse::<IpAddr>() {
        Ok(IpAddr::V4(ip)) => {
            result.push(1);
            result.extend(ip.octets());
        }
        Ok(IpAddr::V6(ip)) => {
            result.push(4);
            result.extend(ip.octets());
        }
        Err(_) if !host.is_empty() && host.len() <= 255 && host.is_ascii() => {
            result.extend([3, host.len() as u8]);
            result.extend(host.as_bytes());
        }
        _ => return Err(io::ErrorKind::InvalidInput.into()),
    }
    result.extend(port.to_be_bytes());
    Ok(result)
}

pub fn decode_packet(packet: &[u8]) -> io::Result<(String, u16, usize)> {
    use std::net::{Ipv4Addr, Ipv6Addr};
    if packet.len() < 4 || packet[..3] != [0, 0, 0] {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "UDP_FRAGMENT_OR_HEADER_INVALID",
        ));
    }
    let (host, offset) = match packet[3] {
        1 if packet.len() >= 10 => (
            Ipv4Addr::new(packet[4], packet[5], packet[6], packet[7]).to_string(),
            8,
        ),
        4 if packet.len() >= 22 => {
            let bytes: [u8; 16] = packet[4..20].try_into().unwrap();
            (Ipv6Addr::from(bytes).to_string(), 20)
        }
        3 if packet.len() >= 5 && packet[4] != 0 && packet.len() >= 7 + packet[4] as usize => {
            let end = 5 + packet[4] as usize;
            (
                std::str::from_utf8(&packet[5..end])
                    .map_err(|_| io::ErrorKind::InvalidData)?
                    .to_owned(),
                end,
            )
        }
        _ => return Err(io::ErrorKind::InvalidData.into()),
    };
    let port = u16::from_be_bytes([packet[offset], packet[offset + 1]]);
    if port == 0 {
        return Err(io::ErrorKind::InvalidData.into());
    }
    Ok((host, port, offset + 2))
}
