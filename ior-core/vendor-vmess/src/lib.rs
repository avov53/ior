//! Protocol-only extraction of shoes 0.2.2 (MIT). No proxy engine is embedded.
pub mod address;
pub mod async_stream;
pub mod option_util;
pub mod resolver;
pub mod stream_reader;
pub mod tcp_handler;
pub mod util;
pub mod vmess;
pub mod xudp;
pub mod client_proxy_selector {
    pub struct ClientProxySelector;
}
