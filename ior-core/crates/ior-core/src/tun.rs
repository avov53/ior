#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Stack {
    Gvisor,
    System,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TunPlan {
    pub stack: Stack,
    pub mtu: u16,
    pub auto_route: bool,
}

impl Default for TunPlan {
    fn default() -> Self {
        Self {
            stack: Stack::Gvisor,
            mtu: 1500,
            auto_route: true,
        }
    }
}

#[cfg(windows)]
pub async fn start(
    options: &crate::ipc_contract::WireTun,
    connector: std::sync::Arc<crate::native_router::NativeRouter>,
    dns_options: &crate::ipc_contract::WireDns,
) -> std::io::Result<tokio::task::JoinHandle<std::io::Result<()>>> {
    let dns = std::sync::Arc::new(crate::tun_dns::TunDns::new(connector.clone(), dns_options)?);
    start_with_dns(options, connector, dns).await
}

#[cfg(windows)]
pub(crate) async fn start_with_dns(
    options: &crate::ipc_contract::WireTun,
    connector: std::sync::Arc<crate::native_router::NativeRouter>,
    dns: std::sync::Arc<crate::tun_dns::TunDns>,
) -> std::io::Result<tokio::task::JoinHandle<std::io::Result<()>>> {
    use futures::{SinkExt, StreamExt};
    use std::{io, sync::Arc};
    if !options.name.starts_with("ior-native-")
        || options.name.len() > 40
        || !(1280..=1500).contains(&options.mtu)
        || !std::path::Path::new(&options.wintun_path).is_absolute()
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "TUN_PARAMETERS_INVALID",
        ));
    }
    netstack_smoltcp::tcp_diagnostics::set_observer(|event| {
        crate::trace::emit(
            serde_json::json!({"stage":"tun","code":"TUN_TCP_SOCKET_METRICS","side":"tun","source":event.peer.to_string(),"destination":event.destination.to_string(),"transport":"tcp","operation":if event.final_report{"final"}else{"periodic"},"sentBytes":event.sent_bytes,"receivedBytes":event.received_bytes,"rttEstimateMs":event.socket.rtt_estimate_ms,"rxWindowBytes":event.socket.receive_window_bytes,"peerWindowBytes":event.socket.peer_window_bytes,"rxQueuedBytes":event.socket.receive_queue_bytes,"txQueuedBytes":event.socket.send_queue_bytes,"rxZeroWindowUs":event.receive_zero_us,"peerZeroWindowUs":event.peer_zero_us,"applicationWaitUs":event.application_wait_us,"reassemblyDroppedPackets":event.socket.reassembly_dropped_packets}),
        );
    });
    // C# assigns addresses, DNS and routes after it has journaled ownership.
    let adapter_start = std::time::Instant::now();
    let device = Arc::new(
        tun_rs::DeviceBuilder::new()
            .name(&options.name)
            .mtu(options.mtu)
            .wintun_file(options.wintun_path.clone())
            .build_async()?,
    );
    let (stack, runner, udp, tcp) = netstack_smoltcp::StackBuilder::default()
        .enable_tcp(true)
        .enable_udp(true)
        .enable_icmp(true)
        .mtu(options.mtu as usize)
        .build()?;
    crate::trace::emit(
        serde_json::json!({"stage":"tun","code":"TUN_ADAPTER_CREATED","durationMs":adapter_start.elapsed().as_millis() as u64}),
    );
    Ok(tokio::spawn(async move {
        let mut tasks = tokio::task::JoinSet::<io::Result<()>>::new();
        let (mut sink, mut stream) = stack.split();
        let reader = device.clone();
        tasks.spawn(async move {
            let mut buffer = vec![0u8; 65535];
            loop {
                let n = reader.recv(&mut buffer).await?;
                if crate::egress_registry::recaptured(&buffer[..n]) {
                    crate::trace::code("tun", "TUN_EGRESS_RECAPTURED");
                    return Err(io::Error::new(
                        io::ErrorKind::ConnectionAborted,
                        "TUN_EGRESS_RECAPTURED",
                    ));
                }
                sink.send(buffer[..n].to_vec()).await?;
            }
        });
        let writer = device.clone();
        tasks.spawn(async move {
            while let Some(packet) = stream.next().await {
                writer.send(&packet?).await?;
            }
            Err(io::Error::new(
                io::ErrorKind::BrokenPipe,
                "TUN_STACK_CLOSED",
            ))
        });
        if let Some(runner) = runner {
            tasks.spawn(runner);
        }
        let tcp_connector = connector.clone();
        let tcp_dns = dns.clone();
        tasks.spawn(async move {
            let mut listener = tcp.ok_or(io::ErrorKind::InvalidInput)?;
            let mut clients = tokio::task::JoinSet::new();
            let semaphore = Arc::new(tokio::sync::Semaphore::new(256));
            while let Some((stream, source, remote)) = listener.next().await {
                while clients.try_join_next().is_some() {}
                let Ok(permit) = semaphore.clone().try_acquire_owned() else {
                    crate::trace::code("tun", "TUN_TCP_CAPACITY_REACHED");
                    continue;
                };
                let connector = tcp_connector.clone();
                let dns = tcp_dns.clone();
                clients.spawn(
                    crate::trace::FLOW.scope(crate::trace::new_flow(), async move {
                        let _permit = permit;
                        crate::trace::emit(serde_json::json!({"stage":"tun","code":"TUN_TCP_ACCEPTED","source":source.to_string(),"destination":remote.to_string()}));
                        let result = relay_tcp(stream, remote, connector, dns).await;
                        if let Err(error) = &result {
                            emit_flow_error_destination("TUN_TCP_FAILED", error,Some(remote));
                        }
                        result
                    }),
                );
            }
            Err(io::Error::new(
                io::ErrorKind::BrokenPipe,
                "TUN_TCP_STACK_CLOSED",
            ))
        });
        let reject_device = device.clone();
        tasks.spawn(async move {
            use std::{collections::HashMap,net::SocketAddr,time::Duration};
            let (mut incoming,mut outgoing)=udp.ok_or(io::ErrorKind::InvalidInput)?.split();
            let (responses,mut response_rx)=tokio::sync::mpsc::channel::<(Vec<u8>,SocketAddr,SocketAddr)>(256);
            let mut flows=HashMap::<(SocketAddr,SocketAddr),tokio::sync::mpsc::Sender<Vec<u8>>>::new();
            let mut workers=tokio::task::JoinSet::new();
            let dns_capacity=Arc::new(tokio::sync::Semaphore::new(32));
            let reject_budget=Arc::new(std::sync::Mutex::new((std::time::Instant::now(),0u32)));
            loop {
                while workers.try_join_next().is_some() {}
                flows.retain(|_,sender|!sender.is_closed());
                tokio::select! {
                    response=response_rx.recv()=>if let Some(response)=response {outgoing.send(response).await?;},
                    received=incoming.next()=>{
                        let Some((data,source,destination))=received else {return Err(io::ErrorKind::BrokenPipe.into());};
                        if destination.port()==53 {
                            let Ok(permit)=dns_capacity.clone().try_acquire_owned() else{crate::trace::code("dns","TUN_DNS_CAPACITY_REACHED");continue;};
                            let dns=dns.clone();let responses=responses.clone();
                            workers.spawn(crate::trace::FLOW.scope(crate::trace::new_flow(),async move {
                                let _permit=permit;crate::trace::code("dns","TUN_DNS_QUERY_ACCEPTED");
                                match dns.query(&data,false).await {
                                    Ok(response)=>responses.send((response,destination,source)).await.map_err(|_|io::ErrorKind::BrokenPipe.into()),
                                    Err(error)=>{
                                        emit_flow_error("TUN_DNS_FAILED",&error);
                                        if let Ok(question)=crate::tun_names::question(&data) {
                                            let response=crate::tun_names::empty_response(&data,&question,2);
                                            responses.send((response,destination,source)).await.map_err(|_|io::ErrorKind::BrokenPipe)?;
                                        }
                                        Err(error)
                                    }
                                }
                            }));continue;
                        }
                        let key=(source,destination);
                        if flows.get(&key).is_some_and(|sender|sender.is_closed()) {flows.remove(&key);}
                        if !flows.contains_key(&key) {
                            if flows.len()>=512 {crate::trace::code("tun","TUN_UDP_CAPACITY_REACHED");continue;}
                            let (sender,mut receiver)=tokio::sync::mpsc::channel::<Vec<u8>>(64);
                            flows.insert(key,sender);
                            let connector=connector.clone();let responses=responses.clone();let name=dns.names.lookup(destination.ip());let names=dns.names.clone();
                            let reject_device=reject_device.clone();let reject_budget=reject_budget.clone();let quote=crate::tun_reject::udp_quote(source,destination,&data);
                            workers.spawn(crate::trace::FLOW.scope(crate::trace::new_flow(),async move {
                                crate::trace::code("tun","TUN_UDP_ACCEPTED");
                                crate::trace::emit(serde_json::json!({"stage":"routing","code":"TUN_UDP_NAME_SOURCE","nameSource":if name.is_some(){"dns_cache"}else{"unavailable"},"addressFamily":if destination.is_ipv6(){"ipv6"}else{"ipv4"}}));
                                names.link_flow(destination.ip(),name.as_deref());
                                let result=async {
                                let mut relay=tokio::time::timeout(Duration::from_secs(15),connector.udp_destination(destination,name)).await.map_err(|_|io::ErrorKind::TimedOut)??;
                                let mut sent=0u64;let mut received=0u64;
                                let mut first_sent=None;
                                let mut first_received=false;
                                loop {tokio::select! {
                                    data=receiver.recv()=>match data{Some(data)=>{relay.send(&data).await?;sent+=data.len() as u64;first_sent.get_or_insert_with(std::time::Instant::now);},None=>return Ok::<(),io::Error>(())},
                                    data=tokio::time::timeout(Duration::from_secs(60),relay.receive())=>{
                                        let data=match data {Ok(result)=>result?,Err(_)=>{crate::trace::emit(serde_json::json!({"stage":"relay","code":"TUN_UDP_IDLE_CLOSED","sentBytes":sent,"receivedBytes":received}));return Ok(());}};
                                        if !first_received {first_received=true;crate::trace::emit(serde_json::json!({"stage":"relay","code":"TUN_UDP_FIRST_REMOTE_BYTE","durationMs":first_sent.map(|started|started.elapsed().as_millis() as u64),"receivedBytes":data.len()}));}
                                        received+=data.len() as u64;
                                        responses.send((data,destination,source)).await.map_err(|_|io::ErrorKind::BrokenPipe)?;
                                    }
                                }}
                                }.await;
                                if let Err(error)=&result{
                                    emit_flow_error_destination("TUN_UDP_FAILED",error,Some(destination));
                                    let allowed={let mut budget=reject_budget.lock().map_err(|_|io::ErrorKind::Other)?;if budget.0.elapsed()>=Duration::from_secs(1){*budget=(std::time::Instant::now(),0);}budget.1+=1;budget.1<=32};
                                    if allowed {if let Some(packet)=quote.as_deref().and_then(crate::tun_reject::udp_unreachable_quote){reject_device.send(&packet).await?;crate::trace::code("tun","TUN_UDP_NETWORK_REJECTED");}}
                                }result
                            }));
                        }
                        if let Some(sender)=flows.get(&key) {if matches!(sender.try_send(data),Err(tokio::sync::mpsc::error::TrySendError::Full(_))){crate::tunnel_metrics::TUN_UDP_DROPPED.fetch_add(1,std::sync::atomic::Ordering::Relaxed);}}
                    }
                }
            }
        });
        let result = tasks
            .join_next()
            .await
            .ok_or(io::ErrorKind::BrokenPipe)?
            .map_err(io::Error::other)?;
        tasks.abort_all();
        while tasks.join_next().await.is_some() {}
        drop(device);
        crate::trace::code("tun", "TUN_ADAPTER_STOPPED");
        result
    }))
}

