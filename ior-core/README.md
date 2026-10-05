# ior-core

Rust workspace for the native ior network engine. The executable accepts `inspect-stdin` (profile link on stdin) and an experimental `serve-stdin <local-port>` command (versioned start JSON on stdin). The latter binds a loopback-only HTTP CONNECT/SOCKS5 TCP ingress with a native, certificate-verified VLESS/TLS connector. It accepts only `MATCH → PROXY` and ordinary VLESS/TLS; the Windows app does not yet launch this preview. No credentials are passed as process arguments. It must not be used as a replacement for the working backend until real server and recovery tests pass.

Modules in `crates/ior-core/src`:

- `core_api`: shared server, DNS, route, and proxy group model.
- `profile_parser`: VLESS, VMess, Trojan, Shadowsocks, Hysteria2 and WireGuard link normalization. VMess currently reads common flat fields only.
- `route_engine`: legacy rule precedence (`block → proxy → direct`) plus a separate ordered-rule evaluator. The ordered model preserves proxy-group targets and can represent GEOIP, GEOSITE and RULE-SET, but it requires an external dataset provider. Missing datasets cause an error instead of silently falling through to DIRECT. It is not yet wired to live network traffic.
- `dns_engine`: bootstrap resolver plan plus direct UDP IPv4 lookup for the server before Proxy connects; TUN still needs physical-interface binding.
- `runtime`: runtime plan, group selection strategies, and a gaming-mode quality scorer based on median RTT, jitter and loss. This is an offline planning primitive only: there are no live probes or traffic switching yet.
- `protocols`: transport capability matrix. Native transports are deliberately marked unavailable.
- `proxy_inbound`: loopback-only HTTP CONNECT/SOCKS5 TCP ingress with bounded concurrency and a mandatory outbound connector; UDP ASSOCIATE deliberately rejects until implemented.
- `ipc_contract`: C# ↔ Rust version 1 JSON payload with fail-closed shape validation; `fixtures/start-v1.json` is shared with .NET tests. No local IPC listener or secret-bearing process arguments exist yet.
- `vless_codec`: VLESS v0 TCP request/response framing with test vectors.
- `vless_tls`: native VLESS/TLS TCP connector using certificate-verified rustls and explicit bootstrap DNS; Reality, Vision and UDP are separate, unsupported transports. A local reference TLS/VLESS server test exercises encrypted TCP relay, but no live VPN server has been tested.
- `tun`: configuration types for future implementation.
- `config_store`: atomic file write helper.

Run `cargo test --offline` and `cargo build --release --offline` from this folder. The release build packages the preview executable at `core/ior-core/ior-core.exe` when Cargo is available. Integration with the Windows app, VLESS Reality/Hysteria2, UDP ingress, direct DNS bootstrap, ordered live routing, a current-user control channel beyond inherited stdin, and Windows TUN still need implementation and compatibility tests before ior-core can replace the backend.
