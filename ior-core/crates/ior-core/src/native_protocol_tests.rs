use super::*;
use ior_vmess_codec::tcp_handler::{TcpServerHandler, TcpServerSetupResult};
use tokio::net::TcpListener;

fn profile(protocol: &str, port: u16) -> WireProfile {
    serde_json::from_value(
        serde_json::json!({"protocol":protocol,"server":"127.0.0.1","port":port,
        "credential":"test-password","serverName":"localhost","publicKey":null,"shortId":null,
        "options":{"transport":"tcp","tls":false,"method":"aes-128-gcm"}}),
    )
    .unwrap()
}
async fn echo_test(connector: &NativeProtocolConnector) {
    let mut stream = connector.connect("echo.test".into(), 443).await.unwrap();
    let bytes = vec![0x5a; 96 * 1024];
    stream.write_all(&bytes).await.unwrap();
    stream.flush().await.unwrap();
    let mut received = vec![0; bytes.len()];
    tokio::time::timeout(
        std::time::Duration::from_secs(8),
        stream.read_exact(&mut received),
    )
    .await
    .unwrap()
    .unwrap();
    assert_eq!(bytes, received);
}
#[tokio::test]
async fn vmess_tcp_and_udp_aead_round_trip() {
    for method in ["aes-128-gcm", "chacha20-poly1305", "none"] {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let mut p = profile("vmess", listener.local_addr().unwrap().port());
        p.credential = "00112233-4455-6677-8899-aabbccddeeff".into();
        p.options.method = Some(method.into());
        let server_p = p.clone();
        let server = tokio::spawn(async move {
            for _ in 0..2 {
                let (socket, _) = listener.accept().await.unwrap();
                let handler = ior_vmess_codec::vmess::VmessTcpServerHandler::new(
                    server_p.options.method.as_deref().unwrap(),
                    &server_p.credential,
                    true,
                );
                match handler
                    .setup_server_stream(Box::new(VmAdapter(Box::new(socket))))
                    .await
                    .unwrap()
                {
                    TcpServerSetupResult::TcpForward { mut stream, .. } => {
                        let mut data = vec![0; 96 * 1024];
                        stream.read_exact(&mut data).await.unwrap();
                        stream.write_all(&data).await.unwrap();
                        stream.flush().await.unwrap();
                    }
                    TcpServerSetupResult::BidirectionalUdp { mut stream, .. } => {
                        for _ in 0..3 {
                            let mut data = vec![0; 65535];
                            let mut buf = ReadBuf::new(&mut data);
                            futures::future::poll_fn(|cx| {
                                Pin::new(&mut stream).poll_read_message(cx, &mut buf)
                            })
                            .await
                            .unwrap();
                            let len = buf.filled().len();
                            futures::future::poll_fn(|cx| {
                                Pin::new(&mut stream).poll_write_message(cx, &data[..len])
                            })
                            .await
                            .unwrap();
                            futures::future::poll_fn(|cx| {
                                Pin::new(&mut stream).poll_flush_message(cx)
                            })
                            .await
                            .unwrap();
                        }
                    }
                    _ => panic!("wrong command"),
                }
            }
        });
        let c = NativeProtocolConnector::new(&p, "1.1.1.1:53".parse().unwrap(), None).unwrap();
        echo_test(&c).await;
        let mut udp = c.udp("echo.test".into(), 53).await.unwrap();
        for len in [12, 1200, 4000] {
            let data = vec![7; len];
            udp.send(&data).await.unwrap();
            assert_eq!(
                tokio::time::timeout(std::time::Duration::from_secs(5), udp.receive())
                    .await
                    .unwrap()
                    .unwrap(),
                data
            );
        }
        server.await.unwrap();
    }
}
#[tokio::test]
async fn shadowsocks_tcp_udp_and_2022_sessions() {
    use shadowsocks::{
        config::ServerType,
        context::Context,
        relay::udprelay::{
            options::UdpSocketControlData,
            proxy_socket::{ProxySocket, UdpSocketType},
        },
    };
    for method in [
        "aes-128-gcm",
        "aes-256-gcm",
        "chacha20-ietf-poly1305",
        "2022-blake3-aes-128-gcm",
        "2022-blake3-aes-256-gcm",
        "2022-blake3-chacha20-poly1305",
    ] {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let mut p = profile("shadowsocks", port);
        p.options.method = Some(method.into());
        if method.starts_with("2022") {
            use base64::Engine;
            p.credential = base64::engine::general_purpose::STANDARD.encode(vec![
                3;
                if method.contains("aes-128") {
                    16
                } else {
                    32
                }
            ]);
        }
        let config = ss_config(&p, listener.local_addr().unwrap()).unwrap();
        let server_config = config.clone();
        let server = tokio::spawn(async move {
            let (socket, _) = listener.accept().await.unwrap();
            let mut stream =
                shadowsocks::relay::tcprelay::proxy_stream::ProxyServerStream::from_stream(
                    Context::new_shared(ServerType::Server),
                    socket,
                    server_config.method(),
                    server_config.key(),
                );
            stream.handshake().await.unwrap();
            let mut bytes = vec![0; 96 * 1024];
            stream.read_exact(&mut bytes).await.unwrap();
            stream.write_all(&bytes).await.unwrap();
            stream.flush().await.unwrap();
        });
        let udp_socket = tokio::net::UdpSocket::bind((std::net::Ipv4Addr::LOCALHOST, port))
            .await
            .unwrap();
        let socket = ProxySocket::from_socket(
            UdpSocketType::Server,
            Context::new_shared(ServerType::Server),
            &config,
            shadowsocks::net::UdpSocket::from(udp_socket),
        );
        let udp_server = tokio::spawn(async move {
            for packet in 1..=3 {
                let mut buf = vec![0; 65536];
                let (n, peer, address, _, control) =
                    socket.recv_from_with_ctrl(&mut buf).await.unwrap();
                let mut reply = UdpSocketControlData::default();
                reply.server_session_id = 42;
                reply.packet_id = packet;
                reply.client_session_id = control.map(|c| c.client_session_id).unwrap_or(0);
                socket
                    .send_to_with_ctrl(peer, &address, &reply, &buf[..n])
                    .await
                    .unwrap();
            }
        });
        let c = NativeProtocolConnector::new(&p, "1.1.1.1:53".parse().unwrap(), None).unwrap();
        echo_test(&c).await;
        server.await.unwrap();
        let mut udp = c.udp("echo.test".into(), 53).await.unwrap();
        for len in [32, 1200, 4000] {
            let bytes = vec![9; len];
            udp.send(&bytes).await.unwrap();
            assert_eq!(
                tokio::time::timeout(std::time::Duration::from_secs(5), udp.receive())
                    .await
                    .unwrap()
                    .unwrap(),
                bytes
            );
        }
        udp_server.await.unwrap();
    }
}
#[tokio::test]
async fn trojan_verified_tls_tcp_and_fragmented_udp() {
    let cert = rcgen::generate_simple_self_signed(vec!["localhost".into()]).unwrap();
    let der = cert.cert.der().clone();
    let mut roots = tokio_rustls::rustls::RootCertStore::empty();
    roots.add(der.clone()).unwrap();
    let server_config = tokio_rustls::rustls::ServerConfig::builder()
        .with_no_client_auth()
        .with_single_cert(
            vec![der],
            tokio_rustls::rustls::pki_types::PrivatePkcs8KeyDer::from(
                cert.signing_key.serialize_der(),
            )
            .into(),
        )
        .unwrap();
    let acceptor = tokio_rustls::TlsAcceptor::from(Arc::new(server_config));
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mut p = profile("trojan", listener.local_addr().unwrap().port());
    p.options.tls = Some(true);
    let server_p = p.clone();
    let server = tokio::spawn(async move {
        for command in [1, 3] {
            let (socket, _) = listener.accept().await.unwrap();
            let mut stream = acceptor.accept(socket).await.unwrap();
            let expected = trojan_header(
                &server_p.credential,
                command,
                if command == 1 { "echo.test" } else { "0.0.0.0" },
                if command == 1 { 443 } else { 1 },
            )
            .unwrap();
            let mut header = vec![0; expected.len()];
            stream.read_exact(&mut header).await.unwrap();
            assert_eq!(header, expected);
            if command == 1 {
                let mut data = vec![0; 96 * 1024];
                stream.read_exact(&mut data).await.unwrap();
                stream.write_all(&data).await.unwrap();
            } else {
                let address = crate::udp_relay::encode_address("echo.test", 53).unwrap();
                for _ in 0..3 {
                    let mut received = vec![0; address.len()];
                    stream.read_exact(&mut received).await.unwrap();
                    assert_eq!(received, address);
                    let len = stream.read_u16().await.unwrap();
                    assert_eq!(stream.read_u16().await.unwrap(), 0x0d0a);
                    let mut data = vec![0; len as usize];
                    stream.read_exact(&mut data).await.unwrap();
                    let mut frame = address.clone();
                    frame.extend(len.to_be_bytes());
                    frame.extend(b"\r\n");
                    frame.extend(data);
                    for chunk in frame.chunks(17) {
                        stream.write_all(chunk).await.unwrap();
                        stream.flush().await.unwrap();
                    }
                }
            }
        }
    });
    let mut c = NativeProtocolConnector::new(&p, "1.1.1.1:53".parse().unwrap(), None).unwrap();
    c.tls = tokio_rustls::TlsConnector::from(Arc::new(
        tokio_rustls::rustls::ClientConfig::builder()
            .with_root_certificates(roots)
            .with_no_client_auth(),
    ));
    echo_test(&c).await;
    let mut udp = c.udp("echo.test".into(), 53).await.unwrap();
    for len in [32, 1200, 4000] {
        let data = vec![2; len];
        udp.send(&data).await.unwrap();
        assert_eq!(
            tokio::time::timeout(std::time::Duration::from_secs(5), udp.receive())
                .await
                .unwrap()
                .unwrap(),
            data
        );
    }
    server.await.unwrap();
}
#[test]
fn udp_replay_window_accepts_reordering_not_replay() {
    let mut w = (0, 0);
    assert!(accept_packet(&mut w, 100));
    assert!(accept_packet(&mut w, 102));
    assert!(accept_packet(&mut w, 101));
    assert!(!accept_packet(&mut w, 101));
    assert!(!accept_packet(&mut w, 1));
}
#[test]
fn unsupported_transport_rejected_before_socket() {
    let mut p = profile("trojan", 443);
    p.options.tls = Some(true);
    p.options.transport = Some("grpc".into());
    assert!(NativeProtocolConnector::new(&p, "1.1.1.1:53".parse().unwrap(), None).is_err());
}
/// Independent reference engine used only by opt-in tests, never native runtime.
#[tokio::test]
async fn optional_xray_reference_tcp_udp_interoperability() {
    let Ok(binary) = std::env::var("IOR_TEST_XRAY") else {
        return;
    };
    for protocol in ["vmess", "shadowsocks", "trojan"] {
        let reservation = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = reservation.local_addr().unwrap().port();
        drop(reservation);
        let tcp = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let tcp_port = tcp.local_addr().unwrap().port();
        let udp = tokio::net::UdpSocket::bind("127.0.0.1:0").await.unwrap();
        let udp_port = udp.local_addr().unwrap().port();
        let tcp_echo = tokio::spawn(async move {
            let (mut socket, _) = tcp.accept().await.unwrap();
            let mut bytes = vec![0; 32768];
            socket.read_exact(&mut bytes).await.unwrap();
            socket.write_all(&bytes).await.unwrap();
        });
        let udp_echo = tokio::spawn(async move {
            let mut bytes = vec![0; 65535];
            for _ in 0..3 {
                let (n, peer) = udp.recv_from(&mut bytes).await.unwrap();
                udp.send_to(&bytes[..n], peer).await.unwrap();
            }
        });
        let mut random = [0; 16];
        getrandom::getrandom(&mut random).unwrap();
        let directory =
            std::env::temp_dir().join(format!("ior-reference-{}", uuid::Uuid::from_bytes(random)));
        std::fs::create_dir(&directory).unwrap();
        let mut p = profile(protocol, port);
        if protocol == "vmess" {
            p.credential = "00112233-4455-6677-8899-aabbccddeeff".into();
        }
        let settings = match protocol {
            "vmess" => serde_json::json!({"clients":[{"id":p.credential,"alterId":0}]}),
            "shadowsocks" => {
                serde_json::json!({"method":"aes-128-gcm","password":p.credential,"network":"tcp,udp"})
            }
            _ => serde_json::json!({"clients":[{"password":p.credential}]}),
        };
        let mut inbound = serde_json::json!({"listen":"127.0.0.1","port":port,"protocol":protocol,"settings":settings});
        if protocol == "trojan" {
            p.options.tls = Some(true);
        }
        let mut connector =
            NativeProtocolConnector::new(&p, "1.1.1.1:53".parse().unwrap(), None).unwrap();
        if protocol == "trojan" {
            let cert = rcgen::generate_simple_self_signed(vec!["localhost".into()]).unwrap();
            let mut roots = tokio_rustls::rustls::RootCertStore::empty();
            roots.add(cert.cert.der().clone()).unwrap();
            connector.tls = tokio_rustls::TlsConnector::from(Arc::new(
                tokio_rustls::rustls::ClientConfig::builder()
                    .with_root_certificates(roots)
                    .with_no_client_auth(),
            ));
            connector.profile.options.tls = Some(true);
            let cert_path = directory.join("cert.pem");
            let key_path = directory.join("key.pem");
            std::fs::write(&cert_path, cert.cert.pem()).unwrap();
            std::fs::write(&key_path, cert.signing_key.serialize_pem()).unwrap();
            inbound["streamSettings"] = serde_json::json!({"network":"tcp","security":"tls","tlsSettings":{"certificates":[{"certificateFile":cert_path,"keyFile":key_path}]}});
        }
        let config = directory.join("reference.json");
        std::fs::write(&config,serde_json::to_vec(&serde_json::json!({"log":{"loglevel":"none"},"inbounds":[inbound],"outbounds":[{"protocol":"freedom","settings":{"finalRules":[{"action":"allow","ip":["127.0.0.1/32"],"port":format!("{tcp_port},{udp_port}"),"network":"tcp,udp"}]}}]})).unwrap()).unwrap();
        let mut command = tokio::process::Command::new(&binary);
        command
            .args(["run", "-c"])
            .arg(&config)
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .kill_on_drop(true);
        #[cfg(windows)]
        command.creation_flags(0x08000000);
        let mut child = command.spawn().unwrap();
        tokio::time::timeout(std::time::Duration::from_secs(5), async {
            loop {
                if tokio::net::TcpStream::connect((std::net::Ipv4Addr::LOCALHOST, port))
                    .await
                    .is_ok()
                {
                    break;
                }
                if child.try_wait().unwrap().is_some() {
                    panic!("reference config rejected");
                }
                tokio::time::sleep(std::time::Duration::from_millis(25)).await;
            }
        })
        .await
        .unwrap();
        let mut relay = connector
            .connect("127.0.0.1".into(), tcp_port)
            .await
            .unwrap();
        let data = vec![6; 32768];
        relay.write_all(&data).await.unwrap();
        relay.flush().await.unwrap();
        let mut received = vec![0; data.len()];
        tokio::time::timeout(
            std::time::Duration::from_secs(5),
            relay.read_exact(&mut received),
        )
        .await
        .unwrap()
        .unwrap();
        assert_eq!(data, received);
        tcp_echo.await.unwrap();
        let mut relay = connector.udp("127.0.0.1".into(), udp_port).await.unwrap();
        for len in [32, 1200, 4000] {
            let data = vec![5; len];
            relay.send(&data).await.unwrap();
            assert_eq!(
                tokio::time::timeout(std::time::Duration::from_secs(5), relay.receive())
                    .await
                    .unwrap()
                    .unwrap(),
                data
            );
        }
        udp_echo.await.unwrap();
        child.kill().await.unwrap();
        child.wait().await.unwrap();
        for name in ["reference.json", "cert.pem", "key.pem"] {
            let path = directory.join(name);
            if path.exists() {
                std::fs::remove_file(path).unwrap();
            }
        }
        std::fs::remove_dir(directory).unwrap();
    }
}
