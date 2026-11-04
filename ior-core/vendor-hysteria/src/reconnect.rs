//! A reconnecting wrapper around [`HysteriaClient`] (Go: core/client/reconnect.go).
//!
//! The connection is (re)established lazily on first use and rebuilt
//! transparently whenever the underlying QUIC connection has been closed —
//! except after an explicit [`close`](ReconnectableClient::close).

use std::sync::Arc;

use tokio::sync::Mutex;

use crate::{config::Config, connect, error::Result, stream::DuplexStream, udp::UdpSession};

/// A client that reconnects on demand. Cheap to share behind an `Arc`.
pub struct ReconnectableClient {
    config: Config,
    inner: Mutex<State>,
}

#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn stale_failure_cannot_invalidate_replacement_generation() {
        let (old_endpoint,_old_server,old_conn,_old_remote)=crate::test_support::pair().await;
        let (endpoint,_server,conn,_remote)=crate::test_support::pair().await;
        let old=Arc::new(crate::HysteriaClient::new(old_conn,old_endpoint,true,false,0));
        let fresh=Arc::new(crate::HysteriaClient::new(conn,endpoint,true,false,0));
        let client=ReconnectableClient::new(Config::default());
        client.inner.lock().await.client=Some(fresh.clone());
        client.invalidate_if_current(&old).await;
        assert!(Arc::ptr_eq(&client.current().await.unwrap(),&fresh));
        drop(old);assert!(fresh.connection().close_reason().is_none());
        client.invalidate_if_current(&fresh).await;
        assert!(client.inner.lock().await.client.is_none());
    }
}

#[derive(Default)]
struct State {
    client: Option<Arc<crate::HysteriaClient>>,
    /// Number of successful connects so far (useful for logging/metrics).
    count: u64,
    /// Permanently closed by the caller.
    closed: bool,
}

impl ReconnectableClient {
    /// Create a lazy reconnectable client; the first connection is made on the
    /// first call to [`tcp_connect`](Self::tcp_connect) or [`udp`](Self::udp).
    pub fn new(config: Config) -> ReconnectableClient {
        ReconnectableClient {
            config,
            inner: Mutex::new(State::default()),
        }
    }

    /// Eagerly establish the first connection (Go's non-lazy mode).
    pub async fn connect_now(config: Config) -> Result<ReconnectableClient> {
        let rc = ReconnectableClient::new(config);
        rc.current().await?;
        Ok(rc)
    }

    /// Number of times a connection has been (re)established.
    pub async fn connect_count(&self) -> u64 {
        self.inner.lock().await.count
    }
    /// Diagnostics must never wait behind a reconnect holding the state lock.
    pub fn try_connect_count(&self) -> Option<u64> {
        self.inner.try_lock().ok().map(|state| state.count)
    }

    /// Get a live client, reconnecting if there is none or the existing one's
    /// QUIC connection has closed.
    async fn current(&self) -> Result<Arc<crate::HysteriaClient>> {
        let mut state = self.inner.lock().await;
        if state.closed {
            return Err(crate::Error::Protocol("client permanently closed".into()));
        }
        let stale = match &state.client {
            Some(c) => c.connection().close_reason().is_some(),
            None => true,
        };
        if stale {
            let client = Arc::new(connect(&self.config).await?);
            state.client = Some(client.clone());
            state.count += 1;
            Ok(client)
        } else {
            Ok(state.client.as_ref().unwrap().clone())
        }
    }

    /// Drop the current connection so the next call reconnects (e.g. after the
    /// device wakes from sleep with a dead UDP socket).
    pub async fn invalidate(&self) {
        let mut state = self.inner.lock().await;
        state.client = None;
    }

    // An old failed stream must not tear down a replacement connection.
    async fn invalidate_if_current(&self, failed: &Arc<crate::HysteriaClient>) {
        let mut state = self.inner.lock().await;
        if state.client.as_ref().is_some_and(|current| Arc::ptr_eq(current, failed)) {
            state.client = None;
        }
    }

    /// Open a proxied TCP connection, reconnecting once if the connection was
    /// found to be closed.
    pub async fn tcp_connect(&self, address: &str) -> Result<DuplexStream> {
        let client = self.current().await?;
        match client.tcp_connect(address).await {
            Ok(s) => Ok(s),
            Err(e) => {
                // If the connection died, drop it and try a fresh one once.
                if client.connection().close_reason().is_some() {
                    self.invalidate_if_current(&client).await;
                    self.current().await?.tcp_connect(address).await
                } else {
                    Err(e)
                }
            }
        }
    }

    /// Open a new UDP relay session on a live connection.
    pub async fn udp(&self) -> Result<UdpSession> {
        self.current().await?.udp()
    }

    /// Permanently close the client. Subsequent calls return an error.
    pub async fn close(&self) {
        let mut state = self.inner.lock().await;
        state.closed = true;
        state.client = None;
    }
    /// Observation only: never opens or restarts a connection.
    pub fn transport_metrics(&self)->Option<(std::time::Duration,u64,u64,u64,u64)> {
        let state=self.inner.try_lock().ok()?;
        let client=state.client.as_ref()?;
        let stats=client.connection().stats();
        Some((stats.path.rtt,stats.path.cwnd,stats.path.lost_packets,stats.path.sent_packets,state.count))
    }
    pub fn udp_metrics(&self)->Option<(u64,u64,u64,u64)>{self.inner.try_lock().ok()?.client.as_ref()?.udp_metrics()}
}
