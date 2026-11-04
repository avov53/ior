//! UDP relay over QUIC unreliable datagrams (Go: core/client/udp.go +
//! the `udpIOImpl` in client.go).
//!
//! A single background task reads datagrams off the QUIC connection, parses
//! them into [`UdpMessage`]s and dispatches them to the owning session by
//! Session ID. Each [`UdpSession`] reassembles fragments locally and sends with
//! automatic fragmentation when a payload exceeds the current datagram limit.

use std::{
    collections::HashMap,
    sync::{
        atomic::{AtomicU32, AtomicU64, AtomicUsize, Ordering},
        Arc, Mutex, Weak,
    },
};

use bytes::Bytes;
use rand::Rng;
use tokio::sync::mpsc;

use crate::{
    error::{Error, Result},
    protocol::{frag_udp_message, Defragger, UdpMessage, MAX_DATAGRAM_FRAME_SIZE, MAX_MESSAGE_LENGTH},
};

/// Per-session inbound queue depth. Like Go's `udpMessageChanSize`, the queue
/// is bounded and messages are dropped when full (UDP is lossy by nature), so a
/// stalled reader can't grow memory without bound.
const SESSION_CHAN_SIZE: usize = 1024;
#[derive(Default)]
struct UdpMetrics { queued:AtomicU64, bytes:AtomicU64, wait_us:AtomicU64, receive_drops:AtomicU64 }

#[derive(Default)]
struct ReceiveMetrics { high_water:AtomicUsize, drops:AtomicU64 }
struct SessionRoute { sender:mpsc::Sender<UdpMessage>, receive:Arc<ReceiveMetrics> }

/// Numeric per-session receive queue evidence; no destination or payload content.
#[derive(Clone,Copy,Debug)]
pub struct UdpReceiveMetrics {
    pub session_id:u32,
    pub queued_datagrams:usize,
    pub capacity:usize,
    pub high_water:usize,
    pub dropped_datagrams:u64,
}

// read_datagram() can complete immediately for a large queued QUIC burst;
// try_send() has no cooperative scheduling point. Bound each dispatch turn
// rather than enlarging the queue or blocking every other UDP session on it.
const RECEIVE_BATCH:usize=64;
#[derive(Default)]
struct ReceiveBudget(usize);
impl ReceiveBudget {
    async fn consumed(&mut self) {
        self.0+=1;
        if self.0>=RECEIVE_BATCH {self.0=0;tokio::task::yield_now().await;}
    }
}

/// Routes inbound UDP datagrams to per-session channels.
pub(crate) struct UdpSessionManager {
    conn: quinn::Connection,
    sessions: Mutex<HashMap<u32, SessionRoute>>,
    next_id: AtomicU32,
    metrics:Arc<UdpMetrics>,
}

impl UdpSessionManager {
    /// Spawn the receive loop and return the manager.
    pub(crate) fn new(conn: quinn::Connection) -> Arc<UdpSessionManager> {
        let mgr = Arc::new(UdpSessionManager {
            conn: conn.clone(),
            sessions: Mutex::new(HashMap::new()),
            next_id: AtomicU32::new(1),
            metrics:Arc::new(UdpMetrics::default()),
        });
        let weak = Arc::downgrade(&mgr);
        tokio::spawn(receive_loop(conn, weak));
        mgr
    }

    /// Open a new UDP session with a fresh Session ID.
    pub(crate) fn new_session(self: &Arc<Self>) -> UdpSession {
        let id = self.next_id.fetch_add(1, Ordering::Relaxed);
        let (tx, rx) = mpsc::channel(SESSION_CHAN_SIZE);
        let receive=Arc::new(ReceiveMetrics::default());
        self.sessions.lock().unwrap().insert(id, SessionRoute{sender:tx,receive:receive.clone()});
        UdpSession {
            id,
            conn: self.conn.clone(),
            rx,
            defrag: Defragger::default(),
            mgr: Arc::downgrade(self),
            metrics:self.metrics.clone(),
            send_lock:Arc::new(tokio::sync::Mutex::new(())),
            receive,
        }
    }

