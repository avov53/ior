//! Retained raw TCP packet storage, shared by both directions of one session.
use std::{
    io,
    ops::{Deref, DerefMut},
    sync::{
        atomic::{AtomicU64, AtomicUsize, Ordering},
        Arc,
    },
};
use tokio::sync::{OwnedSemaphorePermit, Semaphore};
pub const LIMIT: usize = 64 * 1024 * 1024;
pub struct Budget {
    permits: Arc<Semaphore>,
    peak: AtomicUsize,
    dropped: AtomicU64,
    queue_dropped: AtomicU64,
}
impl Default for Budget {
    fn default() -> Self {
        Self {
            permits: Arc::new(Semaphore::new(LIMIT)),
            peak: AtomicUsize::new(0),
            dropped: AtomicU64::new(0),
            queue_dropped: AtomicU64::new(0),
        }
    }
}
impl Budget {
    pub fn used(&self) -> usize {
        LIMIT - self.permits.available_permits()
    }
    pub fn queue_drop(&self) {
        self.queue_dropped.fetch_add(1, Ordering::Relaxed);
    }
    pub fn report(&self) {
        crate::trace::emit(
            serde_json::json!({"stage":"transport","code":"AWG_RAW_PACKET_METRICS","packetBudgetBytes":LIMIT,"packetBudgetUsedBytes":self.used(),"packetBudgetPeakBytes":self.peak.load(Ordering::Relaxed),"budgetDroppedPackets":self.dropped.load(Ordering::Relaxed),"queueDroppedPackets":self.queue_dropped.load(Ordering::Relaxed)}),
        );
    }
    pub fn copy(self: &Arc<Self>, bytes: &[u8]) -> io::Result<Packet> {
        // Reserve before allocating. Include both outer and quoted inner headers
        // for IPv4 ICMP -> IPv6 ICMP translation.
        let size = bytes
            .len()
            .checked_add(40)
            .ok_or(io::ErrorKind::InvalidData)?;
        let permit = self
            .permits
            .clone()
            .try_acquire_many_owned(size.try_into().map_err(|_| io::ErrorKind::InvalidData)?)
            .map_err(|_| {
                self.dropped.fetch_add(1, Ordering::Relaxed);
                io::Error::new(io::ErrorKind::WouldBlock, "AWG_RAW_PACKET_BUDGET_EXHAUSTED")
            })?;
        self.peak.fetch_max(self.used(), Ordering::Relaxed);
        let mut data = Vec::with_capacity(size);
        data.extend_from_slice(bytes);
        Ok(Packet {
            data,
            permit: Some(permit),
            created: std::time::Instant::now(),
        })
    }
    /// Charge an already encoded datagram without copying it a second time.
    pub fn retain(self: &Arc<Self>, data: Vec<u8>) -> io::Result<Packet> {
        let size = data.capacity().max(data.len()).checked_add(40)
            .ok_or(io::ErrorKind::InvalidData)?;
        let permit = self.permits.clone()
            .try_acquire_many_owned(size.try_into().map_err(|_| io::ErrorKind::InvalidData)?)
            .map_err(|_| {
                self.dropped.fetch_add(1, Ordering::Relaxed);
                io::Error::new(io::ErrorKind::WouldBlock, "AWG_RAW_PACKET_BUDGET_EXHAUSTED")
            })?;
        self.peak.fetch_max(self.used(), Ordering::Relaxed);
        Ok(Packet { data, permit: Some(permit), created: std::time::Instant::now() })
    }
}
pub struct Packet {
    data: Vec<u8>,
    permit: Option<OwnedSemaphorePermit>,
    created: std::time::Instant,
}
impl From<Vec<u8>> for Packet {
    fn from(data: Vec<u8>) -> Self {
        Self {
            data,
            permit: None,
            created: std::time::Instant::now(),
        }
    }
}
impl Deref for Packet {
    type Target = [u8];
    fn deref(&self) -> &[u8] {
        &self.data
    }
}
impl DerefMut for Packet {
    fn deref_mut(&mut self) -> &mut [u8] {
        &mut self.data
    }
}
impl AsRef<[u8]> for Packet {
    fn as_ref(&self) -> &[u8] {
        &self.data
    }
}
impl Packet {
    pub fn created(&self) -> std::time::Instant {
        self.created
    }
    pub fn as_slice(&self) -> &[u8] {
        &self.data
    }
    pub fn into_vec(self) -> Vec<u8> {
        self.data
    }
    pub fn replace(&mut self, bytes: &[u8]) -> io::Result<()> {
        if self
            .permit
            .as_ref()
            .is_some_and(|p| bytes.len() > p.num_permits())
        {
            return Err(io::ErrorKind::InvalidData.into());
        }
        self.data.clear();
        self.data.extend_from_slice(bytes);
        Ok(())
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn budget_exhaustion_and_cancellation_release_storage() {
        let budget = Arc::new(Budget::default());
        let bytes = vec![0; 65495];
        let mut packets = Vec::new();
        for _ in 0..1024 {
            packets.push(budget.copy(&bytes).unwrap());
        }
        assert_eq!(budget.used(), 1024 * 65535);
        assert!(budget.copy(&bytes).is_err());
        drop(packets);
        assert_eq!(budget.used(), 0);
    }
    #[test]
    fn translation_keeps_the_same_reservation() {
        let budget = Arc::new(Budget::default());
        let mut packet = budget.copy(&[0; 40]).unwrap();
        packet.replace(&[1; 80]).unwrap();
        assert_eq!(budget.used(), 80);
        assert!(packet.replace(&[0; 81]).is_err());
        drop(packet);
        assert_eq!(budget.used(), 0);
    }
}
