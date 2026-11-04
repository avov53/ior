use std::{
    io,
    pin::Pin,
    task::{Context, Poll},
};

use tokio::io::{AsyncRead, AsyncWrite, ReadBuf};

use crate::protocol::{parse_tcp_response, TcpResponseParse};

/// A proxied TCP connection over a QUIC bidirectional stream.
///
/// Implements [`AsyncRead`] + [`AsyncWrite`] so it drops straight into
/// `tokio::io::copy_bidirectional`.
///
/// Fast-open: [`crate::HysteriaClient::tcp_connect`] returns immediately after
/// writing the request, without blocking on the server's `TCPResponse`. The
/// response header is parsed and stripped lazily on the first `poll_read`, so
/// application data (e.g. an HTTP request) can flow out right away. This avoids
/// a round-trip stall — and, with servers that defer their response until they
/// have upstream data, a multi-second deadlock.
pub struct DuplexStream {
    send: quinn::SendStream,
    recv: quinn::RecvStream,
    response_done: bool,
    scratch: Vec<u8>,
    leftover: Vec<u8>,
    leftover_pos: usize,
}

impl DuplexStream {
    pub(crate) fn new(send: quinn::SendStream, recv: quinn::RecvStream) -> Self {
        Self {
            send,
            recv,
            response_done: false,
            scratch: Vec::new(),
            leftover: Vec::new(),
            leftover_pos: 0,
        }
    }

    /// Drive the TCPResponse header to completion (used when fast-open is off,
    /// so a server rejection surfaces from `tcp_connect` rather than first read).
    pub(crate) async fn establish(&mut self) -> io::Result<()> {
        std::future::poll_fn(|cx| Pin::new(&mut *self).poll_establish(cx)).await
    }

    /// Phase 1: consume the TCPResponse header before yielding any app data.
    fn poll_establish(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        let me = &mut *self;
        while !me.response_done {
            match parse_tcp_response(&me.scratch) {
                TcpResponseParse::Done { ok, consumed } => {
                    if !ok {
                        return Poll::Ready(Err(io::Error::new(
                            io::ErrorKind::ConnectionRefused,
                            "hysteria2: server rejected tcp_connect",
                        )));
                    }
                    me.leftover = me.scratch.split_off(consumed);
                    me.scratch = Vec::new();
                    me.response_done = true;
                    break;
                }
                TcpResponseParse::Invalid => {
                    return Poll::Ready(Err(io::Error::new(
                        io::ErrorKind::InvalidData,
                        "hysteria2: malformed TCPResponse (length guard exceeded)",
                    )));
                }
                TcpResponseParse::NeedMore => {
                    let mut tmp = [0u8; 512];
                    let mut rb = ReadBuf::new(&mut tmp);
                    match Pin::new(&mut me.recv).poll_read(cx, &mut rb) {
                        Poll::Ready(Ok(())) => {
                            let filled = rb.filled();
                            if filled.is_empty() {
                                return Poll::Ready(Err(io::Error::new(
                                    io::ErrorKind::UnexpectedEof,
                                    "hysteria2: stream closed before TCPResponse",
                                )));
                            }
                            me.scratch.extend_from_slice(filled);
                        }
                        Poll::Ready(Err(e)) => return Poll::Ready(Err(e)),
                        Poll::Pending => return Poll::Pending,
                    }
                }
            }
        }
        Poll::Ready(Ok(()))
    }
}

impl AsyncRead for DuplexStream {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        if buf.remaining() == 0 { return Poll::Ready(Ok(())); }
        // Phase 1: ensure the TCPResponse header has been consumed.
        if !self.response_done {
            match self.as_mut().poll_establish(cx) {
                Poll::Ready(Ok(())) => {}
                Poll::Ready(Err(e)) => return Poll::Ready(Err(e)),
                Poll::Pending => return Poll::Pending,
            }
        }
        let me = &mut *self;

        // Phase 2: drain app data that arrived in the same batch as the header.
        if me.leftover_pos < me.leftover.len() {
            let rest = &me.leftover[me.leftover_pos..];
            let n = rest.len().min(buf.remaining());
            buf.put_slice(&rest[..n]);
            me.leftover_pos += n;
            return Poll::Ready(Ok(()));
        }

