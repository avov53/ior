//! Opt-in lab: real official AWG adapters, loopback encrypted UDP impairment,
//! fixed periodic loss in each direction. No system routes or external sites.
use super::*;
use futures::{SinkExt, StreamExt};
use serde::Serialize;
use tokio::{io::AsyncBufReadExt, net::UdpSocket};

const PAYLOAD: usize = 64 * 1024 * 1024;

struct Lab {
    connector: TunnelConnector,
    tasks: tokio::task::JoinSet<()>,
}
impl Drop for Lab {
    fn drop(&mut self) {
        self.tasks.abort_all();
    }
}

fn udp_socket() -> UdpSocket {
    let socket = socket2::Socket::new(
        socket2::Domain::IPV6,
        socket2::Type::DGRAM,
        Some(socket2::Protocol::UDP),
    )
    .unwrap();
    socket.set_recv_buffer_size(4 * 1024 * 1024).unwrap();
    socket.set_send_buffer_size(4 * 1024 * 1024).unwrap();
    socket
        .bind(&"[::1]:0".parse::<SocketAddr>().unwrap().into())
        .unwrap();
    socket.set_nonblocking(true).unwrap();
    UdpSocket::from_std(socket.into()).unwrap()
}

async fn setup(path: &str, rtt: u64, loss_per_mille: u64, quic_target: Option<SocketAddr>) -> Lab {
    setup_with_receive(path,rtt,loss_per_mille,quic_target,crate::tunnel_tcp::BASE_RECEIVE).await
}
async fn setup_with_receive(path: &str, rtt: u64, loss_per_mille: u64, quic_target: Option<SocketAddr>, receive:usize) -> Lab {
    let engine=std::env::var("IOR_AWG_TEST_ENGINE").unwrap_or_else(|_|"go".into());
    setup_with_engine(path,rtt,loss_per_mille,quic_target,receive,&engine).await
}
async fn setup_with_engine(path: &str, rtt: u64, loss_per_mille: u64, quic_target: Option<SocketAddr>, receive:usize, engine:&str) -> Lab {
    let client_secret = boringtun::x25519::StaticSecret::from([7; 32]);
    let server_secret = boringtun::x25519::StaticSecret::from([9; 32]);
    let client_public = boringtun::x25519::PublicKey::from(&client_secret);
    let server_public = boringtun::x25519::PublicKey::from(&server_secret);
    let reservation = udp_socket();
    let server = reservation.local_addr().unwrap();
    drop(reservation);
    let front = Arc::new(udp_socket());
    let back = Arc::new(udp_socket());
    let endpoint = front.local_addr().unwrap();
    let mut tasks = tokio::task::JoinSet::new();
    tasks.spawn(async move {
        let mut a = vec![0; 65536];
        let mut b = vec![0; 65536];
        let mut peer = None;
        let mut a_count = 0u64;
        let mut b_count = 0u64;
        let mut delivery: VecDeque<(tokio::time::Instant, Vec<u8>, Arc<UdpSocket>, SocketAddr)> =
            VecDeque::new();
        let delay = Duration::from_millis(rtt / 2);
        loop {
            while delivery
                .front()
                .is_some_and(|item| item.0 <= tokio::time::Instant::now())
            {
                let (_, packet, socket, address) = delivery.pop_front().unwrap();
                socket.send_to(&packet, address).await.unwrap();
            }
            let deadline = delivery
                .front()
                .map(|item| item.0)
                .unwrap_or(tokio::time::Instant::now() + Duration::from_secs(60));
            tokio::select! {
                result=front.recv_from(&mut a)=>{
                    let(n,address)=result.unwrap();peer=Some(address);a_count+=1;
                    if loss_per_mille>0 && a_count%(1000/loss_per_mille)==0 {continue;}
                    assert!(delivery.len()<8192,"impairment queue overflow");
                    let packet=a[..n].to_vec();let socket=back.clone();
                    delivery.push_back((tokio::time::Instant::now()+delay,packet,socket,server));
                },
                result=back.recv_from(&mut b)=>{
                    let(n,_)=result.unwrap();b_count+=1;
                    if loss_per_mille>0 && b_count%(1000/loss_per_mille)==0 {continue;}
                    let packet=b[..n].to_vec();let socket=front.clone();let address=peer.unwrap();
                    assert!(delivery.len()<8192,"impairment queue overflow");
                    delivery.push_back((tokio::time::Instant::now()+delay,packet,socket,address));
                },
                _=tokio::time::sleep_until(deadline),if !delivery.is_empty()=>{}
            }
        }
    });
    let (stack, runner, udp, listener) = netstack_smoltcp::StackBuilder::default()
        .enable_tcp(true)
        .enable_udp(true)
        .tcp_recv_buffer_size(512 * 1024)
        .tcp_send_buffer_size(2 * 1024 * 1024)
        .mtu(1280)
        .build()
        .unwrap();
    tasks.spawn(async move {
        runner.unwrap().await.unwrap();
    });
    let (mut sink, mut stream) = stack.split();
    let mut process = tokio::process::Command::new(path);
    process
        .stdin(std::process::Stdio::piped())
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::piped())
        .kill_on_drop(true);
    #[cfg(windows)]
    process.creation_flags(0x08000000);
    let mut child = process.spawn().unwrap();
    let mut writer = child.stdin.take().unwrap();
    let mut reader = child.stdout.take().unwrap();
    let mut errors = tokio::io::BufReader::new(child.stderr.take().unwrap());
    let hex = |bytes: &[u8]| bytes.iter().map(|b| format!("{b:02x}")).collect::<String>();
    let config = serde_json::json!({"api":2,"interfaceIndex":1,"interfaceIndex6":1,"endpointV6":true,"mtu":1280,
        "uapi":format!("jc=2\njmin=12\njmax=24\ns1=16\ns2=16\ns3=16\ns4=16\ncontent_padding_addition=8-16\nrandom_trailers=true\nheader_protection_key=0909090909090909090909090909090909090909090909090909090909090909\nh1=12345678\nh2=23456789\nh3=34567890\nh4=45678901\nprivate_key={}\nlisten_port={}\nreplace_peers=true\npublic_key={}\nallowed_ip=10.7.0.2/32\nallowed_ip=fd00:7::2/128\n",hex(&server_secret.to_bytes()),server.port(),hex(client_public.as_bytes()))});
    writer
        .write_all(format!("{config}\n").as_bytes())
        .await
        .unwrap();
    writer.flush().await.unwrap();
    let mut ready = String::new();
    errors.read_line(&mut ready).await.unwrap();
    assert_eq!(ready.trim(), "AWG_ADAPTER_READY");
    tasks.spawn(async move {
        let mut bridges = tokio::task::JoinSet::new();
        bridges.spawn(async move {
            while errors.read_line(&mut String::new()).await.unwrap_or(0) > 0 {}
        });
        bridges.spawn(async move {
            let mut reassembly = crate::tunnel_fragments::Reassembly::default();
            loop {
                let size = reader.read_u32().await.unwrap() as usize;
                assert!((20..65536).contains(&size));
                let mut packet = vec![0; size];
                reader.read_exact(&mut packet).await.unwrap();
                if let Some(packet) = reassembly.accept(&packet).unwrap() {
                    sink.send(packet).await.unwrap();
                }
            }
        });
        bridges.spawn(async move {
            while let Some(packet) = stream.next().await {
                let packet = packet.unwrap();
                for packet in crate::tunnel_fragments::fragment(&packet, 1280).unwrap() {
                    writer.write_u32(packet.len() as u32).await.unwrap();
                    writer.write_all(&packet).await.unwrap();
                    writer.flush().await.unwrap();
                }
            }
        });
        let _ = child.wait().await;
        bridges.abort_all();
    });
    tasks.spawn(async move {
        let mut listener=listener.unwrap();let mut connections=tokio::task::JoinSet::new();
        loop {tokio::select! {
            item=listener.next()=>{let Some((mut tcp,_,_))=item else{break};connections.spawn(async move {
                let mode=tcp.read_u8().await.unwrap();let size=tcp.read_u64().await.unwrap() as usize;
                let mut buffer=vec![0;32768];
                match mode {
                    b'D'=>{for offset in (0..size).step_by(buffer.len()) {let count=buffer.len().min(size-offset);fill(&mut buffer[..count],offset);tcp.write_all(&buffer[..count]).await.unwrap();}},
                    b'U'=>{let mut offset=0;while offset<size {let count=tcp.read(&mut buffer).await.unwrap();assert!(count>0);verify(&buffer[..count],offset);offset+=count;}tcp.write_u64(offset as u64).await.unwrap();},
                    b'E'=>{for _ in 0..size {let mut value=[0;8];tcp.read_exact(&mut value).await.unwrap();tcp.write_all(&value).await.unwrap();}},
                    b'C'=>{for offset in (0..size).step_by(buffer.len()) {let count=buffer.len().min(size-offset);fill(&mut buffer[..count],offset);if !matches!(tokio::time::timeout(Duration::from_secs(2),tcp.write_all(&buffer[..count])).await,Ok(Ok(()))){return;}}},
                    b'R'=>{for _ in 0..size {let offset=tcp.read_u64().await.unwrap() as usize;let length=tcp.read_u64().await.unwrap() as usize;for position in (0..length).step_by(buffer.len()){let count=buffer.len().min(length-position);fill(&mut buffer[..count],offset+position);tcp.write_all(&buffer[..count]).await.unwrap();}}},
                    _=>panic!("invalid lab command"),
                }
                tcp.shutdown().await.unwrap();
            });},
            result=connections.join_next(),if !connections.is_empty()=>{result.unwrap().unwrap();}
        }}
    });
    use sha2::{Digest, Sha256};
    let p:WireProfile=serde_json::from_value(serde_json::json!({"protocol":"amneziawg","server":endpoint.ip().to_string(),"port":endpoint.port(),"serverName":"localhost","credential":base64::engine::general_purpose::STANDARD.encode(client_secret.to_bytes()),"options":{
        "peerPublicKey":base64::engine::general_purpose::STANDARD.encode(server_public.as_bytes()),"localAddresses":["10.7.0.2/32","fd00:7::2/128"],"allowedIps":["10.7.0.0/24","fd00:7::/64"],"mtu":1280,"awgAdapterPath":path,"awgAdapterSha256":format!("{:x}",Sha256::digest(tokio::fs::read(path).await.unwrap())),
        "awg":{"jc":"2","jmin":"12","jmax":"24","s1":"16","s2":"16","s3":"16","s4":"16","content_padding_addition":"8-16","random_trailers":"true","i1":"<b ff><t><r 4><rc 3><rd 2><dz 2><d><ds>","header_protection_key":base64::engine::general_purpose::STANDARD.encode([9u8;32]),"h1":"12345678","h2":"23456789","h3":"34567890","h4":"45678901"}}})).unwrap();
    let network = Some(WireNetwork {
        physical_address: "127.0.0.1".into(),
        interface_index: 1,
        physical_address6: Some("::1".into()),
        interface_index6: Some(1),
    });
    let mut p = p;
    p.options.awg_engine = Some(engine.to_owned());
    let mut connector = TunnelConnector::new(&p, "1.1.1.1:53".parse().unwrap(), network)
        .await
        .unwrap();

    // Warm handshake; excludes AWG startup from transfer timing.
    Arc::get_mut(&mut connector.session).unwrap().tcp_tuning.receive_bytes=receive;
    interactive(&connector, 1).await;
    tasks.spawn(async move {
        let(mut incoming,mut outgoing)=udp.unwrap().split();
        if let Some(target)=quic_target {
            let socket=tokio::net::UdpSocket::bind("127.0.0.1:0").await.unwrap();socket.connect(target).await.unwrap();let mut peer=None;let mut buffer=vec![0;65535];
            loop{tokio::select!{packet=incoming.next()=>{let Some((data,source,destination))=packet else{break};peer=Some((source,destination));socket.send(&data).await.unwrap();},size=socket.recv(&mut buffer)=>{let size=size.unwrap();if let Some((source,destination))=peer{outgoing.send((buffer[..size].to_vec(),destination,source)).await.unwrap();}}}}
        }else{while let Some((data,source,destination))=incoming.next().await{outgoing.send((data,destination,source)).await.unwrap();}}
    });
    Lab { connector, tasks }
}