    fn dispatch(&self, msg: UdpMessage) {
        let sessions = self.sessions.lock().unwrap();
        if let Some(route) = sessions.get(&msg.session_id) {
            // Non-blocking: drop the datagram if the session queue is full or
            // its receiver is gone (Go's `default:` case in udp.go).
            match route.sender.try_send(msg) {
                Ok(())=>{route.receive.high_water.fetch_max(SESSION_CHAN_SIZE-route.sender.capacity(),Ordering::Relaxed);},
                Err(mpsc::error::TrySendError::Full(_))=>{self.metrics.receive_drops.fetch_add(1,Ordering::Relaxed);route.receive.drops.fetch_add(1,Ordering::Relaxed);},
                Err(mpsc::error::TrySendError::Closed(_))=>{},
            }
        }
        // Unknown session — ignore (Go does the same).
    }

    fn remove(&self, id: u32) {
        self.sessions.lock().unwrap().remove(&id);
    }
    pub(crate) fn metrics(&self)->(u64,u64,u64,u64){(self.metrics.queued.load(Ordering::Relaxed),self.metrics.bytes.load(Ordering::Relaxed),self.metrics.wait_us.load(Ordering::Relaxed),self.metrics.receive_drops.load(Ordering::Relaxed))}
}

async fn receive_loop(conn: quinn::Connection, mgr: Weak<UdpSessionManager>) {
    let mut budget=ReceiveBudget::default();
    loop {
        match conn.read_datagram().await {
            Ok(data) => {
                if let Some(msg) = UdpMessage::parse(&data) {
                    match mgr.upgrade() {
                        Some(m) => m.dispatch(msg),
                        None => return, // manager dropped
                    }
                }
                // Invalid datagram — skip, like Go.
                budget.consumed().await;
            }
            Err(_) => return, // connection closed; senders drop, sessions see EOF
        }
    }
}

/// A UDP relay session. Send and receive datagrams to/from arbitrary
/// destination addresses through the proxy.
pub struct UdpSession {
    id: u32,
    conn: quinn::Connection,
    rx: mpsc::Receiver<UdpMessage>,
    defrag: Defragger,
    mgr: Weak<UdpSessionManager>,
    metrics:Arc<UdpMetrics>,
    send_lock:Arc<tokio::sync::Mutex<()>>,
    receive:Arc<ReceiveMetrics>,
}

/// A send-only handle. Receiving remains independent while outgoing capacity
/// is unavailable. Its owner must retain the UdpSession for reply dispatch.
#[derive(Clone)]
pub struct UdpSender { id:u32, conn:quinn::Connection,metrics:Arc<UdpMetrics>,send_lock:Arc<tokio::sync::Mutex<()>> }
impl UdpSender {
    async fn queue(&self,buffer:Vec<u8>)->Result<()> {
        let size=buffer.len() as u64;let started=std::time::Instant::now();
        let result=self.conn.send_datagram_wait(Bytes::from(buffer)).await;
        self.metrics.wait_us.fetch_add(started.elapsed().as_micros() as u64,Ordering::Relaxed);
        result.map_err(|_|Error::Protocol("UDP_SEND_FAILED".into()))?;
        self.metrics.queued.fetch_add(1,Ordering::Relaxed);self.metrics.bytes.fetch_add(size,Ordering::Relaxed);Ok(())
    }
    pub async fn send(&self,data:&[u8],addr:&str)->Result<()> {
        // The protocol reassembler tracks one packet ID per session. Keep all
        // fragments of one packet together even if send handles are cloned.
        let _packet=self.send_lock.lock().await;
        if data.len()>65507{return Err(Error::Protocol("UDP_PAYLOAD_TOO_LARGE".into()));}
        if addr.is_empty()||addr.len()>MAX_MESSAGE_LENGTH as usize{return Err(Error::Protocol("UDP_HEADER_INVALID".into()));}
        let mut msg=UdpMessage{session_id:self.id,packet_id:0,frag_id:0,frag_count:1,addr:addr.to_owned(),data:data.to_vec()};
        let max=self.conn.max_datagram_size().ok_or_else(||Error::Protocol("UDP_DATAGRAMS_UNAVAILABLE".into()))?;
        if msg.size()<=max{
            let mut buf=vec![0;msg.size()];let n=msg.serialize(&mut buf).ok_or_else(||Error::Protocol("UDP_HEADER_INVALID".into()))?;
            buf.truncate(n);self.queue(buf).await?;
        }else{
            let payload=max.saturating_sub(msg.header_size());
            if payload==0||data.len().div_ceil(payload)>255{return Err(Error::Protocol("UDP_FRAGMENT_LIMIT".into()));}
            msg.packet_id=rand::rng().random_range(1..=u16::MAX);
            let fragments=frag_udp_message(&msg,max);
            if fragments.is_empty(){return Err(Error::Protocol("UDP_FRAGMENT_LIMIT".into()));}
            for fragment in fragments{let mut buf=vec![0;fragment.size()];let n=fragment.serialize(&mut buf).ok_or_else(||Error::Protocol("UDP_HEADER_INVALID".into()))?;buf.truncate(n);self.queue(buf).await?;}
        }
        Ok(())
    }
}

