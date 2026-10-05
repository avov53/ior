//! Numeric-only, cumulative tunnel metrics. Packet hot paths never write logs.
use std::{
    sync::atomic::{AtomicU64, Ordering},
    time::Instant,
};

pub(crate) static RX_DROPPED: AtomicU64 = AtomicU64::new(0);
pub(crate) static TUN_UDP_DROPPED: AtomicU64 = AtomicU64::new(0);
pub(crate) static TRANSPORT_QUEUE: WaitMetric = WaitMetric::new();
pub(crate) static APPLICATION_QUEUE: WaitMetric = WaitMetric::new();

pub(crate) struct WaitMetric {
    count: AtomicU64,
    micros: AtomicU64,
    maximum: AtomicU64,
}
impl WaitMetric {
    const fn new() -> Self {
        Self {
            count: AtomicU64::new(0),
            micros: AtomicU64::new(0),
            maximum: AtomicU64::new(0),
        }
    }
    fn record(&self, started: Instant) {
        let us = started.elapsed().as_micros().min(u64::MAX as u128) as u64;
        self.count.fetch_add(1, Ordering::Relaxed);
        self.micros.fetch_add(us, Ordering::Relaxed);
        self.maximum.fetch_max(us, Ordering::Relaxed);
    }
}
pub(crate) struct QueueWait<'a> {
    metric: &'a WaitMetric,
    started: Option<Instant>,
}
impl<'a> QueueWait<'a> {
    pub(crate) fn new(metric: &'a WaitMetric) -> Self {
        Self {
            metric,
            started: None,
        }
    }
    pub(crate) fn update(&mut self, full: bool) {
        if full {
            self.started.get_or_insert_with(Instant::now);
        } else if let Some(started) = self.started.take() {
            self.metric.record(started);
        }
    }
}
impl Drop for QueueWait<'_> {
    fn drop(&mut self) {
        self.update(false);
    }
}

pub(crate) struct TcpObservation {
    pub sent: u64,
    pub received: u64,
    remote: std::net::SocketAddr,
    last: Instant,
    reported: Instant,
    previous_bytes: (u64, u64),
    blocked: [bool; 3],
    totals: [u64; 3],
    socket: Option<smoltcp::socket::tcp::Diagnostics>,
}
impl TcpObservation {
    pub fn new(remote: std::net::SocketAddr) -> Self {
        let now = Instant::now();
        Self {
            sent: 0,
            received: 0,
            remote,
            last: now,
            reported: now,
            previous_bytes: (0, 0),
            blocked: [false; 3],
            totals: [0; 3],
            socket: None,
        }
    }
    pub fn sample(&mut self, socket: smoltcp::socket::tcp::Diagnostics, application_blocked: bool) {
        let now = Instant::now();
        self.account(now);
        self.blocked = [
            socket.receive_window_bytes == 0,
            socket.peer_window_bytes == 0 && socket.send_queue_bytes > 0,
            application_blocked,
        ];
        self.socket = Some(socket);
        if now.duration_since(self.reported) >= std::time::Duration::from_secs(5)
            && ((self.sent, self.received) != self.previous_bytes
                || self.blocked.iter().any(|v| *v))
        {
            self.emit("periodic");
            self.reported = now;
            self.previous_bytes = (self.sent, self.received);
        }
    }
    fn account(&mut self, now: Instant) {
        let elapsed = now
            .duration_since(self.last)
            .as_micros()
            .min(u64::MAX as u128) as u64;
        for (i, blocked) in self.blocked.iter().enumerate() {
            if *blocked {
                self.totals[i] = self.totals[i].saturating_add(elapsed);
            }
        }
        self.last = now;
    }
    fn emit(&self, operation: &str) {
        if let Some(s) = self.socket {
            crate::trace::emit(
                serde_json::json!({"stage":"transport","code":"TUNNEL_TCP_SOCKET_METRICS","side":"outbound","destination":self.remote.to_string(),"transport":"tcp","operation":operation,"sentBytes":self.sent,"receivedBytes":self.received,"rttEstimateMs":s.rtt_estimate_ms,"rxWindowBytes":s.receive_window_bytes,"peerWindowBytes":s.peer_window_bytes,"rxQueuedBytes":s.receive_queue_bytes,"txQueuedBytes":s.send_queue_bytes,"rxZeroWindowUs":self.totals[0],"peerZeroWindowUs":self.totals[1],"applicationWaitUs":self.totals[2],"reassemblyDroppedPackets":s.reassembly_dropped_packets}),
            );
        }
    }
}
impl Drop for TcpObservation {
    fn drop(&mut self) {
        self.account(Instant::now());
        self.emit("final");
    }
}

pub(crate) fn report() {
    for (operation, metric) in [
        ("transport_queue", &TRANSPORT_QUEUE),
        ("application_queue", &APPLICATION_QUEUE),
    ] {
        let count = metric.count.load(Ordering::Relaxed);
        if count > 0 {
            crate::trace::emit(
                serde_json::json!({"stage":"transport","code":"TUNNEL_QUEUE_METRICS","operation":operation,"count":count,"queueWaitUs":metric.micros.load(Ordering::Relaxed),"maxWaitUs":metric.maximum.load(Ordering::Relaxed)}),
            );
        }
    }
    for (operation, metric) in [("tunnel_rx", &RX_DROPPED), ("tun_udp", &TUN_UDP_DROPPED)] {
        let count = metric.load(Ordering::Relaxed);
        if count > 0 {
            crate::trace::emit(
                serde_json::json!({"stage":"transport","code":"TUNNEL_QUEUE_DROPS","operation":operation,"droppedPackets":count}),
            );
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn tcp_waits_account_only_the_blocked_interval_and_saturate() {
        let mut observation = TcpObservation::new("127.0.0.1:443".parse().unwrap());
        let start = observation.last;
        observation.blocked = [true, false, true];
        observation.account(start + std::time::Duration::from_millis(7));
        assert_eq!(observation.totals, [7000, 0, 7000]);
        observation.blocked = [false, true, false];
        observation.account(start + std::time::Duration::from_millis(10));
        assert_eq!(observation.totals, [7000, 3000, 7000]);
        observation.totals[1] = u64::MAX;
        observation.account(start + std::time::Duration::from_millis(11));
        assert_eq!(observation.totals[1], u64::MAX);
    }
    #[test]
    fn wait_counts_full_episodes_and_flushes_on_drop() {
        let metric = WaitMetric::new();
        {
            let mut wait = QueueWait::new(&metric);
            wait.update(false);
            wait.update(true);
            wait.update(true);
            wait.update(false);
            wait.update(false);
            wait.update(true);
        }
        assert_eq!(metric.count.load(Ordering::Relaxed), 2);
    }
}
