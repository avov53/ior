use std::sync::Arc;
use tokio::sync::{OwnedSemaphorePermit, Semaphore};

pub const BASE_RECEIVE: usize = 128 * 1024;
pub const LARGE_RECEIVE: usize = 512 * 1024;
/// Accepted AWG fallback window gate at RTT 200ms / deterministic 0.5% loss.
pub const AWG_RECEIVE: usize = 2 * 1024 * 1024;
pub const DEFAULT_RECEIVE: usize = LARGE_RECEIVE;
pub const DEFAULT_REASSEMBLY_SEGMENTS: usize = 4;
pub const EXTRA_BUDGET: usize = 64 * 1024 * 1024;

// Bounded receive allocations for controlled TCP tests; WireGuard retains 128 KiB.
// CUBIC remains off: it did not meet the earlier sender/latency gates.
#[derive(Clone, Copy)]
pub struct Tuning {
    pub receive_bytes: usize,
    pub cubic: bool,
    pub reassembly_segments: usize,
}
impl Default for Tuning {
    fn default() -> Self {
        Self {
            receive_bytes: DEFAULT_RECEIVE,
            cubic: false,
            reassembly_segments: DEFAULT_REASSEMBLY_SEGMENTS,
        }
    }
}
impl Tuning {
    pub fn receive_allocation(
        self,
        budget: &Arc<Semaphore>,
    ) -> (usize, Option<OwnedSemaphorePermit>) {
        for size in [self.receive_bytes, LARGE_RECEIVE] {
            if self.receive_bytes < size || size <= BASE_RECEIVE {
                continue;
            }
            if let Ok(permit) = budget
                .clone()
                .try_acquire_many_owned((size - BASE_RECEIVE) as u32)
            {
                return (size, Some(permit));
            }
        }
        (BASE_RECEIVE, None)
    }
    pub fn congestion(self) -> smoltcp::socket::tcp::CongestionControl {
        if self.cubic {
            smoltcp::socket::tcp::CongestionControl::Cubic
        } else {
            smoltcp::socket::tcp::CongestionControl::None
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn budget_falls_back_and_returns_on_close_and_cancellation() {
        let budget = Arc::new(Semaphore::new(EXTRA_BUDGET));
        let tuning = Tuning {
            receive_bytes: LARGE_RECEIVE,
            cubic: false,
            reassembly_segments: 4,
        };
        let mut permits = Vec::new();
        for _ in 0..EXTRA_BUDGET / (LARGE_RECEIVE - BASE_RECEIVE) {
            let (size, permit) = tuning.receive_allocation(&budget);
            assert_eq!(size, LARGE_RECEIVE);
            permits.push(permit.unwrap());
        }
        assert_eq!(tuning.receive_allocation(&budget).0, BASE_RECEIVE);
        drop(permits);
        assert_eq!(budget.available_permits(), EXTRA_BUDGET);
        let other = budget.clone();
        let task = tokio::spawn(async move {
            let (_size, _permit) = tuning.receive_allocation(&other);
            std::future::pending::<()>().await;
        });
        tokio::task::yield_now().await;
        assert_eq!(
            budget.available_permits(),
            EXTRA_BUDGET - (LARGE_RECEIVE - BASE_RECEIVE)
        );
        task.abort();
        let _ = task.await;
        assert_eq!(budget.available_permits(), EXTRA_BUDGET);
        assert_eq!(
            Tuning::default().receive_allocation(&budget).0,
            DEFAULT_RECEIVE
        );
        assert_eq!(
            Tuning::default().congestion(),
            smoltcp::socket::tcp::CongestionControl::None
        );
    }
    #[test]
    fn larger_windows_fallback_in_order_without_exceeding_budget() {
        for size in [1024 * 1024, 2 * 1024 * 1024] {
            let budget = Arc::new(Semaphore::new(size - BASE_RECEIVE));
            let tuning = Tuning {
                receive_bytes: size,
                cubic: false,
                reassembly_segments: 4,
            };
            let (allocated, permit) = tuning.receive_allocation(&budget);
            assert_eq!(allocated, size);
            assert_eq!(tuning.receive_allocation(&budget).0, BASE_RECEIVE);
            drop(permit);
            assert_eq!(budget.available_permits(), size - BASE_RECEIVE);
            let limited = Arc::new(Semaphore::new(LARGE_RECEIVE - BASE_RECEIVE));
            let (allocated, permit) = tuning.receive_allocation(&limited);
            assert_eq!(allocated, LARGE_RECEIVE);
            assert_eq!(tuning.receive_allocation(&limited).0, BASE_RECEIVE);
            drop(permit);
            assert_eq!(limited.available_permits(), LARGE_RECEIVE - BASE_RECEIVE);
        }
    }
}
