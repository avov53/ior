use ior_core::{
    ipc_contract::StartRequest, profile_parser, proxy_inbound, runtime,
    vless_tls::VlessTlsConnector,
};
use std::{
    io::{self, Read},
    sync::Arc,
};

async fn serve_from_stdin(port: u16) -> io::Result<()> {
    if port == 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "invalid local port",
        ));
    }
    let mut payload = Vec::new();
    io::stdin().take(1024 * 1024).read_to_end(&mut payload)?;
    let request: StartRequest = serde_json::from_slice(&payload).map_err(|_| {
        io::Error::new(
            io::ErrorKind::InvalidInput,
            "invalid private control payload",
        )
    })?;
    request
        .validate_shape()
        .map_err(|message| io::Error::new(io::ErrorKind::InvalidInput, message))?;
    if request.profile.protocol != "vless_tls"
        || request.routes.len() != 1
        || request.routes[0].kind != "MATCH"
        || request.routes[0].target != "PROXY"
    {
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "native Proxy preview supports VLESS/TLS and MATCH to PROXY only",
        ));
    }
    let resolver: std::net::IpAddr =
        request.dns.bootstrap.parse().map_err(|_| {
            io::Error::new(io::ErrorKind::InvalidInput, "bootstrap DNS must be an IP")
        })?;
    let connector = Arc::new(VlessTlsConnector::from_profile(
        &request.profile,
        std::net::SocketAddr::new(resolver, 53),
    )?);
    let listener = tokio::net::TcpListener::bind(("127.0.0.1", port)).await?;
    println!("READY {}", listener.local_addr()?.port());
    proxy_inbound::serve_local(listener, connector).await
}

#[tokio::main]
async fn main() {
    let arguments: Vec<String> = std::env::args().collect();
    match arguments.get(1).map(String::as_str) {
        Some("--version") => println!(
            "ior-core {} (native Proxy preview)",
            env!("CARGO_PKG_VERSION")
        ),
        Some("serve-stdin") => {
            let port = arguments
                .get(2)
                .and_then(|value| value.parse::<u16>().ok())
                .unwrap_or(0);
            if let Err(error) = serve_from_stdin(port).await {
                eprintln!("ior-core start failed: {error}");
                std::process::exit(2);
            }
        }
        Some("control-stdin") => {
            let port = arguments
                .get(2)
                .and_then(|value| value.parse::<u16>().ok())
                .unwrap_or(0);
            let mut reader = tokio::io::BufReader::new(tokio::io::stdin());
            let mut writer = tokio::io::stdout();
            if let Err(error) =
                ior_core::control_channel::serve(&mut reader, &mut writer, port).await
            {
                eprintln!("ior-core control failed: {:?}", error.kind());
                std::process::exit(2);
            }
        }
        Some("inspect-stdin") => {
            let mut link = String::new();
            if io::stdin()
                .take(16 * 1024)
                .read_to_string(&mut link)
                .is_err()
            {
                eprintln!("could not read private profile input");
                std::process::exit(2)
            }
            match profile_parser::parse_link(link.trim()) {
                Ok(profile) => {
                    let plan = runtime::RuntimePlan::for_profile(profile);
                    println!(
                        "protocol={:?} server={} port={} native_ready={}",
                        plan.profile.protocol,
                        plan.profile.server,
                        plan.profile.port,
                        plan.native_ready
                    );
                }
                Err(error) => {
                    eprintln!("{error}");
                    std::process::exit(2);
                }
            }
        }
        _ => {
            eprintln!("ior-core: --version | inspect-stdin | serve-stdin <local-port>");
            std::process::exit(2);
        }
    }
}
