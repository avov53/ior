//! VLESS and Vision framing only. TLS and cryptography stay in the pinned library.
use reality_rustls::Connection;
use std::{
    collections::VecDeque,
    io::{self, Read, Write},
    net::TcpStream,
};

/// Never read beyond one TLS record before Vision switches to raw inner TLS.
pub struct RecordSocket {
    pub tcp: TcpStream,
    raw: bool,
    handshake: bool,
    handshake_reads: u64,
    handshake_read_us: u64,
    header: Vec<u8>,
    remaining: usize,
    nonblocking:bool,
}
impl RecordSocket {
    pub fn new(tcp: TcpStream) -> Self {
        Self {
            tcp,
            raw: false,
            handshake: false,
            handshake_reads: 0,
            handshake_read_us: 0,
            header: Vec::new(),
            remaining: 0,
            nonblocking:false,
        }
    }
    /// Before sending VLESS data there can be no raw Vision payload to over-read.
    pub fn new_handshake(tcp: TcpStream) -> Self {
        Self {
            handshake: true,
            ..Self::new(tcp)
        }
    }
    pub fn finish_handshake(&mut self) {
        self.handshake = false;
    }
    pub fn handshake_metrics(&self) -> (u64, u64) {
        (self.handshake_reads, self.handshake_read_us)
    }
}
impl Read for RecordSocket {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        if buffer.is_empty() {
            return Ok(0);
        }
        if self.handshake {
            let started = std::time::Instant::now();
            let result = self.tcp.read(buffer);
            self.handshake_reads += 1;
            self.handshake_read_us = self
                .handshake_read_us
                .saturating_add(started.elapsed().as_micros().min(u64::MAX as u128) as u64);
            return result;
        }
        if self.nonblocking{self.tcp.set_nonblocking(false)?;self.nonblocking=false;}
        if self.raw {
            return self.tcp.read(buffer);
        }
        if self.remaining == 0 {
            while self.header.len() < 5 {
                let mut bytes = [0u8; 5];
                let missing = 5 - self.header.len();
                let n = self.tcp.read(&mut bytes[..missing])?;
                if n == 0 {
                    return Ok(0);
                }
                self.header.extend_from_slice(&bytes[..n]);
            }
            self.remaining = u16::from_be_bytes([self.header[3], self.header[4]]) as usize;
            if self.remaining > 18432 {
                return Err(io::ErrorKind::InvalidData.into());
            }
        }
        if !self.header.is_empty() {
            let n = buffer.len().min(self.header.len());
            buffer[..n].copy_from_slice(&self.header[..n]);
            self.header.drain(..n);
            return Ok(n);
        }
        let limit = buffer.len().min(self.remaining);
        let n = self.tcp.read(&mut buffer[..limit])?;
        self.remaining -= n;
        Ok(n)
    }
}

#[cfg(test)]
mod record_tests {
    use super::*;
    #[test]
    fn handshake_bulk_read_restores_vision_record_boundaries() {
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let mut peer = TcpStream::connect(listener.local_addr().unwrap()).unwrap();
        let (tcp, _) = listener.accept().unwrap();
        tcp.set_read_timeout(Some(std::time::Duration::from_secs(1)))
            .unwrap();
        let mut socket = RecordSocket::new_handshake(tcp);
        let records = [23, 3, 3, 0, 1, 7, 23, 3, 3, 0, 1, 8];
        peer.write_all(&records).unwrap();
        let mut received = [0; 12];
        socket.read_exact(&mut received).unwrap();
        assert_eq!(received, records);
        assert!(socket.handshake_metrics().0 > 0);
        socket.finish_handshake();
        peer.write_all(&records).unwrap();
        let mut buffer = [0; 64];
        assert_eq!(socket.read(&mut buffer).unwrap(), 5);
        assert_eq!(&buffer[..5], &records[..5]);
        assert_eq!(socket.read(&mut buffer).unwrap(), 1);
        assert_eq!(buffer[0], 7);
        assert!(socket.tcp.peek(&mut buffer).unwrap() > 0);
        assert_eq!(buffer[0], 23); // Next record remains outside the TLS reader.
    }
}
impl Write for RecordSocket {
    fn write(&mut self, data: &[u8]) -> io::Result<usize> {
        // Windows timed blocking sends can lose the partial-write count on timeout.
        // This socket has one owner. Writes stay nonblocking; reads retain the bounded
        // idle wait. WouldBlock preserves the exact encrypted tail for the next flush.
        if !self.handshake&&!self.nonblocking{self.tcp.set_nonblocking(true)?;self.nonblocking=true;}
        self.tcp.write(data)
    }
    fn flush(&mut self) -> io::Result<()> {
        self.tcp.flush()
    }
}