fn fill(buffer: &mut [u8], offset: usize) {
    for (i, b) in buffer.iter_mut().enumerate() {
        *b = ((offset + i) % 251) as u8;
    }
}
fn verify(buffer: &[u8], offset: usize) {
    for (i, b) in buffer.iter().enumerate() {
        assert_eq!(
            *b,
            ((offset + i) % 251) as u8,
            "payload corruption at {}",
            offset + i
        );
    }
}

#[cfg(windows)]
fn powershell(script: &str) -> std::process::Output {
    use std::os::windows::process::CommandExt;
    std::process::Command::new("powershell.exe")
        .args(["-NoProfile", "-NonInteractive", "-Command", script])
        .creation_flags(0x08000000)
        .output()
        .unwrap()
}

#[cfg(windows)]
struct WindowsTunLease {
    name: String,
    task: tokio::task::JoinHandle<io::Result<()>>,
}
#[cfg(windows)]
impl Drop for WindowsTunLease {
    fn drop(&mut self) {
        self.task.abort();
        // Only this freshly created adapter and its two exact fixture routes.
        let _=powershell(&format!("$a=Get-NetAdapter -Name '{}' -ErrorAction SilentlyContinue; if($a) {{ foreach($p in @('10.7.0.1/32','fd00:7::1/128')) {{ Get-NetRoute -InterfaceIndex $a.ifIndex -DestinationPrefix $p -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue }}; Get-NetIPAddress -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue | Where-Object IPAddress -in @('172.31.255.2','fdfe:dcba:9866::2') | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue }}",self.name));
    }
}