impl UdpSession {
    pub fn receive_metrics(&self)->UdpReceiveMetrics {
        UdpReceiveMetrics{session_id:self.id,queued_datagrams:self.rx.len(),capacity:SESSION_CHAN_SIZE,high_water:self.receive.high_water.load(Ordering::Relaxed),dropped_datagrams:self.receive.drops.load(Ordering::Relaxed)}
    }
    pub fn sender(&self)->UdpSender {UdpSender{id:self.id,conn:self.conn.clone(),metrics:self.metrics.clone(),send_lock:self.send_lock.clone()}}
    /// The Session ID assigned by the client.
    pub fn id(&self) -> u32 {
        self.id
    }

    /// Send `data` to `addr` (`host:port`) through the proxy, fragmenting if the
    /// payload exceeds the current QUIC datagram limit.
    pub fn send(&self, data: &[u8], addr: &str) -> Result<()> {
        let msg = UdpMessage {
            session_id: self.id,
            packet_id: 0,
            frag_id: 0,
            frag_count: 1,
            addr: addr.to_string(),
            data: data.to_vec(),
        };

        // Fast path: try to send unfragmented.
        let mut buf = vec![0u8; msg.size()];
        if let Some(n) = msg.serialize(&mut buf) {
            match self.conn.send_datagram(Bytes::copy_from_slice(&buf[..n])) {
                Ok(()) => return Ok(()),
                Err(quinn::SendDatagramError::TooLarge) => { /* fall through to fragment */ }
                Err(e) => return Err(Error::Protocol(format!("send datagram: {e}"))),
            }
        }

        // Fragment to the current datagram limit and send each piece.
        let max = self
            .conn
            .max_datagram_size()
            .unwrap_or(MAX_DATAGRAM_FRAME_SIZE as usize);
        let mut frag = msg;
        frag.packet_id = rand::rng().random_range(1..=u16::MAX);
        for f in frag_udp_message(&frag, max) {
            let mut fbuf = vec![0u8; f.size()];
            if let Some(n) = f.serialize(&mut fbuf) {
                self.conn
                    .send_datagram(Bytes::copy_from_slice(&fbuf[..n]))
                    .map_err(|e| Error::Protocol(format!("send fragment: {e}")))?;
            }
        }
        Ok(())
    }

    /// Receive the next datagram, returning `(payload, source_addr)`.
    /// Returns an error once the session or connection is closed.
    pub async fn recv(&mut self) -> Result<(Vec<u8>, String)> {
        loop {
            let msg = self
                .rx
                .recv()
                .await
                .ok_or_else(|| Error::Protocol("udp session closed".into()))?;
            if let Some(full) = self.defrag.feed(msg) {
                return Ok((full.data, full.addr));
            }
            // Incomplete fragmented packet — wait for more.
        }
    }
}

