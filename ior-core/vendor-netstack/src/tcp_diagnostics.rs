//! Optional numeric observer; disabled unless the host installs it.
use smoltcp::socket::tcp::Diagnostics;
use std::{
    net::SocketAddr,
    sync::OnceLock,
    time::{Duration, Instant},
};

#[derive(Clone, Copy, Debug)]
pub struct TcpDiagnostic {
    pub peer: SocketAddr,
    pub destination: SocketAddr,
    pub socket: Diagnostics,
    pub sent_bytes: u64,
    pub received_bytes: u64,
    pub receive_zero_us: u64,
    pub peer_zero_us: u64,
    pub application_wait_us: u64,
    pub final_report: bool,
}

static OBSERVER: OnceLock<fn(TcpDiagnostic)> = OnceLock::new();
pub fn set_observer(observer: fn(TcpDiagnostic)) {
    let _ = OBSERVER.set(observer);
}

pub(crate) struct Observation {
    pub sent: u64,
    pub received: u64,
    last: Instant,
    report_at: Instant,
    totals: [u64; 3],
    blocked: [bool; 3],
    changed: bool,
    previous_bytes: (u64, u64),
}
impl Default for Observation {
    fn default() -> Self {
        let now = Instant::now();
        Self {
            sent: 0,
            received: 0,
            last: now,
            report_at: now,
            totals: [0; 3],
            blocked: [false; 3],
            changed: false,
            previous_bytes: (0, 0),
        }
    }
}
impl Observation {
    pub fn sample(
        &mut self,
        peer: SocketAddr,
        destination: SocketAddr,
        socket: Diagnostics,
        application_blocked: bool,
        final_report: bool,
    ) {
        if OBSERVER.get().is_none() {
            return;
        }
        let now = Instant::now();
        let elapsed = now
            .duration_since(self.last)
            .as_micros()
            .min(u64::MAX as u128) as u64;
        for (total, blocked) in self.totals.iter_mut().zip(self.blocked) {
            if blocked {
                *total = total.saturating_add(elapsed);
            }
        }
        self.last = now;
        self.blocked = [
            socket.receive_window_bytes == 0,
            socket.peer_window_bytes == 0 && socket.send_queue_bytes > 0,
            application_blocked,
        ];
        self.changed |=
            self.blocked.iter().any(|v| *v) || self.previous_bytes != (self.sent, self.received);
        if final_report
            || (self.changed && now.duration_since(self.report_at) >= Duration::from_secs(5))
        {
            self.report_at = now;
            self.changed = false;
            self.previous_bytes = (self.sent, self.received);
            if let Some(observer) = OBSERVER.get() {
                observer(TcpDiagnostic {
                    peer,
                    destination,
                    socket,
                    sent_bytes: self.sent,
                    received_bytes: self.received,
                    receive_zero_us: self.totals[0],
                    peer_zero_us: self.totals[1],
                    application_wait_us: self.totals[2],
                    final_report,
                });
            }
        }
    }
}
