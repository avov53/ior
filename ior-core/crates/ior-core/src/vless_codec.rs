//! Minimal VLESS v0 TCP framing. TLS/Reality transport and Vision flow are
//! separate layers and are not implied by successful header encoding.

use std::{io, net::IpAddr};
use tokio::io::{AsyncRead, AsyncReadExt};

pub fn encode_tcp_request(user_id: &str, host: &str, port: u16) -> io::Result<Vec<u8>> {
    let id = uuid::Uuid::parse_str(user_id)
        .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "invalid VLESS UUID"))?;
    if port == 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "invalid target port",
        ));
    }
    let mut output = Vec::with_capacity(22 + host.len());
    output.push(0); // VLESS version
    output.extend_from_slice(id.as_bytes());
    output.push(0); // empty Addons; Vision is not supported here
    output.push(1); // TCP command
    output.extend_from_slice(&port.to_be_bytes());
    match host.parse::<IpAddr>() {
        Ok(IpAddr::V4(ip)) => {
            output.push(1);
            output.extend_from_slice(&ip.octets());
        }
        Ok(IpAddr::V6(ip)) => {
            output.push(3);
            output.extend_from_slice(&ip.octets());
        }
        Err(_) => {
            if host.is_empty() || host.len() > 255 || !host.is_ascii() || host.contains(':') {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "invalid VLESS target domain",
                ));
            }
            output.push(2);
            output.push(host.len() as u8);
            output.extend_from_slice(host.as_bytes());
        }
    }
    Ok(output)
}

pub async fn read_response_header<R: AsyncRead + Unpin>(reader: &mut R) -> io::Result<()> {
    let mut header = [0; 2];
    reader.read_exact(&mut header).await?;
    if header[0] != 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "unsupported VLESS response version",
        ));
    }
    if header[1] != 0 {
        let mut addons = vec![0; header[1] as usize];
        reader.read_exact(&mut addons).await?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn encodes_tcp_domain_header_in_xray_order() {
        let bytes =
            encode_tcp_request("11111111-1111-1111-1111-111111111111", "example.com", 443).unwrap();
        assert_eq!(bytes[0], 0);
        assert_eq!(&bytes[1..17], &[0x11; 16]);
        assert_eq!(&bytes[17..22], &[0, 1, 1, 0xbb, 2]);
        assert_eq!(bytes[22], 11);
        assert_eq!(&bytes[23..], b"example.com");
    }

    #[tokio::test]
    async fn response_header_consumes_addons_without_body() {
        let mut input: &[u8] = &[0, 2, 0xaa, 0xbb, 0x42];
        read_response_header(&mut input).await.unwrap();
        assert_eq!(input, &[0x42]);
    }
}