/// Uses actual Windows TCP and the production TUN reader/writer. Requires admin.
#[cfg(windows)]
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
#[ignore = "owned Windows Wintun fixture routes; run elevated with IOR_TEST_AWG_ADAPTER, IOR_TEST_WINTUN and IOR_AWG_LAB_OUTPUT"]
async fn windows_tcp_through_production_tun_and_official_awg() {
    let path = std::env::var("IOR_TEST_AWG_ADAPTER").unwrap();
    let wintun = std::env::var("IOR_TEST_WINTUN").unwrap();
    let output = std::env::var("IOR_AWG_LAB_OUTPUT").unwrap();
    assert!(!std::path::Path::new(&output).exists());
    let inspect=powershell("$ErrorActionPreference='Stop'; $i=[Security.Principal.WindowsIdentity]::GetCurrent(); if(-not ([Security.Principal.WindowsPrincipal]::new($i)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator required'}; foreach($p in @('10.7.0.1/32','fd00:7::1/128')){if(Get-NetRoute -DestinationPrefix $p -ErrorAction SilentlyContinue){throw 'Fixture route already exists'}}");
    assert!(
        inspect.status.success(),
        "{}",
        String::from_utf8_lossy(&inspect.stderr)
    );
    let mut rows = Vec::new();
    let matrix = std::env::var_os("IOR_AWG_TCP_MATRIX").is_some();
    let scenarios = if matrix {
        [50, 100, 200]
            .into_iter()
            .flat_map(|rtt| [0, 5, 10].into_iter().map(move |loss| (rtt, loss, false)))
            .collect::<Vec<_>>()
    } else {
        vec![
            (
                std::env::var("IOR_AWG_TEST_RTT")
                    .ok()
                    .and_then(|s| s.parse().ok())
                    .unwrap_or(50),
                5,
                false,
            ),
            (
                std::env::var("IOR_AWG_TEST_RTT")
                    .ok()
                    .and_then(|s| s.parse().ok())
                    .unwrap_or(50),
                5,
                true,
            ),
        ]
    };
    for (index, (rtt, loss, ipv6)) in scenarios.into_iter().enumerate() {
        let raw = true;
        let lab = setup(&path, rtt, loss, None).await;
        let router = Arc::new(
            crate::native_router::NativeRouter::new(
                Arc::new(lab.connector.clone()),
                vec![crate::ipc_contract::WireRule {
                    kind: "MATCH".into(),
                    value: String::new(),
                    target: "PROXY".into(),
                    source: "windows-lab".into(),
                    no_resolve: true,
                }],
                "1.1.1.1:53".parse().unwrap(),
                None,
            )
            .unwrap(),
        );
        let dns = Arc::new(
            crate::tun_dns::TunDns::new(
                router.clone(),
                &crate::ipc_contract::WireDns {
                    bootstrap: "1.1.1.1".into(),
                    upstream: "10.7.0.1".into(),
                    upstreams: vec![],
                    ipv4_only: true,
                },
            )
            .unwrap(),
        );
        if ipv6 {
            let mut p = vec![
                1, 2, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 7, b'f', b'i', b'x', b't', b'u', b'r', b'e', 0,
                0, 65, 0, 1,
            ];
            let q = crate::tun_names::question(&p).unwrap();
            p[2] = 0x81;
            p[3] = 0x80;
            p[7] = 1;
            let mut data = vec![0, 1, 0, 0, 4, 0, 4, 10, 7, 0, 1, 0, 6, 0, 16];
            data.extend("fd00:7::1".parse::<Ipv6Addr>().unwrap().octets());
            p.extend([0xc0, 0x0c, 0, 65, 0, 1, 0, 0, 1, 44]);
            p.extend((data.len() as u16).to_be_bytes());
            p.extend(data);
            dns.names.observe(&p, &q).unwrap();
        }
        let name = format!("ior-native-lab-{:x}-{index}", std::process::id());
        assert!(powershell(&format!(
            "if(Get-NetAdapter -Name '{name}' -ErrorAction SilentlyContinue){{exit 1}}"
        ))
        .status
        .success());
        let task = crate::tun::start_with_dns(
            &crate::ipc_contract::WireTun {
                name: name.clone(),
                mtu: 1280,
                wintun_path: wintun.clone(),
            },
            router,
            dns,
        )
        .await
        .unwrap();
        let lease = WindowsTunLease {
            name: name.clone(),
            task,
        };
        let applied=powershell(&format!("$ErrorActionPreference='Stop';$a=Get-NetAdapter -Name '{name}';Set-NetIPInterface -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -DadTransmits 0;Set-NetIPInterface -InterfaceIndex $a.ifIndex -AddressFamily IPv6 -DadTransmits 0;New-NetIPAddress -InterfaceIndex $a.ifIndex -IPAddress '172.31.255.2' -PrefixLength 32 | Out-Null;New-NetIPAddress -InterfaceIndex $a.ifIndex -IPAddress 'fdfe:dcba:9866::2' -PrefixLength 128 | Out-Null;foreach($p in @('10.7.0.1/32','fd00:7::1/128')){{New-NetRoute -InterfaceIndex $a.ifIndex -DestinationPrefix $p -NextHop $(if($p.Contains(':')){{'::'}}else{{'0.0.0.0'}}) -PolicyStore ActiveStore | Out-Null}};for($i=0;$i -lt 30;$i++){{$v=Get-NetIPAddress -InterfaceIndex $a.ifIndex | Where-Object IPAddress -in @('172.31.255.2','fdfe:dcba:9866::2');if(@($v).Count -eq 2 -and @($v | Where-Object AddressState -ne Preferred).Count -eq 0){{exit 0}};Start-Sleep -Milliseconds 100}};throw 'Fixture addresses not preferred'"));
        assert!(
            applied.status.success(),
            "{}",
            String::from_utf8_lossy(&applied.stderr)
        );
        let remote = if ipv6 {
            "[fd00:7::1]:443"
        } else {
            "10.7.0.1:443"
        };
        let start = std::time::Instant::now();
        tokio::time::timeout(Duration::from_secs(180), async {
            let mut tcp = tokio::net::TcpStream::connect(remote).await.unwrap();
            tcp.set_nodelay(true).unwrap();
            tcp.write_u8(b'E').await.unwrap();
            tcp.write_u64(5).await.unwrap();
            for i in 0..5u8 {
                tcp.write_all(&[i; 4]).await.unwrap();
                tokio::time::sleep(Duration::from_millis(5)).await;
                tcp.write_all(&[i; 4]).await.unwrap();
                let mut r = [0; 8];
                tcp.read_exact(&mut r).await.unwrap();
                assert_eq!(r, [i; 8]);
            }
            tcp.shutdown().await.unwrap();
            drop(tcp);
            let size = if matrix {
                4 * 1024 * 1024usize
            } else {
                64 * 1024 * 1024usize
            };
            let mut tcp = tokio::net::TcpStream::connect(remote).await.unwrap();
            tcp.write_u8(b'D').await.unwrap();
            tcp.write_u64(size as u64).await.unwrap();
            let concurrent = tokio::spawn(async move {
                let started = std::time::Instant::now();
                let mut small = tokio::net::TcpStream::connect(remote).await.unwrap();
                small.set_nodelay(true).unwrap();
                small.write_u8(b'E').await.unwrap();
                small.write_u64(8).await.unwrap();
                for i in 0..8u8 {
                    small.write_all(&[i; 8]).await.unwrap();
                    let mut bytes = [0; 8];
                    small.read_exact(&mut bytes).await.unwrap();
                    assert_eq!(bytes, [i; 8]);
                }
                small.shutdown().await.unwrap();
                let mut parallel = tokio::net::TcpStream::connect(remote).await.unwrap();
                parallel.write_u8(b'D').await.unwrap();
                parallel.write_u64(1024 * 1024).await.unwrap();
                let mut data = vec![0; 1024 * 1024];
                parallel.read_exact(&mut data).await.unwrap();
                verify(&data, 0);
                started.elapsed()
            });
            tokio::time::sleep(Duration::from_millis(300)).await;
            let mut buffer = vec![0; 32768];
            let mut offset = 0;
            while offset < size {
                let n = tcp.read(&mut buffer).await.unwrap();
                assert!(n > 0);
                verify(&buffer[..n], offset);
                offset += n;
            }
            assert_eq!(offset, size);
            assert_eq!(tcp.read(&mut buffer).await.unwrap(), 0);
            tcp.shutdown().await.unwrap();
            assert!(
                concurrent.await.unwrap() < Duration::from_secs(60),
                "small/parallel transfer starved"
            );
            // Cancel one response mid-transfer, then issue a new request after a pause.
            let mut cancelled = tokio::net::TcpStream::connect(remote).await.unwrap();
            cancelled.write_u8(b'C').await.unwrap();
            cancelled.write_u64(4 * 1024 * 1024).await.unwrap();
            let mut prefix = [0; 4096];
            cancelled.read_exact(&mut prefix).await.unwrap();
            verify(&prefix, 0);
            drop(cancelled);
            tokio::time::sleep(Duration::from_millis(100)).await;
            let mut next = tokio::net::TcpStream::connect(remote).await.unwrap();
            next.write_u8(b'D').await.unwrap();
            next.write_u64(65536).await.unwrap();
            let mut bytes = vec![0; 65536];
            next.read_exact(&mut bytes).await.unwrap();
            verify(&bytes, 0);
            next.shutdown().await.unwrap();
        })
        .await
        .expect("Windows TCP fixture timed out");
        rows.push(serde_json::json!({"raw":raw,"ipv6":ipv6,"bytes":if matrix{4*1024*1024}else{64*1024*1024},"nominalRttMs":rtt,"lossPerMille":loss,"integrity":true,"durationMs":start.elapsed().as_millis()as u64}));
        drop(lease);
        drop(lab);
        tokio::time::sleep(Duration::from_millis(500)).await;
        assert!(powershell(&format!("if(Get-NetAdapter -Name '{name}' -ErrorAction SilentlyContinue){{exit 1}};foreach($p in @('10.7.0.1/32','fd00:7::1/128')){{if(Get-NetRoute -DestinationPrefix $p -ErrorAction SilentlyContinue){{exit 1}}}};exit 0")).status.success(),"owned adapter/routes not removed");
    }
    tokio::fs::write(output,serde_json::to_vec_pretty(&serde_json::json!({"complete":true,"localOnly":true,"purpose":"Windows TCP through production TUN correctness, not YouTube performance","rollback":true,"results":rows})).unwrap()).await.unwrap();
}