#[cfg(not(windows))]
pub async fn start(
    _: &crate::ipc_contract::WireTun,
    _: std::sync::Arc<crate::native_router::NativeRouter>,
    _: &crate::ipc_contract::WireDns,
) -> std::io::Result<tokio::task::JoinHandle<std::io::Result<()>>> {
    Err(std::io::Error::new(
        std::io::ErrorKind::Unsupported,
        "WINDOWS_TUN_REQUIRED",
    ))
}

fn emit_flow_error(code: &str, error: &std::io::Error) {
    emit_flow_error_destination(code, error, None)
}
fn emit_flow_error_destination(
    code: &str,
    error: &std::io::Error,
    destination: Option<std::net::SocketAddr>,
) {
    let raw = error.to_string();
    let reason = if raw.len() <= 80
        && raw
            .bytes()
            .all(|b| b.is_ascii_uppercase() || b.is_ascii_digit() || b == b'_')
    {
        raw
    } else {
        format!("{:?}", error.kind())
    };
    crate::trace::emit(
        serde_json::json!({"stage":"tun","code":code,"kind":format!("{:?}",error.kind()),"reason":reason,"destination":destination.map(|address|address.to_string()),"classification":if reason=="TUNNEL_IPV6_LOCAL_ADDRESS_REQUIRED" || reason=="TUNNEL_IPV4_LOCAL_ADDRESS_REQUIRED" {"ADDRESS_FAMILY_UNAVAILABLE"}else{"FLOW_IO_FAILURE"}}),
    );
}

