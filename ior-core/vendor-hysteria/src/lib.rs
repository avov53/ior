//! # rsteria2
//!
//! A clean, async Rust **client** for the [Hysteria2](https://hysteria.network/)
//! proxy protocol, built on [quinn]. Implements the wire protocol faithfully
//! against the official Go reference: HTTP/3 auth, TCP request/response framing
//! with padding, fast-open, the Brutal congestion controller, UDP relay,
//! Salamander obfuscation and UDP port hopping.
//!
//! ```no_run
//! # async fn run() -> Result<(), Box<dyn std::error::Error>> {
//! use rsteria2::{Config, connect};
//! use tokio::io::{AsyncReadExt, AsyncWriteExt};
//!
//! let cfg = Config {
//!     server_addr: "example.com:443".into(),
//!     auth: "my-password".into(),
//!     ..Default::default()
//! };
//! let client = connect(&cfg).await?;
//! let mut stream = client.tcp_connect("ifconfig.me:80").await?;
//! stream.write_all(b"GET / HTTP/1.0\r\nHost: ifconfig.me\r\n\r\n").await?;
//! let mut buf = Vec::new();
//! stream.read_to_end(&mut buf).await?;
//! # Ok(()) }
//! ```

mod client;
mod config;
mod congestion;
mod connect;
mod error;
mod protocol;
mod reconnect;
mod stream;
mod transport;
mod udp;
#[cfg(test)]
mod udp_http3_tests;
#[cfg(target_os = "android")]
pub mod android_socket {
    use std::{io, sync::{Arc, RwLock}};
    type Protector = Arc<dyn Fn(i32) -> io::Result<()> + Send + Sync>;
    static PROTECTOR: RwLock<Option<Protector>> = RwLock::new(None);
    pub fn set(protector: Option<Protector>) { *PROTECTOR.write().unwrap()=protector; }
    pub(crate) fn protect(fd:i32)->io::Result<()> {
        let callback=PROTECTOR.read().map_err(|_|io::ErrorKind::Other)?.clone()
            .ok_or_else(||io::Error::other("ANDROID_QUIC_PROTECTOR_MISSING"))?;
        callback(fd)
    }
}
#[cfg(test)]
mod test_support;

pub use client::HysteriaClient;
pub use config::{Config, QuicParams};
pub use congestion::{Brutal, BrutalFactory};
pub use connect::connect;
pub use error::{Error, Result};
pub use reconnect::ReconnectableClient;
pub use stream::DuplexStream;
pub use udp::{UdpSession,UdpSender,UdpReceiveMetrics};