async fn command(connector: &TunnelConnector, mode: u8, size: usize) -> Box<dyn RelayStream> {
    let mut tcp = connector.connect("10.7.0.1".into(), 443).await.unwrap();
    tcp.write_u8(mode).await.unwrap();
    tcp.write_u64(size as u64).await.unwrap();
    tcp
}
async fn download(connector: &TunnelConnector, size: usize, slow: bool) -> u64 {
    let mut tcp = command(connector, b'D', size).await;
    let start = std::time::Instant::now();
    if slow {
        tokio::time::sleep(Duration::from_millis(500)).await;
    }
    let mut buffer = vec![0; 32768];
    let mut offset = 0;
    while offset < size {
        let n = tcp.read(&mut buffer).await.unwrap();
        assert!(n > 0, "early EOF");
        verify(&buffer[..n], offset);
        offset += n;
    }
    assert_eq!(offset, size);
    assert_eq!(tcp.read(&mut buffer).await.unwrap(), 0);
    tcp.shutdown().await.unwrap();
    start.elapsed().as_micros().min(u64::MAX as u128) as u64
}
async fn upload(connector: &TunnelConnector, size: usize) -> u64 {
    let mut tcp = command(connector, b'U', size).await;
    let start = std::time::Instant::now();
    let mut buffer = vec![0; 32768];
    for offset in (0..size).step_by(buffer.len()) {
        let n = buffer.len().min(size - offset);
        fill(&mut buffer[..n], offset);
        tcp.write_all(&buffer[..n]).await.unwrap();
    }
    tcp.shutdown().await.unwrap();
    assert_eq!(tcp.read_u64().await.unwrap(), size as u64);
    start.elapsed().as_micros().min(u64::MAX as u128) as u64
}
async fn interactive(connector: &TunnelConnector, count: usize) -> Vec<u64> {
    let mut tcp = command(connector, b'E', count).await;
    let mut samples = Vec::new();
    for i in 0..count {
        let start = std::time::Instant::now();
        tcp.write_all(&[i as u8; 4]).await.unwrap();
        tokio::task::yield_now().await;
        tcp.write_all(&[i as u8; 4]).await.unwrap();
        let mut reply = [0; 8];
        tcp.read_exact(&mut reply).await.unwrap();
        assert_eq!(reply, [i as u8; 8]);
        samples.push(start.elapsed().as_micros() as u64);
    }
    tcp.shutdown().await.unwrap();
    samples
}
#[tokio::test(flavor="multi_thread",worker_threads=4)]
#[ignore="210-second local Rust/Go rekey comparison; IOR_TEST_AWG_ADAPTER"]
async fn awg_sustained_rekey_cancel_and_new_requests() {
    async fn run(path:&str,engine:&str)->serde_json::Value {
        let lab=setup_with_engine(path,100,5,None,crate::tunnel_tcp::LARGE_RECEIVE,engine).await;
        let mut tcp=command(&lab.connector,b'E',22).await;
        let mut udp=lab.connector.datagram("10.7.0.1:443".parse().unwrap()).unwrap();
        let start=tokio::time::Instant::now();let mut samples=Vec::new();let mut large=Vec::new();
        for step in 0..22 {
            tokio::time::sleep_until(start+Duration::from_secs(step*10)).await;
            let at=std::time::Instant::now();
            tokio::time::timeout(Duration::from_secs(5),async{
                tcp.write_all(&[step as u8;8]).await.unwrap();let mut bytes=[0;8];tcp.read_exact(&mut bytes).await.unwrap();assert_eq!(bytes,[step as u8;8]);
                udp.send(&[step as u8;16]).await.unwrap();assert_eq!(udp.receive().await.unwrap(),vec![step as u8;16]);
            }).await.expect("persistent TCP/UDP stalled");
            let request_ms=at.elapsed().as_millis() as u64;samples.push(request_ms);
            if matches!(step,0|11|12|13|18|21){
                let transfer=tokio::time::timeout(Duration::from_secs(10),download(&lab.connector,1024*1024,false)).await.expect("new download stalled");large.push(transfer);
            }
            if matches!(step,5|13){let mut old=command(&lab.connector,b'D',2*1024*1024).await;let mut prefix=[0;8192];old.read_exact(&mut prefix).await.unwrap();verify(&prefix,0);drop(old);}
            println!("AWG_SUSTAINED_STEP engine={engine} elapsedSec={} requestMs={request_ms}",start.elapsed().as_secs());
        }
        tcp.shutdown().await.unwrap();drop(udp);samples.sort_unstable();
        serde_json::json!({"engine":engine,"elapsedMs":start.elapsed().as_millis() as u64,"requestMedianMs":samples[11],"requestP95Ms":samples[20],"requestMaxMs":samples[21],"downloadsUs":large,"integrity":true,"persistentTcpUdp":true,"cancelledDownloads":2})
    }
    let path=std::env::var("IOR_TEST_AWG_ADAPTER").unwrap();let (rust,go)=tokio::join!(run(&path,"rust"),run(&path,"go"));
    println!("AWG_SUSTAINED_RESULT {}",serde_json::json!({"rust":rust,"go":go,"rttMs":100,"lossPercent":0.5}));
}
#[tokio::test(flavor="multi_thread",worker_threads=4)]
#[ignore="local AWG 64MiB + interactive + cancel/resume; IOR_TEST_AWG_ADAPTER"]
async fn awg_large_window_integrity_cancel_and_resume(){
    let path=std::env::var("IOR_TEST_AWG_ADAPTER").unwrap();
    let lab=setup_with_engine(&path,100,5,None,crate::tunnel_tcp::AWG_RECEIVE,"rust").await;
    tokio::time::timeout(Duration::from_secs(90),async{
        let (large,small)=tokio::join!(download(&lab.connector,64*1024*1024,false),interactive(&lab.connector,8));
        let mut old=command(&lab.connector,b'D',64*1024*1024).await;let mut prefix=[0;8192];old.read_exact(&mut prefix).await.unwrap();verify(&prefix,0);drop(old);
        tokio::time::sleep(Duration::from_millis(100)).await;
        let resumed=download(&lab.connector,1024*1024,false).await;
        println!("AWG_LARGE_WINDOW_INTEGRITY bytes=67108864 downloadUs={large} smallRequests={} resumedUs={resumed} cancelled=1",small.len());
    }).await.expect("large/cancel/resume scenario stalled");
}
#[tokio::test(flavor="multi_thread",worker_threads=4)]
#[ignore="local AWG fallback receive-window gate; IOR_TEST_AWG_ADAPTER"]
async fn awg_fallback_receive_window_gate() {
    let window=|name:&str,default:usize|std::env::var(name).ok().map(|value|value.parse::<usize>().unwrap()).unwrap_or(default);
    let baseline=window("IOR_AWG_WINDOW_BASELINE",crate::tunnel_tcp::BASE_RECEIVE);
    let candidate_window=window("IOR_AWG_WINDOW_CANDIDATE",crate::tunnel_tcp::LARGE_RECEIVE);
    assert!((crate::tunnel_tcp::BASE_RECEIVE..=2*1024*1024).contains(&baseline));
    assert!((crate::tunnel_tcp::BASE_RECEIVE..=2*1024*1024).contains(&candidate_window));
    let rtt=std::env::var("IOR_AWG_WINDOW_RTT").ok().map(|v|v.parse::<u64>().unwrap()).unwrap_or(100);
    assert!((50..=200).contains(&rtt));
    let path=std::env::var("IOR_TEST_AWG_ADAPTER").unwrap();
    let mut downloads=[Vec::new(),Vec::new()];let mut requests=[Vec::new(),Vec::new()];
    for round in 0..3 {
        for candidate in if round%2==0 {[false,true]}else{[true,false]} {
            let lab=setup_with_receive(&path,rtt,5,None,if candidate{candidate_window}else{baseline}).await;
            let workload=async {tokio::join!(download(&lab.connector,8*1024*1024,false),interactive(&lab.connector,20))};
            let (time,small)=tokio::time::timeout(Duration::from_secs(90),workload).await.expect("window scenario hung");
            let index=candidate as usize;downloads[index].push(time);requests[index].extend(small);
            println!("AWG_WINDOW_SAMPLE candidate={candidate} round={round} downloadUs={time}");
        }
    }
    downloads.iter_mut().for_each(|v|v.sort_unstable());requests.iter_mut().for_each(|v|v.sort_unstable());
    let p95=|v:&Vec<u64>|v[(v.len()*95).div_ceil(100)-1];
    let accepted=downloads[1][1]*100<=downloads[0][1]*85 && p95(&requests[1])*100<=p95(&requests[0])*105;
    println!("AWG_WINDOW_GATE accepted={accepted} baselineMedianUs={} candidateMedianUs={} baselineP95Us={} candidateP95Us={}",downloads[0][1],downloads[1][1],p95(&requests[0]),p95(&requests[1]));
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
#[ignore = "local official AWG engines, run with IOR_TEST_AWG_ADAPTER"]
async fn direct_udp_packet_matrix_dual_stack_and_cancellation() {
    let path = std::env::var("IOR_TEST_AWG_ADAPTER").unwrap();
    let mut rows = Vec::new();
    for rtt in [50, 100, 200] {
        for loss in [0, 5, 10] {
            let lab = setup(&path, rtt, loss, None).await;
            let router = Arc::new(
                crate::native_router::NativeRouter::new(
                    Arc::new(lab.connector.clone()),
                    vec![crate::ipc_contract::WireRule {
                        kind: "MATCH".into(),
                        value: String::new(),
                        target: "PROXY".into(),
                        source: "lab".into(),
                        no_resolve: true,
                    }],
                    "1.1.1.1:53".parse().unwrap(),
                    None,
                )
                .unwrap(),
            );
            let (out, mut incoming) = mpsc::channel(16);
            let mut manager = crate::awg_packets::Manager::new(router, out);
            for (source, dest) in [
                ("172.31.255.2:4000", "10.7.0.1:443"),
                ("[fdfe:dcba::2]:4001", "[fd00:7::1]:443"),
            ] {
                let (source, dest) = (source.parse().unwrap(), dest.parse().unwrap());
                for size in [1, 1200, 4096] {
                    let data = vec![(size % 251) as u8; size];
                    let packet = crate::awg_packets::udp_packet(source, dest, &data);
                    let mut success = false;
                    for _ in 0..5 {
                        assert!(manager.process(&packet).await.unwrap().is_none());
                        if let Ok(Some(reply)) =
                            tokio::time::timeout(Duration::from_secs(2), incoming.recv()).await
                        {
                            let t = crate::awg_packets::tuple(&reply).unwrap();
                            assert_eq!(t.source, dest);
                            assert_eq!(t.destination, source);
                            let h = if dest.is_ipv6() { 40 } else { 20 };
                            assert_eq!(&reply[h + 8..], &data);
                            success = true;
                            break;
                        }
                    }
                    assert!(success, "UDP lost all attempts RTT={rtt} loss={loss}");
                }
            }
            drop(manager);
            drop(incoming);
            tokio::time::timeout(Duration::from_secs(3), async {
                while !lab.connector.session.flows.lock().unwrap().is_empty()
                    || lab.connector.session.packet_budget.used() != 0
                {
                    tokio::time::sleep(Duration::from_millis(10)).await;
                }
            })
            .await
            .unwrap();
            rows.push(serde_json::json!({"rttMs":rtt,"lossPerMille":loss,"dualStack":true,"udp443":true,"integrity":true,"budgetReleased":true}));
        }
    }
    if let Ok(path) = std::env::var("IOR_AWG_UDP_LAB_OUTPUT") {
        tokio::fs::write(path, serde_json::to_vec_pretty(&rows).unwrap())
            .await
            .unwrap();
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
#[ignore = "local HTTP/3 over official AWG packet path; IOR_TEST_AWG_ADAPTER"]
async fn actual_http3_through_awg_udp443() {
    use bytes::Buf;
    let _ = tokio_rustls::rustls::crypto::ring::default_provider().install_default();
    let cert = rcgen::generate_simple_self_signed(vec!["localhost".into()]).unwrap();
    let der = cert.cert.der().clone();
    let key =
        tokio_rustls::rustls::pki_types::PrivatePkcs8KeyDer::from(cert.signing_key.serialize_der());
    let mut tls = tokio_rustls::rustls::ServerConfig::builder()
        .with_no_client_auth()
        .with_single_cert(vec![der.clone()], key.into())
        .unwrap();
    tls.alpn_protocols = vec![b"h3".to_vec()];
    let config = quinn::ServerConfig::with_crypto(Arc::new(
        quinn::crypto::rustls::QuicServerConfig::try_from(tls).unwrap(),
    ));
    let server = quinn::Endpoint::server(config, "127.0.0.1:0".parse().unwrap()).unwrap();
    let mut roots = tokio_rustls::rustls::RootCertStore::empty();
    roots.add(der).unwrap();
    let mut tls = tokio_rustls::rustls::ClientConfig::builder()
        .with_root_certificates(roots)
        .with_no_client_auth();
    tls.alpn_protocols = vec![b"h3".to_vec()];
    let mut client = quinn::Endpoint::client("127.0.0.1:0".parse().unwrap()).unwrap();
    client.set_default_client_config(quinn::ClientConfig::new(Arc::new(
        quinn::crypto::rustls::QuicClientConfig::try_from(tls).unwrap(),
    )));
    let path = std::env::var("IOR_TEST_AWG_ADAPTER").unwrap();
    let mut lab = setup(&path, 100, 5, Some(server.local_addr().unwrap())).await;
    let router = Arc::new(
        crate::native_router::NativeRouter::new(
            Arc::new(lab.connector.clone()),
            vec![crate::ipc_contract::WireRule {
                kind: "MATCH".into(),
                value: String::new(),
                target: "PROXY".into(),
                source: "http3-lab".into(),
                no_resolve: true,
            }],
            "1.1.1.1:53".parse().unwrap(),
            None,
        )
        .unwrap(),
    );
    let front = tokio::net::UdpSocket::bind("127.0.0.1:0").await.unwrap();
    let address = front.local_addr().unwrap();
    let (out, mut incoming) = mpsc::channel(256);
    let mut manager = crate::awg_packets::Manager::new(router, out);
    lab.tasks.spawn(async move{let mut buffer=vec![0;65535];let mut peer=None;loop{tokio::select!{received=front.recv_from(&mut buffer)=>{let(n,physical)=received.unwrap();peer=Some(physical);let p=crate::awg_packets::udp_packet("172.19.0.1:54000".parse().unwrap(),"10.7.0.1:443".parse().unwrap(),&buffer[..n]);assert!(manager.process(&p).await.unwrap().is_none());},p=incoming.recv()=>{let Some(p)=p else{break};if let Some(peer)=peer{front.send_to(&p[28..],peer).await.unwrap();}}}}});
    lab.tasks.spawn(async move {
        let connection = server.accept().await.unwrap().await.unwrap();
        let mut h3 = h3::server::Connection::new(h3_quinn::Connection::new(connection))
            .await
            .unwrap();
        while let Some(request) = h3.accept().await.unwrap() {
            let (_, mut stream) = request.resolve_request().await.unwrap();
            stream
                .send_response(http::Response::builder().status(200).body(()).unwrap())
                .await
                .unwrap();
            stream
                .send_data(bytes::Bytes::from(vec![42; 1024 * 1024]))
                .await
                .unwrap();
            stream.finish().await.unwrap();
        }
    });
    tokio::time::timeout(Duration::from_secs(45), async {
        let connection = client.connect(address, "localhost").unwrap().await.unwrap();
        assert_eq!(
            connection
                .handshake_data()
                .unwrap()
                .downcast::<quinn::crypto::rustls::HandshakeData>()
                .unwrap()
                .protocol,
            Some(b"h3".to_vec())
        );
        let (mut driver, mut sender) = h3::client::new(h3_quinn::Connection::new(connection))
            .await
            .unwrap();
        lab.tasks.spawn(async move {
            let _ = futures::future::poll_fn(|cx| driver.poll_close(cx)).await;
        });
        for _ in 0..2 {
            let mut stream = sender
                .send_request(
                    http::Request::builder()
                        .uri("https://localhost/fixture")
                        .body(())
                        .unwrap(),
                )
                .await
                .unwrap();
            stream.finish().await.unwrap();
            assert_eq!(stream.recv_response().await.unwrap().status(), 200);
            let mut count = 0;
            while let Some(data) = stream.recv_data().await.unwrap() {
                assert!(data.chunk().iter().all(|b| *b == 42));
                count += data.remaining();
            }
            assert_eq!(count, 1024 * 1024);
            tokio::time::sleep(Duration::from_millis(100)).await;
        }
    })
    .await
    .expect("HTTP/3 through AWG timed out");
    client.close(0u32.into(), b"done");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
#[ignore = "local official AWG engine failure; IOR_TEST_AWG_ADAPTER"]
async fn engine_exit_closes_packet_path_without_direct_fallback() {
    let path = std::env::var("IOR_TEST_AWG_ADAPTER").unwrap();
    let lab = setup(&path, 0, 0, None).await;
    let router = Arc::new(
        crate::native_router::NativeRouter::new(
            Arc::new(lab.connector.clone()),
            vec![crate::ipc_contract::WireRule {
                kind: "MATCH".into(),
                value: String::new(),
                target: "PROXY".into(),
                source: "failure-lab".into(),
                no_resolve: true,
            }],
            "1.1.1.1:53".parse().unwrap(),
            None,
        )
        .unwrap(),
    );
    let (out, mut incoming) = mpsc::channel(16);
    let mut manager = crate::awg_packets::Manager::new(router, out);
    let packet = crate::awg_packets::udp_packet(
        "172.19.0.1:55000".parse().unwrap(),
        "10.7.0.1:443".parse().unwrap(),
        b"fixture",
    );
    assert!(manager.process(&packet).await.unwrap().is_none());
    assert!(
        tokio::time::timeout(Duration::from_secs(3), incoming.recv())
            .await
            .unwrap()
            .is_some()
    );
    lab.connector.session.task.abort();
    tokio::time::timeout(Duration::from_secs(3), async {
        while !lab.connector.session.tx.is_closed()
            || !lab.connector.session.flows.lock().unwrap().is_empty()
        {
            tokio::task::yield_now().await;
        }
    })
    .await
    .unwrap();
    assert_eq!(
        manager.process(&packet).await.unwrap_err().kind(),
        io::ErrorKind::BrokenPipe
    );
    drop(manager);
    drop(incoming);
    assert_eq!(lab.connector.session.packet_budget.used(), 0);
}
