//! Counts payload bytes and identifies the side/operation that failed.
use std::{
    future::Future,
    io,
    pin::Pin,
    task::{Context, Poll},
    time::Instant,
};
use tokio::io::{AsyncRead, AsyncWrite, ReadBuf};
pub struct Observed<S> {
    resource_domain: Option<String>,
    inner: S,
    side: &'static str,
    remote: std::net::SocketAddr,
    read: u64,
    written: u64,
    started: Instant,
    first_write: Option<Instant>,
    last_read: Option<Instant>,
    last_write: Option<Instant>,
    read_pending: Option<Instant>,
    write_pending: Option<Instant>,
    pending_tick: Pin<Box<tokio::time::Sleep>>,
    last_wait_log: Option<Instant>,
    wait_count: u64,
    read_wait_us: u64,
    write_wait_us: u64,
    read_eof: bool,
    write_closed: bool,
    names: Option<std::sync::Arc<crate::tun_names::NameCache>>,
    last_resource_sample: Instant,
}
impl<S> Observed<S> {
    pub fn new(inner: S, side: &'static str, remote: std::net::SocketAddr) -> Self {
        Self {
            resource_domain: None,
            inner,
            side,
            remote,
            read: 0,
            written: 0,
            started: Instant::now(),
            first_write: None,
            last_read: None,
            last_write: None,
            read_pending: None,
            write_pending: None,
            pending_tick: Box::pin(tokio::time::sleep(std::time::Duration::from_secs(10))),
            last_wait_log: None,
            wait_count: 0,
            read_wait_us: 0,
            write_wait_us: 0,
            read_eof: false,
            write_closed: false,
            names: None,
            last_resource_sample: Instant::now(),
        }
    }
    pub fn with_names(mut self, names: std::sync::Arc<crate::tun_names::NameCache>) -> Self {
        self.names = Some(names);
        self
    }
    pub fn with_resource_domain(mut self,host:Option<&str>)->Self {self.resource_domain=host.filter(|name|crate::resource_diagnostics::enabled(name)).map(str::to_owned);self}
    fn resource_sample(&mut self) {
        if self.side!="transport" || self.last_resource_sample.elapsed().as_secs()<5 {return;}
        let Some(host)=&self.resource_domain else{return;};
        crate::resource_diagnostics::emit(host,"RESOURCE_TCP_PROGRESS",serde_json::json!({"stage":"relay","transport":"tcp","side":"transport","destination":self.remote.to_string(),"durationMs":self.started.elapsed().as_millis() as u64,"sentBytes":self.written,"receivedBytes":self.read,"lastReceivedAgoMs":self.last_read.map(|at|at.elapsed().as_millis() as u64),"lastSentAgoMs":self.last_write.map(|at|at.elapsed().as_millis() as u64),"sendWaitUs":self.write_wait_us+self.write_pending.map(|at|at.elapsed().as_micros() as u64).unwrap_or(0),"readWaitUs":self.read_wait_us+self.read_pending.map(|at|at.elapsed().as_micros() as u64).unwrap_or(0)}));
        self.last_resource_sample=Instant::now();
    }
    fn progress(&self, code: &str, operation: &str, duration: u64) {
        crate::trace::emit(serde_json::json!({"stage":"relay","code":code,"side":self.side,
            "operation":operation,"destination":self.remote.to_string(),
            "destinationName":self.names.as_ref().and_then(|names| names.lookup(self.remote.ip())),
            "sentBytes":if self.side=="application"{self.read}else{self.written},
            "receivedBytes":if self.side=="application"{self.written}else{self.read},
            "durationMs":duration,"waitCount":self.wait_count,"readWaitUs":self.read_wait_us,"sendWaitUs":self.write_wait_us,
            "lastReceivedAgoMs":(if self.side=="application"{self.last_write}else{self.last_read}).map(|at|at.elapsed().as_millis() as u64),"lastSentAgoMs":(if self.side=="application"{self.last_read}else{self.last_write}).map(|at|at.elapsed().as_millis() as u64),
            "nameSource":"dns_cache_observation","readEof":self.read_eof,"writeClosed":self.write_closed}));
    }
    fn pending(&mut self, cx: &mut Context<'_>, operation: &str) {
        if self.side != "transport" { return; }
        if self.pending_tick.as_mut().poll(cx).is_ready() {
            self.resource_sample();
            let elapsed=self.read_pending.into_iter().chain(self.write_pending).map(|t|t.elapsed().as_millis() as u64).max().unwrap_or(0);
            if elapsed >= 1000 { self.progress("TUN_TCP_PENDING", operation, elapsed); }
            self.pending_tick.as_mut().reset(tokio::time::Instant::now()+std::time::Duration::from_secs(10));
            let _=self.pending_tick.as_mut().poll(cx);
        }
    }
    fn first(&self, read: bool) {
        if let Some(host)=&self.resource_domain{crate::resource_diagnostics::emit(host,"RESOURCE_TCP_FIRST_BYTE",serde_json::json!({"stage":"relay","side":self.side,"operation":if read{"read"}else{"write"},"durationMs":self.started.elapsed().as_millis() as u64}));}
        if self.side == "transport" {
            crate::trace::emit(
                serde_json::json!({"stage":"relay","code":if read{"TUN_TCP_FIRST_REMOTE_BYTE"}else{"TUN_TCP_FIRST_SENT_BYTE"},"destination":self.remote.to_string(),"durationMs":self.started.elapsed().as_millis() as u64,"afterFirstSendMs":if read {self.first_write.map(|at|at.elapsed().as_millis() as u64)}else{None}}),
            );
        }
    }
    fn resumed(&mut self, pending: Option<Instant>, operation: &str) {
        if self.side == "transport" {
            if let Some(start) = pending {
                let elapsed=start.elapsed().as_micros().min(u64::MAX as u128) as u64;
                if operation=="read" {self.read_wait_us=self.read_wait_us.saturating_add(elapsed);}
                else {self.write_wait_us=self.write_wait_us.saturating_add(elapsed);}
                if start.elapsed().as_millis() >= 100 {
                    self.wait_count += 1;
                    if self.last_wait_log.is_none_or(|last|last.elapsed().as_secs()>=5) {
                        self.progress("TUN_TCP_IO_WAIT",operation,start.elapsed().as_millis() as u64);
                        self.last_wait_log=Some(Instant::now());
                    }
                }
            }
        }
    }
    fn failed(&self, operation: &str, error: &io::Error) {
        if let Some(host)=&self.resource_domain{crate::resource_diagnostics::emit(host,"RESOURCE_TCP_ERROR",serde_json::json!({"stage":"relay","side":self.side,"operation":operation,"kind":format!("{:?}",error.kind()),"sentBytes":if self.side=="application"{self.read}else{self.written},"receivedBytes":if self.side=="application"{self.written}else{self.read}}));}
        let local = self.side == "application";
        let closed = local && matches!(error.kind(),io::ErrorKind::BrokenPipe|io::ErrorKind::ConnectionReset|io::ErrorKind::ConnectionAborted);
        crate::trace::emit(
            serde_json::json!({"stage":"relay","code":if closed{"TUN_TCP_APPLICATION_CLOSED"}else{"TUN_TCP_IO_FAILED"},"side":self.side,"operation":operation,"classification":if closed{"APPLICATION_CONNECTION_CLOSED"}else if local{"APPLICATION_IO_FAILURE"}else{"TRANSPORT_IO_FAILURE"},
            "direction":if local==(operation=="read"){"local_to_remote"}else{"remote_to_local"},
            "sentBytes":if local{self.read}else{self.written},"receivedBytes":if local{self.written}else{self.read},
            "destination":self.remote.to_string(),"kind":format!("{:?}",error.kind())}),
        );
    }
}
impl<S> Drop for Observed<S> {
    fn drop(&mut self) {
        if let Some(host)=&self.resource_domain{crate::resource_diagnostics::emit(host,"RESOURCE_TCP_SUMMARY",serde_json::json!({"stage":"relay","side":self.side,"durationMs":self.started.elapsed().as_millis() as u64,"sentBytes":if self.side=="application"{self.read}else{self.written},"receivedBytes":if self.side=="application"{self.written}else{self.read},"readEof":self.read_eof,"writeClosed":self.write_closed}));}
        self.progress("TUN_TCP_RELAY_SUMMARY","drop",self.started.elapsed().as_millis() as u64);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    #[tokio::test]
    async fn counts_partial_reads_writes_and_preserves_half_close() {
        let (stream, mut peer) = tokio::io::duplex(16);
        let mut observed = Observed::new(stream, "application", "127.0.0.1:443".parse().unwrap());
        peer.write_all(b"request").await.unwrap();
        let mut bytes = [0; 3];
        observed.read_exact(&mut bytes).await.unwrap();
        assert_eq!(&bytes, b"req");
        assert_eq!(observed.read, 3);
        observed.write_all(b"reply").await.unwrap();
        observed.shutdown().await.unwrap();
        let mut response = Vec::new();
        peer.read_to_end(&mut response).await.unwrap();
        assert_eq!(response, b"reply");
        assert_eq!(observed.written, 5);
        let mut remaining = [0; 4];
        observed.read_exact(&mut remaining).await.unwrap();
        assert_eq!(&remaining, b"uest");
        assert_eq!(observed.read, 7);
    }
    #[tokio::test]
    async fn broken_pipe_keeps_successful_byte_count() {
        let (stream, peer) = tokio::io::duplex(16);
        let mut observed = Observed::new(stream, "transport", "127.0.0.1:443".parse().unwrap());
        observed.write_all(b"sent").await.unwrap();
        drop(peer);
        assert_eq!(
            observed.write_all(b"failed").await.unwrap_err().kind(),
            io::ErrorKind::BrokenPipe
        );
        assert_eq!(observed.written, 4);
    }
}
impl<S: AsyncRead + Unpin> AsyncRead for Observed<S> {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        let before = buf.filled().len();
        let result = Pin::new(&mut self.inner).poll_read(cx, buf);
        match &result {
            Poll::Ready(Ok(())) => {
                let n = (buf.filled().len() - before) as u64;
                if n > 0 && self.read == 0 {
                    self.first(true);
                }
                self.read += n;
                if n>0 {self.last_read=Some(Instant::now());}
                if n==0 && buf.remaining()>0 && !self.read_eof {
                    self.read_eof=true;
                    self.progress("TUN_TCP_READ_EOF","read",self.started.elapsed().as_millis() as u64);
                }
                let pending = self.read_pending.take();
                self.resumed(pending, "read");
                self.resource_sample();
            }
            Poll::Ready(Err(error)) => self.failed("read", error),
            Poll::Pending => {
                self.read_pending.get_or_insert_with(Instant::now);
                self.pending(cx,"read");
            }
        };
        result
    }
}
impl<S: AsyncWrite + Unpin> AsyncWrite for Observed<S> {
    fn poll_write(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &[u8],
    ) -> Poll<io::Result<usize>> {
        let result = Pin::new(&mut self.inner).poll_write(cx, buf);
        match &result {
            Poll::Ready(Ok(n)) => {
                if *n > 0 && self.written == 0 {
                    self.first(false);
                }
                self.written += *n as u64;
                if *n>0 {let at=Instant::now();self.first_write.get_or_insert(at);self.last_write=Some(at);}
                let pending = self.write_pending.take();
                self.resumed(pending, "write");
                self.resource_sample();
            }
            Poll::Ready(Err(error)) => self.failed("write", error),
            Poll::Pending => {
                self.write_pending.get_or_insert_with(Instant::now);
                self.pending(cx,"write");
            }
        };
        result
    }
    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        let result = Pin::new(&mut self.inner).poll_flush(cx);
        if let Poll::Ready(Err(error)) = &result {
            self.failed("flush", error)
        }
        result
    }
    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        let result = Pin::new(&mut self.inner).poll_shutdown(cx);
        if matches!(result,Poll::Ready(Ok(()))) && !self.write_closed {
            self.write_closed=true;
            self.progress("TUN_TCP_WRITE_CLOSED","shutdown",self.started.elapsed().as_millis() as u64);
        }
        if let Poll::Ready(Err(error)) = &result {
            self.failed("shutdown", error)
        }
        result
    }
}