async fn relay_tcp<S: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin>(
    stream: S,
    remote: std::net::SocketAddr,
    router: std::sync::Arc<crate::native_router::NativeRouter>,
    dns: std::sync::Arc<crate::tun_dns::TunDns>,
) -> std::io::Result<()> {
    use std::{io, time::Duration};
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    let mut stream = crate::relay_diagnostics::Observed::new(stream, "application", remote);
    if remote.port() == 53 {
        loop {
            let length =
                match tokio::time::timeout(Duration::from_secs(15), stream.read_u16()).await {
                    Ok(Ok(length)) => length as usize,
                    Ok(Err(error)) if error.kind() == io::ErrorKind::UnexpectedEof => return Ok(()),
                    Ok(Err(error)) => return Err(error),
                    Err(_) => return Ok(()),
                };
            if !(12..=65535).contains(&length) {
                return Err(io::ErrorKind::InvalidData.into());
            }
            let mut query = vec![0; length];
            tokio::time::timeout(Duration::from_secs(5), stream.read_exact(&mut query))
                .await
                .map_err(|_| io::ErrorKind::TimedOut)??;
            let response = dns.query(&query, true).await?;
            stream.write_u16(response.len() as u16).await?;
            stream.write_all(&response).await?;
        }
    }
    let mut prefix = Vec::new();
    let deadline = tokio::time::Instant::now() + Duration::from_millis(200);
    let mut name = None;
    loop {
        let mut buffer = [0u8; 4096];
        match tokio::time::timeout_at(deadline, stream.read(&mut buffer)).await {
            Ok(Ok(0)) => return Ok(()),
            Ok(Ok(length)) => prefix.extend_from_slice(&buffer[..length]),
            Ok(Err(error)) => return Err(error),
            Err(_) => break,
        }
        match crate::tun_names::sniff(&prefix) {
            crate::tun_names::Sniff::Name(found) => {
                name = Some(found);
                crate::trace::code("routing", "TUN_NAME_SNI_HTTP");
                break;
            }
            crate::tun_names::Sniff::Unknown => break,
            crate::tun_names::Sniff::NeedMore if prefix.len() < 32768 => continue,
            _ => break,
        }
    }
    if name.is_none() {
        name = dns.names.lookup(remote.ip());
        if name.is_some() {
            crate::trace::code("routing", "TUN_NAME_DNS_CACHE");
        } else {
            crate::trace::code("routing", "TUN_NAME_UNAVAILABLE");
        }
    }
    dns.names.link_flow(remote.ip(), name.as_deref());
    let outbound = tokio::time::timeout(
        Duration::from_secs(15),
        router.connect_destination(remote, name),
    )
    .await
    .map_err(|_| io::ErrorKind::TimedOut)??;
    let mut outbound = crate::relay_diagnostics::Observed::new(outbound, "transport", remote);
    outbound.write_all(&prefix).await?;
    outbound.flush().await?;
    crate::trace::code("relay", "TUN_TCP_RELAY_READY");
    let (sent, received) = tokio::io::copy_bidirectional(&mut stream, &mut outbound).await?;
    crate::trace::emit(
        serde_json::json!({"stage":"relay","code":"TUN_TCP_RELAY_CLOSED","sentBytes":sent+prefix.len() as u64,"receivedBytes":received}),
    );
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        ipc_contract::{WireDns, WireRule},
        native_router::NativeRouter,
        proxy_inbound::{ConnectFuture, OutboundConnector},
    };
    use std::{io, sync::Arc};
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    struct Forbidden;
    impl OutboundConnector for Forbidden {
        fn connect(&self, _: String, _: u16) -> ConnectFuture {
            Box::pin(async { Err(io::Error::other("proxy must not be used")) })
        }
    }
    fn rule(kind: &str, value: &str, target: &str) -> WireRule {
        WireRule {
            kind: kind.into(),
            value: value.into(),
            target: target.into(),
            no_resolve: true,
            source: "ior".into(),
        }
    }
    fn dns(router: Arc<NativeRouter>) -> Arc<crate::tun_dns::TunDns> {
        Arc::new(
            crate::tun_dns::TunDns::new(
                router,
                &WireDns {
                    bootstrap: "127.0.0.1".into(),
                    upstream: "8.8.8.8".into(),
                    ipv4_only: true,
                    upstreams: vec![],
                },
            )
            .unwrap(),
        )
    }
    #[tokio::test]
    async fn tun_http_prefix_is_preserved_and_direct_uses_original_ip() {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let destination = listener.local_addr().unwrap();
        let router = Arc::new(
            NativeRouter::new(
                Arc::new(Forbidden),
                vec![
                    rule("DOMAIN", "cdn.example", "DIRECT"),
                    rule("MATCH", "", "REJECT"),
                ],
                "127.0.0.1:9".parse().unwrap(),
                None,
            )
            .unwrap(),
        );
        let (mut app, stream) = tokio::io::duplex(1024);
        let resolver = dns(router.clone());
        let worker = tokio::spawn(relay_tcp(stream, destination, router, resolver));
        app.write_all(b"GET / HTTP/1.1\r\nHo").await.unwrap();
        tokio::task::yield_now().await;
        app.write_all(b"st: cdn.example\r\n\r\n").await.unwrap();
        let expected = b"GET / HTTP/1.1\r\nHost: cdn.example\r\n\r\n";
        let (mut remote, _) = listener.accept().await.unwrap();
        let mut request = vec![0; expected.len()];
        remote.read_exact(&mut request).await.unwrap();
        assert_eq!(request, expected);
        remote.write_all(b"response").await.unwrap();
        remote.shutdown().await.unwrap();
        let mut response = vec![0; 8];
        app.read_exact(&mut response).await.unwrap();
        assert_eq!(response, b"response");
        app.shutdown().await.unwrap();
        worker.await.unwrap().unwrap();
    }
    #[tokio::test]
    async fn tun_name_rule_rejects_without_opening_an_outbound() {
        let router = Arc::new(
            NativeRouter::new(
                Arc::new(Forbidden),
                vec![
                    rule("DOMAIN", "blocked.example", "REJECT"),
                    rule("MATCH", "", "PROXY"),
                ],
                "127.0.0.1:9".parse().unwrap(),
                None,
            )
            .unwrap(),
        );
        let (mut app, stream) = tokio::io::duplex(1024);
        let resolver = dns(router.clone());
        let worker = tokio::spawn(relay_tcp(
            stream,
            "203.0.113.7:80".parse().unwrap(),
            router,
            resolver,
        ));
        app.write_all(b"GET / HTTP/1.1\r\nHost: blocked.example\r\n\r\n")
            .await
            .unwrap();
        assert_eq!(
            worker.await.unwrap().unwrap_err().kind(),
            io::ErrorKind::PermissionDenied
        );
    }
}
