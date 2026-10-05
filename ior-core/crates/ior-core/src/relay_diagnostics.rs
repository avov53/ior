//! Counts payload bytes and identifies the side/operation that failed.
use std::{
    io,
    pin::Pin,
    task::{Context, Poll},
    time::Instant,
};
use tokio::io::{AsyncRead, AsyncWrite, ReadBuf};
pub struct Observed<S> {
    inner: S,
    side: &'static str,
    remote: std::net::SocketAddr,
    read: u64,
    written: u64,
    started: Instant,
    read_pending: Option<Instant>,
    write_pending: Option<Instant>,
}
impl<S> Observed<S> {
    pub fn new(inner: S, side: &'static str, remote: std::net::SocketAddr) -> Self {
        Self {
            inner,
            side,
            remote,
            read: 0,
            written: 0,
            started: Instant::now(),
            read_pending: None,
            write_pending: None,
        }
    }
    fn first(&self, read: bool) {
        if self.side == "transport" {
            crate::trace::emit(
                serde_json::json!({"stage":"relay","code":if read{"TUN_TCP_FIRST_REMOTE_BYTE"}else{"TUN_TCP_FIRST_SENT_BYTE"},"destination":self.remote.to_string(),"durationMs":self.started.elapsed().as_millis() as u64}),
            );
        }
    }
    fn resumed(&self, pending: Option<Instant>, operation: &str) {
        if self.side == "transport" {
            if let Some(start) = pending {
                if start.elapsed().as_millis() >= 100 {
                    crate::trace::emit(
                        serde_json::json!({"stage":"relay","code":"TUN_TCP_IO_WAIT","operation":operation,"durationMs":start.elapsed().as_millis() as u64,"destination":self.remote.to_string()}),
                    );
                }
            }
        }
    }
    fn failed(&self, operation: &str, error: &io::Error) {
        let local = self.side == "application";
        crate::trace::emit(
            serde_json::json!({"stage":"relay","code":"TUN_TCP_IO_FAILED","side":self.side,"operation":operation,"classification":if local{"APPLICATION_IO_FAILURE"}else{"TRANSPORT_IO_FAILURE"},
            "direction":if local==(operation=="read"){"local_to_remote"}else{"remote_to_local"},
            "sentBytes":if local{self.read}else{self.written},"receivedBytes":if local{self.written}else{self.read},
            "destination":self.remote.to_string(),"kind":format!("{:?}",error.kind())}),
        );
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
                let pending = self.read_pending.take();
                self.resumed(pending, "read");
            }
            Poll::Ready(Err(error)) => self.failed("read", error),
            Poll::Pending => {
                self.read_pending.get_or_insert_with(Instant::now);
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
                let pending = self.write_pending.take();
                self.resumed(pending, "write");
            }
            Poll::Ready(Err(error)) => self.failed("write", error),
            Poll::Pending => {
                self.write_pending.get_or_insert_with(Instant::now);
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
        if let Poll::Ready(Err(error)) = &result {
            self.failed("shutdown", error)
        }
        result
    }
}
