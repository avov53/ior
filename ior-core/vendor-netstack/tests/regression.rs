//! Regression tests that reproduce the bugs found in the static analysis.

use std::time::Duration;

use etherparse::{IpNumber, Ipv4Header, UdpHeader};
use futures::{SinkExt,StreamExt};
use tokio::time::timeout;

use netstack_smoltcp::StackBuilder;

fn make_udp_ipv4(
    src_ip: [u8; 4],
    src_port: u16,
    dst_ip: [u8; 4],
    dst_port: u16,
    payload: &[u8],
) -> Vec<u8> {
    let udp_hdr = UdpHeader::with_ipv4_checksum(
        src_port,
        dst_port,
        &Ipv4Header::new(
            (UdpHeader::LEN + payload.len()) as u16,
            64,
            IpNumber::UDP,
            src_ip,
            dst_ip,
        )
        .unwrap(),
        payload,
    )
    .unwrap();

    let ip_hdr = Ipv4Header::new(
        (UdpHeader::LEN + payload.len()) as u16,
        64,
        IpNumber::UDP,
        src_ip,
        dst_ip,
    )
    .unwrap();

    let mut buf = Vec::with_capacity(Ipv4Header::MIN_LEN + UdpHeader::LEN + payload.len());
    ip_hdr.write(&mut buf).unwrap();
    udp_hdr.write(&mut buf).unwrap();
    buf.extend_from_slice(payload);
    buf
}

/// before(include) a15e0b72bfc72cb032e67138070da01e325d66f8
/// sink_buf is used in `Stack` to hold a slot for sending any pkt
///
/// the original assumption is that the `poll_ready` -> `start_send` -> `poll_flush`
/// are called sequentially so the slot could be reused and will never get blocked.
///
/// but once the user calls `send_all` on `Stack`, which will not immediate flush the pkt(call `poll_flush`),
/// then `sink_buf` is could be Some(pkt), then it will trigger `Poll::Pending` branch in `Stack::poll_ready`,
/// who did not register the waker correctly, so it will got hanged forever.
#[tokio::test(flavor = "current_thread")]
async fn bug1_poll_ready_waker_registered_via_send_all() {
    let (mut stack, _runner, _udp_socket, _tcp) = StackBuilder::default()
        .enable_udp(true)
        .udp_buffer_size(64)
        .stack_buffer_size(64)
        .build()
        .unwrap();

    let pkt1 = make_udp_ipv4([1, 2, 3, 4], 1111, [5, 6, 7, 8], 9999, b"first");
    let pkt2 = make_udp_ipv4([1, 2, 3, 4], 1111, [5, 6, 7, 8], 9999, b"second");

    let mut stream = futures::stream::iter([Ok(pkt1), Ok(pkt2)]);

    let result = timeout(Duration::from_secs(1), stack.send_all(&mut stream)).await;
    // should be ok after the fix
    assert!(result.is_ok());
}

#[tokio::test]
async fn explicit_abort_emits_reset_but_normal_drop_remains_graceful(){
    fn sum(bytes:&[u8])->u16 {let mut sum=0u32;for chunk in bytes.chunks(2){sum+=u16::from_be_bytes([chunk[0],*chunk.get(1).unwrap_or(&0)]) as u32;}while sum>>16!=0{sum=(sum&65535)+(sum>>16);}!(sum as u16)}
    fn packet(seq:u32,ack:u32,flags:u8)->Vec<u8>{
        let mut p=vec![0;40];p[0]=0x45;p[2..4].copy_from_slice(&40u16.to_be_bytes());p[8]=64;p[9]=6;p[12..16].copy_from_slice(&[10,0,0,1]);p[16..20].copy_from_slice(&[198,51,100,2]);p[20..22].copy_from_slice(&40000u16.to_be_bytes());p[22..24].copy_from_slice(&443u16.to_be_bytes());p[24..28].copy_from_slice(&seq.to_be_bytes());p[28..32].copy_from_slice(&ack.to_be_bytes());p[32]=0x50;p[33]=flags;p[34..36].copy_from_slice(&65535u16.to_be_bytes());let checksum=sum(&p[..20]);p[10..12].copy_from_slice(&checksum.to_be_bytes());let mut pseudo=p[12..20].to_vec();pseudo.extend([0,6,0,20]);pseudo.extend(&p[20..]);let checksum=sum(&pseudo);p[36..38].copy_from_slice(&checksum.to_be_bytes());p
    }
    for reject in [false,true]{
    let (stack,runner,_,tcp)=StackBuilder::default().enable_tcp(true).build().unwrap();
    let runner=tokio::spawn(runner.unwrap());let (mut sink,mut outgoing)=stack.split();let mut listener=tcp.unwrap();
    sink.send(packet(100,0,2)).await.unwrap();
    let syn_ack=timeout(Duration::from_secs(2),outgoing.next()).await.unwrap().unwrap().unwrap();let offset=(syn_ack[0]&15) as usize*4;
    assert_eq!(syn_ack[offset+13]&0x12,0x12);let seq=u32::from_be_bytes(syn_ack[offset+4..offset+8].try_into().unwrap());
    sink.send(packet(101,seq.wrapping_add(1),0x10)).await.unwrap();
    let (stream,_,_)=timeout(Duration::from_secs(2),listener.next()).await.unwrap().unwrap();let abort=stream.abort_handle();drop(stream);if reject{abort.abort();}
    timeout(Duration::from_secs(2),async {loop{let p=outgoing.next().await.unwrap().unwrap();let offset=(p[0]&15) as usize*4;let flags=p[offset+13];if reject{if flags&4!=0{break;}}else{assert_eq!(flags&4,0,"ordinary drop must not reset");if flags&1!=0{break;}} }}).await.expect("peer must receive the requested RST or graceful FIN");
    runner.abort();let _=runner.await;
    }
}