impl Drop for UdpSession {
    fn drop(&mut self) {
        if let Some(mgr) = self.mgr.upgrade() {
            mgr.remove(self.id);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn incoming(session_id:u32,sequence:u32)->UdpMessage {
        UdpMessage{session_id,packet_id:0,frag_id:0,frag_count:1,addr:"burst.invalid:443".into(),data:sequence.to_be_bytes().to_vec()}
    }
    #[tokio::test(flavor="current_thread")]
    async fn cooperative_burst_dispatch_prevents_ready_consumer_starvation(){
        let(_client,_server,local,_remote)=crate::test_support::pair_with_datagram_buffer(Some(8192)).await;
        let manager=UdpSessionManager::new(local);
        // Same burst as the phone's 351 drops beyond a full 1024-message queue.
        let mut legacy=manager.new_session();let legacy_id=legacy.id();
        let legacy_consumer=tokio::spawn(async move{
            for sequence in 0..SESSION_CHAN_SIZE as u32{let(data,_)=legacy.recv().await.unwrap();assert_eq!(data,sequence.to_be_bytes());}
            legacy.receive_metrics()
        });
        for sequence in 0..1375 {manager.dispatch(incoming(legacy_id,sequence));}
        let legacy_stats=legacy_consumer.await.unwrap();
        assert_eq!(legacy_stats.dropped_datagrams,351);assert_eq!(legacy_stats.high_water,SESSION_CHAN_SIZE);
        assert!(!manager.sessions.lock().unwrap().contains_key(&legacy_id));

        let mut session=manager.new_session();let id=session.id();
        let receiver=tokio::spawn(async move {
            for sequence in 0..4096u32 {let(data,_)=session.recv().await.unwrap();assert_eq!(data,sequence.to_be_bytes());}
            session.receive_metrics()
        });
        let mut budget=ReceiveBudget::default();
        for sequence in 0..4096 {manager.dispatch(incoming(id,sequence));budget.consumed().await;}
        let metrics=tokio::time::timeout(std::time::Duration::from_secs(2),receiver).await.unwrap().unwrap();
        assert_eq!(metrics.dropped_datagrams,0);assert_eq!(metrics.queued_datagrams,0);
        assert!(metrics.high_water<=RECEIVE_BATCH);assert!(!manager.sessions.lock().unwrap().contains_key(&id));
    }
    #[tokio::test(flavor="current_thread")]
    async fn stalled_session_stays_bounded_without_blocking_another_receiver(){
        let(_client,_server,local,_remote)=crate::test_support::pair_with_datagram_buffer(Some(8192)).await;
        let manager=UdpSessionManager::new(local);let stalled=manager.new_session();
        let mut active=manager.new_session();let active_id=active.id();
        let consumer=tokio::spawn(async move{for sequence in 0..128u32{let(data,_)=active.recv().await.unwrap();assert_eq!(data,sequence.to_be_bytes());}active.receive_metrics()});
        let mut budget=ReceiveBudget::default();
        for sequence in 0..8192u32 {
            manager.dispatch(incoming(stalled.id(),sequence));
            if sequence%64==0{manager.dispatch(incoming(active_id,sequence/64));}
            budget.consumed().await;
        }
        let stats=stalled.receive_metrics();assert_eq!(stats.queued_datagrams,SESSION_CHAN_SIZE);
        assert_eq!(stats.dropped_datagrams,8192-SESSION_CHAN_SIZE as u64);
        assert_eq!(consumer.await.unwrap().dropped_datagrams,0);
        drop(stalled);assert!(manager.sessions.lock().unwrap().is_empty());
    }
    #[tokio::test]
    async fn legacy_quinn_send_reproduces_unsent_eviction(){
        let(_client,_server,local,remote)=crate::test_support::pair_with_datagram_buffer(Some(8192)).await;
        for id in 0..100u32{let mut data=vec![0x5a;1024];data[..4].copy_from_slice(&id.to_be_bytes());local.send_datagram(Bytes::from(data)).unwrap();}
        let mut ids=Vec::new();while let Ok(Ok(data))=tokio::time::timeout(std::time::Duration::from_millis(150),remote.read_datagram()).await{ids.push(u32::from_be_bytes(data[..4].try_into().unwrap()));}
        assert!(ids.len()<100,"legacy send unexpectedly retained every datagram");
        assert_eq!(ids.last(),Some(&99));assert!(!ids.contains(&0));
    }
    #[tokio::test]
    async fn waiting_sender_preserves_packets_fragments_and_parallel_reply(){
        let(_client,_server,local,remote)=crate::test_support::pair_with_datagram_buffer(Some(8192)).await;
        let manager=UdpSessionManager::new(local.clone());let mut session=manager.new_session();let sender=session.sender();
        let server=tokio::spawn(async move{
            let mut defrag=Defragger::default();let mut count=0;
            while count<8193{let bytes=remote.read_datagram().await.unwrap();if let Some(packet)=defrag.feed(UdpMessage::parse(&bytes).unwrap()){
                let id=u32::from_be_bytes(packet.data[..4].try_into().unwrap());assert_eq!(id,count);assert!(packet.data[4..].iter().all(|b|*b==0x5a));
                assert_eq!(packet.data.len(),if id==8192{12000}else{1024});
                let reply=UdpMessage{data:id.to_be_bytes().to_vec(),packet_id:0,frag_id:0,frag_count:1,..packet};let mut buf=vec![0;reply.size()];reply.serialize(&mut buf).unwrap();remote.send_datagram_wait(Bytes::from(buf)).await.unwrap();count+=1;
            }}
            tokio::time::sleep(std::time::Duration::from_millis(100)).await;
        });
        let receive=tokio::spawn(async move{for id in 0..8193u32{let(data,addr)=session.recv().await.unwrap();assert_eq!(addr,"example.invalid:443");assert_eq!(data,id.to_be_bytes());}});
        tokio::time::timeout(std::time::Duration::from_secs(10),async{
            for id in 0..8193u32{let mut data=vec![0x5a;if id==8192{12000}else{1024}];data[..4].copy_from_slice(&id.to_be_bytes());sender.send(&data,"example.invalid:443").await.unwrap();}
            receive.await.unwrap();server.await.unwrap();
        }).await.unwrap();
    }
    #[tokio::test]
    async fn cancelled_capacity_wait_does_not_send_or_block_another_session(){
        use std::{future::Future,pin::Pin,task::{Context,Poll}};
        let(_client,_server,local,remote)=crate::test_support::pair_with_datagram_buffer(Some(8192)).await;
        let manager=UdpSessionManager::new(local.clone());let first=manager.new_session();let second=manager.new_session();
        for _ in 0..9{local.send_datagram(Bytes::from(vec![0x5a;1024])).unwrap();}
        let sender=first.sender();let data=[0u8;1024];let mut cancelled=Box::pin(sender.send(&data,"cancel.invalid:1"));
        let waker=std::task::Waker::noop();let mut context=Context::from_waker(waker);let state=Pin::new(&mut cancelled).poll(&mut context);assert!(matches!(state,Poll::Pending),"capacity state: {state:?}");drop(cancelled);
        second.sender().send(b"new","new.invalid:1").await.unwrap();
        let mut found=false;while let Ok(Ok(data))=tokio::time::timeout(std::time::Duration::from_millis(150),remote.read_datagram()).await{if let Some(message)=UdpMessage::parse(&data){assert_ne!(message.addr,"cancel.invalid:1");if message.addr=="new.invalid:1"{assert_eq!(message.data,b"new");found=true;}}}
        assert!(found);assert!(local.close_reason().is_none());
    }
}
