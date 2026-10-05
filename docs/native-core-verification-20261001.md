# Native backend verification, 2026-10-01

## Passed

- The application starts `ior-core.exe` through inherited private pipes. It does not start Xray or sing-box for native connections.
- Hysteria2 through the application's HTTP CONNECT listener: live HTTPS with certificate verification.
- Hysteria2 through SOCKS5 UDP ASSOCIATE: live DNS response, transaction identity and response flag checked.
- Rust unit tests for ordered PROXY/DIRECT/REJECT decisions, domain boundaries, CIDR/no-resolve, unknown rules, bounded IPC, proxy and verified VLESS TLS reference transport.
- Xray's pinned executable accepts generated Proxy configurations for all eight user profile types: VLESS, VMess, Trojan, Shadowsocks, Hysteria2, WireGuard, SOCKS and HTTP. This is config validation, not live traffic certification of all eight types.

## Implemented but not certified

- Wintun plus smoltcp TCP/UDP/ICMP data path, bounded flow queues and shutdown ownership.
- C# native adapter addressing, DNS, IPv4/IPv6 capture routes and a pre-mutation rollback journal. The parent-death watchdog owns only the child process registered for this run.
- Native Reality client using a pinned TLS/Reality library and Vision framing. A valid camouflage certificate is deliberately rejected unless authenticated by Reality.

## Blocking evidence

- Two live TUN test attempts did not run: Windows elevation was cancelled. No TUN traffic or rollback pass is recorded. The application therefore does not advertise native TUN as verified.
- The current native Reality handshake against the available server returns `REALITY_AUTHENTICATION_REJECTED`. A successfully completed ordinary TLS handshake was NOT treated as Reality success. Reality remains closed in the application.
- Native routing currently supports ordered MATCH, DOMAIN, DOMAIN-SUFFIX, DOMAIN-KEYWORD and IP-CIDR with PROXY/DIRECT/REJECT. GEOIP, GEOSITE, RULE-SET, groups and logical constructs are rejected explicitly rather than flattened or ignored. TUN-domain routing also needs a verified DNS/sniffing association before domain rules can be opened there.
- Native VLESS UDP is not yet implemented. Hysteria2 is the verified native UDP transport.

## Reproducible checks

`scripts/Test-NativeTun.ps1` must run elevated. It uses the local private application state, journals network changes before mutation, disposes the child, restores the lease in `finally`, and verifies physical forwarding after cleanup. It must not run alongside another VPN.

Live tests opt in through environment variables. No live profile, password, public subscription URL or UUID belongs in this repository or this report. Generated test reports expose only fixed diagnostic codes.

This implementation is not full completion of the requested Reality/TUN/complex-routing release. The experimental setting currently opens verified Hysteria2 Proxy and the existing verified VLESS+TLS TCP prototype, not unfinished combinations.