pub struct VlessCarrier<C: Connection> {
    pub tls: reality_util::StreamOwned<C, RecordSocket>,
    id: Option<[u8; 16]>,
    first_write: bool,
    response_done: bool,
    response: Vec<u8>,
    pending: Vec<u8>,
    output: VecDeque<u8>,
    first_block: bool,
    unpadding: bool,
    raw_read: bool,
    first_frame:Option<(Vec<u8>,usize,usize)>,
}
impl<C: Connection> VlessCarrier<C> {
    fn read_plaintext(&mut self,buffer:&mut [u8])->io::Result<usize>{
        loop{
            match self.tls.conn.reader().read(buffer){
                Err(error) if error.kind()==io::ErrorKind::WouldBlock=>(),
                result=>return result,
            }
            // Receiving must not depend on flushing a saturated outbound socket.
            self.tls.conn.read_tls(&mut self.tls.sock)?;
            self.tls.conn.process_new_packets().map_err(|error|io::Error::new(io::ErrorKind::InvalidData,error))?;
        }
    }
    pub fn transport(tls: reality_util::StreamOwned<C, RecordSocket>) -> Self {
        let mut carrier=Self::new(tls,None);carrier.response_done=true;carrier
    }
    pub fn new(tls: reality_util::StreamOwned<C, RecordSocket>, id: Option<[u8; 16]>) -> Self {
        Self {
            tls,
            id,
            first_write: true,
            response_done: false,
            response: Vec::new(),
            pending: Vec::new(),
            output: VecDeque::new(),
            first_block: true,
            unpadding: id.is_some(),
            raw_read: false,
            first_frame:None,
        }
    }
    fn ingest(&mut self, data: &[u8]) -> io::Result<()> {
        if !self.response_done {
            self.response.extend(data);
            if self.response.len() < 2 {
                return Ok(());
            }
            if self.response[0] != 0 {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "VLESS_RESPONSE_INVALID",
                ));
            }
            let n = 2 + self.response[1] as usize;
            if self.response.len() < n {
                return Ok(());
            }
            let remaining = self.response[n..].to_vec();
            self.response.clear();
            self.response_done = true;
            return self.ingest(&remaining);
        }
        if !self.unpadding {
            self.output.extend(data);
            return Ok(());
        }
        self.pending.extend(data);
        if self.first_block {
            if self.pending.len() < 16 {
                return Ok(());
            }
            if self.pending[..16] != self.id.unwrap() {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "VISION_IDENTITY_MISMATCH",
                ));
            }
            self.pending.drain(..16);
            self.first_block = false;
        }
        while self.pending.len() >= 5 {
            let command = self.pending[0];
            let content = u16::from_be_bytes([self.pending[1], self.pending[2]]) as usize;
            let padding = u16::from_be_bytes([self.pending[3], self.pending[4]]) as usize;
            if command > 2 {
                return Err(io::ErrorKind::InvalidData.into());
            }
            let total = 5 + content + padding;
            if self.pending.len() < total {
                return Ok(());
            }
            self.output.extend(&self.pending[5..5 + content]);
            self.pending.drain(..total);
            if command != 0 {
                self.unpadding = false;
                self.raw_read = command == 2;
                self.output.extend(self.pending.drain(..));
                if self.raw_read {
                    self.tls.sock.raw = true;
                }
                break;
            }
        }
        Ok(())
    }
}
impl<C: Connection> Read for VlessCarrier<C> {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        if buffer.is_empty() {
            return Ok(0);
        }
        while self.output.is_empty() {
            // Once framing has ended, no further bytes need the Vision decoder.
            // Keep the exact same TLS/raw reader, but avoid an intermediate queue.
            if self.response_done && !self.unpadding {
                // Preserve the historical per-read chunk size and queue memory.
                let length=buffer.len().min(16384);
                return if self.raw_read {
                    self.tls.sock.read(&mut buffer[..length])
                } else {
                    self.read_plaintext(&mut buffer[..length])
                };
            }
            let mut data = [0u8; 16384];
            let n = if self.raw_read {
                self.tls.sock.read(&mut data)?
            } else {
                self.read_plaintext(&mut data)?
            };
            if n == 0 {
                return Ok(0);
            }
            self.ingest(&data[..n])?;
        }
        let n = buffer.len().min(self.output.len());
        drain_output(&mut self.output, &mut buffer[..n]);
        Ok(n)
    }
}

