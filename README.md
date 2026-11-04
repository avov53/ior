# ior

Windows desktop VPN client with TUN, local proxy, subscriptions, and multiple routing cores.

## Release 1.2.3

- Adds an in-app update badge. The app checks the latest stable GitHub release at startup, downloads only after the user clicks, and applies the staged Windows package on restart.
- Promotes ior-core VLESS Reality and Hysteria2 in Proxy and Windows 11 TUN modes; other ior-core combinations remain experimental.
- Removes the About page and retains older navigation settings safely.

## Features

- Import `vless://` links with TLS or Reality.
- Import `ss://` Shadowsocks links.
- Import `hysteria2://` / `hy2://` links.
- Import WireGuard from `wireguard://base64(wg-config)` or raw `[Interface]` config.
- Generate a `sing-box` config with TUN and local mixed HTTP/SOCKS inbound.
- Start and stop a selected `sing-box.exe` process from the GUI.

## Requirements

- Windows.
- .NET 8 SDK to build.
- `sing-box.exe` available on disk.

## Build and run

```powershell
dotnet run
```

If TUN mode is enabled, run the app as administrator. Without administrator rights, disable TUN and use the local mixed proxy port.

Generated app state and config are stored in:

```text
%APPDATA%\ior
```

## Experimental native protocols

Enable experimental core testing in Settings and select ior-core. VMess AEAD
(alterId=0, TCP with optional TLS), Shadowsocks AEAD/2022, Trojan TCP+TLS,
WireGuard are connected to the shared
HTTP/SOCKS TCP/UDP ingress and Windows 10/11 Wintun router. They are available
for user tests, not marked production-verified. WireGuard Proxy uses user-space
IP sockets through its encrypted tunnel, not a hidden Xray/sing-box engine.

Start validates the native profile before network changes, checks HTTPS and
UDP before declaring connected, and uses the existing owned rollback journal.
Ordered PROXY/DIRECT/REJECT and supported rule-provider/geodata expansions are
shared with Reality. Unsupported groups, matchers, plugins and WS/gRPC
variants fail explicitly; unknown imported parameters are retained as blockers.
TLS certificate checking stays enabled. WireGuard uses supported local IPv4/IPv6 addresses and AllowedIPs; unsupported destination families are rejected.
ior-core TUN is available on Windows 10 1607+ and Windows 11. Windows 10 live networking and rollback qualification remains pending; Windows 11 proofs do not qualify Windows 10. Standard WireGuard Proxy/TUN remains behind the experimental switch and supports TCP/UDP, DNS, dual-stack and profile MTU/AllowedIPs. Real-server throughput and video playback are not yet confirmed.

Exact dependency versions, checksums and licences are in ior-core/Cargo.lock
and ior-core/licenses. Test with `cargo test --locked`,
`dotnet test HappBox.Tests/HappBox.Tests.csproj`,
and `npm run build` in design. Local encrypted reference tests do not constitute
a completed live Windows TUN/rollback or Windows 10 qualification matrix.

AmneziaWG 3.1 is experimental in ior-core TUN only (Windows 10 1607+/11).
Enable experimental core testing, import the original .conf or a Base64 config
link, and select ior-core + TUN. Native Rust is the default engine; the pinned
official Go engine remains a manual alternative. Neither creates a separate
system AWG adapter. Selected routing rules apply, with unmatched traffic sent
through AWG; an empty routing profile sends all traffic through AWG.
Fresh, nonconflicting DNS observations allow PROXY TCP/UDP to preserve Windows
TCP even with domain DIRECT/REJECT rules. Unknown/conflicting names retain the
name-inspecting stack; paths are fixed for each connection. Parameters, profile
DNS and MTU are preserved. Local tests do not establish browser speed.
See docs/awg-routing-rust-default-20261009.md for comparison instructions and limits.

## Subscription HWID

HWID sending is off by default. Settings → Connection allows a global default,
per-subscription allow/block rules and an optional manual HWID. Required HWID
responses ask for consent; background refreshes show the requirement in the card.
An installation seed is stored separately with current-user access. Derived UUIDs
are stable per HTTPS origin. Redirect destinations require their own approval.
Generated IDs and manual overrides are excluded from shared QR/JSON subscriptions;
restoring a backup clears HWID permissions and overrides.

Native startup reports are stored in `%APPDATA%\ior\diagnostics\native-start` before
preflight begins and included in exported diagnostic ZIPs. They contain version,
Windows build, binary hash, failed stage/codes and HRESULTs, without exception
messages or profile credentials. Windows 10 TUN failures still need reproduction
on the affected device; report generation is not a transport fix.
