//! Per-destination XUDP mux session. Crypto and Vision remain in the carrier.
use crate::{
    proxy_inbound::RelayStream,
    udp_relay::{DatagramRelay, ReceiveFuture, SendFuture},
};
use std::{io, net::IpAddr};
use tokio::io::{AsyncReadExt, AsyncWriteExt, ReadHalf, WriteHalf};

pub struct XudpDatagram {
    writer: WriteHalf<Box<dyn RelayStream>>,
    receiver: tokio::sync::mpsc::Receiver<io::Result<Vec<u8>>>,
    reader: tokio::task::JoinHandle<()>,
    address: Vec<u8>,
    first: bool,
}
fn address(host: &str, port: u16) -> io::Result<Vec<u8>> {
    let mut value = port.to_be_bytes().to_vec();
    match host.parse::<IpAddr>() {
        Ok(IpAddr::V4(ip)) => {
            value.push(1);
            value.extend(ip.octets());
        }
        Ok(IpAddr::V6(ip)) => {
            value.push(3);
            value.extend(ip.octets());
        }
        Err(_)
            if !host.is_empty()
                && host.len() <= 255
                && host.is_ascii()
                && !host.contains(['\0', '\r', '\n']) =>
        {
            value.extend([2, host.len() as u8]);
            value.extend(host.as_bytes());
        }
        _ => return Err(io::ErrorKind::InvalidInput.into()),
    }
    if port == 0 {
        return Err(io::ErrorKind::InvalidInput.into());
    }
    Ok(value)
}
fn packet(address: &[u8], first: bool, data: &[u8]) -> io::Result<Vec<u8>> {
    if data.is_empty() || data.len() > 65507 {
        return Err(io::ErrorKind::InvalidInput.into());
    }
    let mut metadata = vec![0, 0, if first { 1 } else { 2 }, 1];
    if first {
        metadata.push(2);
        metadata.extend(address);
        metadata.extend([0; 8]);
    }
    let mut packet = (metadata.len() as u16).to_be_bytes().to_vec();
    packet.extend(metadata);
    packet.extend((data.len() as u16).to_be_bytes());
    packet.extend(data);
    Ok(packet)
}
async fn receive(reader: &mut ReadHalf<Box<dyn RelayStream>>) -> io::Result<Vec<u8>> {
    for _ in 0..64 {
        let length = reader.read_u16().await? as usize;
        if !(4..=512).contains(&length) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "XUDP_METADATA_INVALID",
            ));
        }
        let mut metadata = vec![0; length];
        reader.read_exact(&mut metadata).await?;
        if metadata[..2] != [0, 0] || !matches!(metadata[2], 2 | 3 | 4) || metadata[3] & !1 != 0 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "XUDP_SESSION_INVALID",
            ));
        }
        if metadata[2] == 3 {
            return Err(io::Error::new(
                io::ErrorKind::ConnectionAborted,
                "XUDP_SESSION_CLOSED",
            ));
        }
        if metadata[3] & 1 == 1 {
            let length = reader.read_u16().await? as usize;
            if length > 65507 {
                return Err(io::ErrorKind::InvalidData.into());
            }
            let mut data = vec![0; length];
            reader.read_exact(&mut data).await?;
            if metadata[2] == 2 && !data.is_empty() {
                return Ok(data);
            }
        }
    }
    Err(io::Error::new(
        io::ErrorKind::InvalidData,
        "XUDP_CONTROL_FLOOD",
    ))
}
impl XudpDatagram {
    pub fn new(stream: Box<dyn RelayStream>, host: String, port: u16) -> io::Result<Self> {
        let address = address(&host, port)?;
        let (mut read, writer) = tokio::io::split(stream);
        let (sender, receiver) = tokio::sync::mpsc::channel(32);
        let reader = tokio::spawn(async move {
            loop {
                let result = receive(&mut read).await;
                let failed = result.is_err();
                if sender.send(result).await.is_err() || failed {
                    break;
                }
            }
        });
        Ok(Self {
            writer,
            receiver,
            reader,
            address,
            first: true,
        })
    }
}
impl Drop for XudpDatagram {
    fn drop(&mut self) {
        self.reader.abort();
    }
}
impl DatagramRelay for XudpDatagram {
    fn send<'a>(&'a mut self, data: &'a [u8]) -> SendFuture<'a> {
        Box::pin(async move {
            let bytes = packet(&self.address, self.first, data)?;
            self.writer.write_all(&bytes).await?;
            self.writer.flush().await?;
            self.first = false;
            Ok(())
        })
    }
    fn receive(&mut self) -> ReceiveFuture<'_> {
        Box::pin(async move {
            self.receiver
                .recv()
                .await
                .unwrap_or_else(|| Err(io::ErrorKind::UnexpectedEof.into()))
        })
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn ipv6_and_datagram_boundaries() {
        let a=address("2001:db8::1",443).unwrap();
        assert_eq!(&a[..3],&[1,187,3]); assert_eq!(a.len(),19);
        for size in [1200,1280,1500,65507] {let p=packet(&a,true,&vec![7;size]).unwrap();assert_eq!(p.len(),size+36);}
        assert!(packet(&a,true,&vec![0;65508]).is_err());
        assert!(address("::1",0).is_err());
    }
    #[tokio::test]
    async fn fragmented_receive_preserves_datagram_and_drop_cancels_reader() {
        let (stream,mut peer)=tokio::io::duplex(32);
        let mut relay=XudpDatagram::new(Box::new(stream),"::1".into(),443).unwrap();
        let sender=tokio::spawn(async move {
            for b in [0,4,0,0,2,1,0,3,1,2,3] {peer.write_all(&[b]).await.unwrap();tokio::task::yield_now().await;}
            peer
        });
        assert_eq!(relay.receive().await.unwrap(),vec![1,2,3]);
        let mut peer=sender.await.unwrap();drop(relay);
        let mut b=[0];assert_eq!(tokio::time::timeout(std::time::Duration::from_secs(1),peer.read(&mut b)).await.unwrap().unwrap(),0);
    }
    #[test]
    fn first_and_subsequent_packets_have_distinct_metadata() {
        let address = address("8.8.8.8", 53).unwrap();
        let first = packet(&address, true, b"dns").unwrap();
        assert_eq!(&first[..9], &[0, 20, 0, 0, 1, 1, 2, 0, 53]);
        assert_eq!(&first[22..], &[0, 3, b'd', b'n', b's']);
        assert_eq!(
            packet(&address, false, b"dns").unwrap(),
            vec![0, 4, 0, 0, 2, 1, 0, 3, b'd', b'n', b's']
        );
        assert!(packet(&address, true, &[]).is_err());
    }
}