        // Phase 3: pass through.
        Pin::new(&mut me.recv).poll_read(cx, buf)
    }
}

impl AsyncWrite for DuplexStream {
    fn poll_write(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &[u8],
    ) -> Poll<io::Result<usize>> {
        Pin::new(&mut self.send)
            .poll_write(cx, buf)
            .map_err(io::Error::other)
    }

    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.send).poll_flush(cx)
    }

    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.send).poll_shutdown(cx)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn upload_backpressure_preserves_parallel_stream(){
        use tokio::io::{AsyncReadExt,AsyncWriteExt};
        let (_client,_server,local,remote)=crate::test_support::pair().await;
        let server=tokio::spawn(async move{
            let(mut send,mut recv)=remote.accept_bi().await.unwrap();
            let other=remote.clone();let small=tokio::spawn(async move{let(mut reply,mut request)=other.accept_bi().await.unwrap();assert_eq!(request.read_to_end(64).await.unwrap(),b"small");reply.write_all(&[0,0,0]).await.unwrap();reply.write_all(b"ok").await.unwrap();reply.finish().unwrap();});
            send.write_all(&[0,0,0]).await.unwrap();tokio::time::sleep(std::time::Duration::from_millis(100)).await;
            let data=recv.read_to_end(33*1024*1024).await.unwrap();assert_eq!(data.len(),32*1024*1024);assert!(data.iter().all(|b|*b==0x5a));send.write_all(b"done").await.unwrap();send.finish().unwrap();small.await.unwrap();tokio::time::sleep(std::time::Duration::from_millis(100)).await;
        });
        let(send,recv)=local.open_bi().await.unwrap();let mut stream=DuplexStream::new(send,recv);
        stream.write_all(&[0x5a;4096]).await.unwrap();
        let other=local.clone();let small=tokio::spawn(async move{let(send,recv)=other.open_bi().await.unwrap();let mut s=DuplexStream::new(send,recv);s.write_all(b"small").await.unwrap();s.shutdown().await.unwrap();let mut reply=Vec::new();s.read_to_end(&mut reply).await.unwrap();assert_eq!(reply,b"ok");});
        tokio::time::timeout(std::time::Duration::from_secs(20),async{stream.write_all(&vec![0x5a;32*1024*1024-4096]).await.unwrap();stream.shutdown().await.unwrap();let mut reply=Vec::new();stream.read_to_end(&mut reply).await.unwrap();assert_eq!(reply,b"done");small.await.unwrap();}).await.unwrap();server.await.unwrap();
    }
    use tokio::io::{AsyncReadExt,AsyncWriteExt};
    use std::time::Duration;

    #[tokio::test]
    async fn upgraded_bidirectional_stream_survives_pause_and_reuse(){
        let (_client,_server,local,remote)=crate::test_support::pair().await;
        let server=tokio::spawn(async move{
            let(mut send,mut recv)=remote.accept_bi().await.unwrap();
            let expected=b"GET / HTTP/1.1\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n";
            let mut request=vec![0;expected.len()];recv.read_exact(&mut request).await.unwrap();assert_eq!(request,expected);
            send.write_all(&[0,0,0]).await.unwrap();send.write_all(b"HTTP/1.1 101 Switching Protocols\r\n\r\n").await.unwrap();
            // Opaque application frames continue on the same QUIC stream after idle.
            for _ in 0..2{let mut frame=[0;4];recv.read_exact(&mut frame).await.unwrap();assert_eq!(frame,[0x82,2,7,8]);send.write_all(&frame).await.unwrap();}
            send.finish().unwrap();tokio::time::sleep(Duration::from_millis(100)).await;
        });
        let(send,recv)=local.open_bi().await.unwrap();let mut stream=DuplexStream::new(send,recv);
        stream.write_all(b"GET / HTTP/1.1\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n").await.unwrap();
        let mut reply=[0;36];stream.read_exact(&mut reply).await.unwrap();assert_eq!(&reply,b"HTTP/1.1 101 Switching Protocols\r\n\r\n");
        for _ in 0..2{tokio::time::sleep(Duration::from_millis(250)).await;stream.write_all(&[0x82,2,7,8]).await.unwrap();stream.flush().await.unwrap();let mut frame=[0;4];tokio::time::timeout(Duration::from_secs(2),stream.read_exact(&mut frame)).await.unwrap().unwrap();assert_eq!(frame,[0x82,2,7,8]);}
        stream.shutdown().await.unwrap();server.await.unwrap();assert!(local.close_reason().is_none());
    }

    #[tokio::test]
    async fn fragmented_delayed_response_preserves_half_close_and_empty_read() {
        let (_client,_server,local,remote)=crate::test_support::pair().await;
        let server=tokio::spawn(async move {
            let (mut send,mut recv)=remote.accept_bi().await.unwrap();
            assert_eq!(recv.read_to_end(64).await.unwrap(),b"request");
            for byte in [0u8,0,0] {send.write_all(&[byte]).await.unwrap();tokio::time::sleep(Duration::from_millis(10)).await;}
            send.write_all(b"final payload").await.unwrap();send.finish().unwrap();
            tokio::time::sleep(Duration::from_millis(100)).await;
        });
        let (send,recv)=local.open_bi().await.unwrap();let mut stream=DuplexStream::new(send,recv);
        assert_eq!(stream.read(&mut []).await.unwrap(),0);assert!(!stream.response_done);
        stream.write_all(b"request").await.unwrap();stream.shutdown().await.unwrap();
        let mut answer=Vec::new();tokio::time::timeout(Duration::from_secs(2),stream.read_to_end(&mut answer)).await.unwrap().unwrap();
        assert_eq!(answer,b"final payload");assert!(local.close_reason().is_none());server.await.unwrap();
    }

    #[tokio::test]
    async fn reset_cancel_and_parallel_bulk_do_not_block_new_streams() {
        let (_client,_server,local,remote)=crate::test_support::pair().await;
        let server=tokio::spawn(async move {
            let mut jobs=tokio::task::JoinSet::new();
            for _ in 0..5 {
                let (mut send,mut recv)=remote.accept_bi().await.unwrap();
                jobs.spawn(async move {
                    let mut tag=[0];if recv.read_exact(&mut tag).await.is_err(){return;}
                    if tag[0]==b'R' {send.reset(7u32.into()).unwrap();return;}
                    if tag[0]==b'C' {let _=recv.read_to_end(16).await;return;}
                    send.write_all(&[0,0,0]).await.unwrap();
                    if tag[0]==b'L' {for _ in 0..4096 {send.write_all(&[0x5a;16384]).await.unwrap();}}
                    else {send.write_all(b"small").await.unwrap();}
                    send.finish().unwrap();
                });
            }
            while let Some(job)=jobs.join_next().await {job.unwrap();}
            tokio::time::sleep(Duration::from_millis(100)).await;
        });
        let open=||async {let (send,recv)=local.open_bi().await.unwrap();DuplexStream::new(send,recv)};
        let mut bad=open().await;bad.write_all(b"R").await.unwrap();assert!(bad.read(&mut [0;1]).await.is_err());drop(bad);
        let mut cancelled=open().await;cancelled.write_all(b"C").await.unwrap();drop(cancelled);
        let mut bulk=open().await;bulk.write_all(b"L").await.unwrap();bulk.shutdown().await.unwrap();
        let download=tokio::spawn(async move {
            let mut buffer=[0u8;8192];let mut total=0;
            loop {let n=bulk.read(&mut buffer).await.unwrap();if n==0 {break;}assert!(buffer[..n].iter().all(|v|*v==0x5a));total+=n;}
            assert_eq!(total,64*1024*1024);
        });
        for _ in 0..2 {
            tokio::time::sleep(Duration::from_millis(50)).await;
            let mut small=open().await;small.write_all(b"S").await.unwrap();small.shutdown().await.unwrap();
            let mut answer=Vec::new();tokio::time::timeout(Duration::from_secs(2),small.read_to_end(&mut answer)).await.unwrap().unwrap();assert_eq!(answer,b"small");
        }
        tokio::time::timeout(Duration::from_secs(20),download).await.unwrap().unwrap();
        assert!(local.close_reason().is_none());server.await.unwrap();
    }
}
