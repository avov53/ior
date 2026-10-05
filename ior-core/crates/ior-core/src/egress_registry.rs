//! Detect actual recapture of this process's bound external socket tuples.
use std::{
    collections::HashMap,
    io,
    net::{IpAddr, Ipv4Addr, SocketAddr},
    pin::Pin,
    sync::{Mutex, OnceLock},
    task::{Context, Poll},
};
use tokio::io::{AsyncRead, AsyncWrite, ReadBuf};
#[derive(Clone, Copy, Hash, PartialEq, Eq)]
struct Tuple {
    local: SocketAddr,
    remote: SocketAddr,
    udp: bool,
}
static SOCKETS: OnceLock<Mutex<HashMap<Tuple, usize>>> = OnceLock::new();
pub struct Guard(Tuple);
pub fn register(local: SocketAddr, remote: SocketAddr, udp: bool) -> Option<Guard> {
    if local.ip().is_unspecified() || local.port() == 0 {
        return None;
    }
    let tuple = Tuple { local, remote, udp };
    let mut sockets = SOCKETS
        .get_or_init(|| Mutex::new(HashMap::new()))
        .lock()
        .ok()?;
    *sockets.entry(tuple).or_default() += 1;
    Some(Guard(tuple))
}
impl Drop for Guard {
    fn drop(&mut self) {
        if let Some(registry) = SOCKETS.get() {
            if let Ok(mut sockets) = registry.lock() {
                if let Some(count) = sockets.get_mut(&self.0) {
                    *count -= 1;
                    if *count == 0 {
                        sockets.remove(&self.0);
                    }
                }
            }
        }
    }
}
pub fn recaptured(packet: &[u8]) -> bool {
    if packet.len() < 24 || packet[0] >> 4 != 4 || packet[6] & 0x1f != 0 || packet[7] != 0 {
        return false;
    }
    let offset = (packet[0] & 15) as usize * 4;
    if offset < 20 || packet.len() < offset + 4 {
        return false;
    }
    let udp = match packet[9] {
        6 => false,
        17 => true,
        _ => return false,
    };
    if packet.len() < offset + if udp { 8 } else { 20 } {
        return false;
    }
    let local = SocketAddr::new(
        IpAddr::V4(Ipv4Addr::from(
            <[u8; 4]>::try_from(&packet[12..16]).unwrap(),
        )),
        u16::from_be_bytes([packet[offset], packet[offset + 1]]),
    );
    let remote = SocketAddr::new(
        IpAddr::V4(Ipv4Addr::from(
            <[u8; 4]>::try_from(&packet[16..20]).unwrap(),
        )),
        u16::from_be_bytes([packet[offset + 2], packet[offset + 3]]),
    );
    SOCKETS
        .get()
        .and_then(|registry| registry.lock().ok())
        .is_some_and(|sockets| sockets.contains_key(&Tuple { local, remote, udp }))
}
pub struct RegisteredStream<S> {
    inner: S,
    _guard: Option<Guard>,
}
impl<S> RegisteredStream<S> {
    pub fn new(inner: S, guard: Option<Guard>) -> Self {
        Self {
            inner,
            _guard: guard,
        }
    }
}
impl<S: AsyncRead + Unpin> AsyncRead for RegisteredStream<S> {
    fn poll_read(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buffer: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        Pin::new(&mut self.get_mut().inner).poll_read(cx, buffer)
    }
}
impl<S: AsyncWrite + Unpin> AsyncWrite for RegisteredStream<S> {
    fn poll_write(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buffer: &[u8],
    ) -> Poll<io::Result<usize>> {
        Pin::new(&mut self.get_mut().inner).poll_write(cx, buffer)
    }
    fn poll_flush(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.get_mut().inner).poll_flush(cx)
    }
    fn poll_shutdown(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.get_mut().inner).poll_shutdown(cx)
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn only_own_live_outgoing_tuple_confirms_a_loop() {
        let mut packet = vec![0; 40];
        packet[0] = 0x45;
        packet[9] = 6;
        packet[12..16].copy_from_slice(&[192, 0, 2, 11]);
        packet[16..20].copy_from_slice(&[203, 0, 113, 7]);
        packet[20..22].copy_from_slice(&19001u16.to_be_bytes());
        packet[22..24].copy_from_slice(&443u16.to_be_bytes());
        assert!(!recaptured(&packet));
        let guard = register(
            "192.0.2.11:19001".parse().unwrap(),
            "203.0.113.7:443".parse().unwrap(),
            false,
        );
        assert!(recaptured(&packet));
        packet[9] = 17;
        assert!(!recaptured(&packet));
        packet[9] = 6;
        drop(guard);
        assert!(!recaptured(&packet));
    }
}