fn drain_output(output: &mut VecDeque<u8>, buffer: &mut [u8]) {
    let (first, second) = output.as_slices();
    let first_len = first.len().min(buffer.len());
    buffer[..first_len].copy_from_slice(&first[..first_len]);
    let second_len = buffer.len() - first_len;
    buffer[first_len..].copy_from_slice(&second[..second_len]);
    output.drain(..buffer.len());
}

#[cfg(test)]
mod carrier_tests {
    use super::*;
    fn carrier() -> (VlessCarrier<reality_rustls::ClientConnection>, TcpStream) {
        let listener=std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let peer=TcpStream::connect(listener.local_addr().unwrap()).unwrap();
        let (tcp,_)=listener.accept().unwrap();
        tcp.set_read_timeout(Some(std::time::Duration::from_secs(5))).unwrap();
        let config=reality_rustls::ClientConfig::builder(std::sync::Arc::new(reality_provider::DEFAULT_TLS13_PROVIDER.clone()))
            .with_root_certificates(reality_rustls::RootCertStore::empty()).with_no_client_auth().unwrap();
        let conn=std::sync::Arc::new(config).connect("test.invalid".try_into().unwrap()).build().unwrap();
        (VlessCarrier::new(reality_util::StreamOwned::new(conn,RecordSocket::new(tcp)),Some([42;16])),peer)
    }
    fn transition(c:&mut VlessCarrier<reality_rustls::ClientConnection>,command:u8,payload:&[u8]) {
        let mut frame=vec![0,0];frame.extend([42;16]);frame.push(command);
        frame.extend((payload.len() as u16).to_be_bytes());frame.extend(3u16.to_be_bytes());frame.extend(payload);frame.extend([0;3]);
        for chunk in frame.chunks(3){c.ingest(chunk).unwrap();}
    }
    #[test]fn wrapped_output_drains_without_reordering() {
        let mut q:VecDeque<_>=(0u8..30).collect();q.drain(..20);q.extend(30u8..50);
        let expected:Vec<_>=q.iter().copied().collect();let mut result=vec![0;expected.len()];
        drain_output(&mut q,&mut result);assert_eq!(result,expected);assert!(q.is_empty());
    }
    #[test]fn fragmented_vision_boundary_preserves_buffered_tail_then_raw_bytes() {
        let (mut c,mut peer)=carrier();transition(&mut c,2,b"first");
        peer.write_all(b"second").unwrap();peer.shutdown(std::net::Shutdown::Write).unwrap();
        let mut first=[0;2];c.read_exact(&mut first).unwrap();assert_eq!(&first,b"fi");
        let mut rest=vec![];c.read_to_end(&mut rest).unwrap();assert_eq!(&rest,b"rstsecond");
        let (mut c,_)=carrier();transition(&mut c,1,b"tls");assert!(!c.raw_read);assert!(!c.unpadding);
    }
    #[test]fn invalid_vision_never_enables_direct_raw_read() {
        for invalid_identity in [false,true] {
            let(mut c,_)=carrier();let mut frame=vec![0,0];frame.extend([if invalid_identity{43}else{42};16]);
            frame.extend([3,0,1,0,0,7]);assert!(c.ingest(&frame).is_err());assert!(!c.raw_read);assert!(c.unpadding);
        }
    }
    #[test]
    #[ignore="Explicit release loopback throughput comparison, no external network"]
    fn compare_legacy_and_direct_raw_64_mib() {
        let mut old=vec![];let mut new=vec![];
        for round in 0..3 {for legacy in if round%2==0 {[true,false]}else{[false,true]} {
            let (mut c,mut peer)=carrier();transition(&mut c,2,b"");
            let sender=std::thread::spawn(move||{let chunk=vec![0x5a;64*1024];for _ in 0..1024{peer.write_all(&chunk).unwrap();}peer.shutdown(std::net::Shutdown::Write).unwrap();});
            let mut result=vec![0;64*1024*1024];let start=std::time::Instant::now();let mut pos=0;
            while pos<result.len() {
                let target=&mut result[pos..(pos+64*1024).min(64*1024*1024)];
                let n=if legacy {
                    if c.output.is_empty(){let mut data=[0;16384];let n=c.tls.sock.read(&mut data).unwrap();c.output.extend(&data[..n]);}
                    let n=target.len().min(c.output.len());for b in &mut target[..n]{*b=c.output.pop_front().unwrap();}n
                }else{c.read(target).unwrap()};
                assert!(n>0);pos+=n;
            }
            let elapsed=start.elapsed().as_micros();assert!(result.iter().all(|b|*b==0x5a));sender.join().unwrap();
            if legacy{old.push(elapsed);}else{new.push(elapsed);}
        }}
        old.sort();new.sort();println!("VISION_LOOPBACK_64M legacyUs={:?} directUs={:?} medianImprovementPercent={:.1}",old,new,100.0*(1.0-new[1] as f64/old[1] as f64));
    }
}
impl<C: Connection> Write for VlessCarrier<C> {
    fn write(&mut self, data: &[u8]) -> io::Result<usize> {
        if data.is_empty(){return Ok(0);}
        if self.first_write && self.id.is_some() {
            if self.first_frame.is_none(){
            let n = data.len().min(8192);
            let mut frame = self.id.unwrap().to_vec();
            // END keeps this direction in certificate-verified outer TLS.
            frame.push(1);
            frame.extend((n as u16).to_be_bytes());
            frame.extend(64u16.to_be_bytes());
            frame.extend(&data[..n]);
            let mut padding = [0u8; 64];
            getrandom::getrandom(&mut padding).map_err(io::Error::other)?;
            frame.extend(padding);
            self.first_frame=Some((frame,0,n));
            }
            let(frame,offset,n)=self.first_frame.as_mut().unwrap();
            while *offset<frame.len(){let written=self.tls.conn.writer().write(&frame[*offset..])?;if written==0{return Err(io::ErrorKind::WouldBlock.into());}*offset+=written;}
            let n=*n;self.first_frame=None;
            self.first_write = false;
            Ok(n)
        } else {
            self.first_write = false;
            // rustls' bounded plaintext writer returns zero when its buffer is full.
            // The worker must flush/retry, rather than tear down a live carrier.
            match self.tls.conn.writer().write(data)?{0=>Err(io::ErrorKind::WouldBlock.into()),n=>Ok(n)}
        }
    }
    fn flush(&mut self) -> io::Result<()> {
        self.tls.conn.writer().flush()?;
        while self.tls.conn.wants_write(){if self.tls.conn.write_tls(&mut self.tls.sock)?==0{return Err(io::ErrorKind::WriteZero.into());}}
        self.tls.sock.flush()
    }
}
