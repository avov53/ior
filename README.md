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
HTTP/SOCKS TCP/UDP ingress and Windows 11 Wintun router. They are available
for user tests, not marked production-verified. WireGuard Proxy uses user-space
IP sockets through its encrypted tunnel, not a hidden Xray/sing-box engine.

Start validates the native profile before network changes, checks HTTPS and
UDP before declaring connected, and uses the existing owned rollback journal.
Ordered PROXY/DIRECT/REJECT and supported rule-provider/geodata expansions are
shared with Reality. Unsupported groups, matchers, plugins and WS/gRPC
variants fail explicitly; unknown imported parameters are retained as blockers.
TLS certificate checking stays enabled. WireGuard currently carry IPv4 and reject
UDP payloads exceeding the configured inner MTU; IPv6-only profiles are rejected.
Windows 10 TUN remains closed pending separate system tests.

Exact dependency versions, checksums and licences are in ior-core/Cargo.lock
and ior-core/licenses. Test with `cargo test --locked`,
`dotnet test HappBox.Tests/HappBox.Tests.csproj`,
and `npm run build` in design. Local encrypted reference tests do not constitute
a completed live Windows TUN/rollback or Windows 10 qualification matrix.

AmneziaWG is no longer supported. Its links and configurations are rejected,
including subscription entries disguised as WireGuard. Standard WireGuard remains available.
