# ior

Windows GUI client prototype for `sing-box`.

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
