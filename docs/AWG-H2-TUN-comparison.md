# Native AWG / Hysteria2 TUN experiment

Windows 11 experimental access permits both transports in ior-core. This is not a production or Windows 10 qualification. Sing-box + AWG TUN remains separately gated. Hysteria2 QUIC endpoints now bind to the selected physical address/interface, including reconnects; server routes and rollback remain managed by C#.

Run from the repository after building the app and Rust release:

```powershell
& .\scripts\Test-NativeTunBenchmark.ps1 -ProjectRoot (Get-Location).Path
```

Disconnect any active VPN first. One UAC launch starts an owned elevated coordinator; each cycle owns its processes and a rollback journal. It uses saved AWG and Hysteria2 profiles without persisting edits, DNS 8.8.8.8 for both, and the same ordered REJECT / DIRECT / PROXY qualification route. Unknown selected-profile rules must still pass preflight before network changes.

Ten cycles per protocol alternate their ordering. The report contains binary/adapter hashes, stage timings, readiness, DNS, DIRECT HTTPS, Discord Gateway WebSocket, and public Telegram/Roblox/YouTube HTML first-byte timings with a second request on the same connection pool. These HTTP measurements do not claim browser-rendered content, Telegram Updating, image decoding or YouTube playback. Those require a separate browser scenario. UAC/process-launch waiting is printed separately. Cold means first request in the test client, not a flushed browser or operating-system cache.

Normal-cycle restoration compares physical-interface routes/DNS/proxy snapshots and checks that no owned lease journal remains. A separate normal/crash qualification follows. Failed cycles stay in the report rather than being discarded. An active VPN or failed restoration stops testing.

The schema-2 report alternates baseline/current binaries for both AWG and Hysteria2 (ten cycles per variant/protocol). Baseline uses isolated PowerShell commands and sequential HTTPS readiness; current uses the prepared worker and hedged readiness. Both run through the same qualification contour. The report records hashes, failures and cold/repeated client requests. Acceptance requires ten samples, at least 15% median improvement, no more than 5% p95 regression and no current failures. A one-second target is not an early-readiness shortcut.

`TUN_TCP_IO_FAILED` identifies application/transport side, read/write/flush/shutdown operation, direction and successful bytes; no request contents or credentials are recorded. IPv6-unavailable failures remain separately coded and must not be attributed to startup latency.

## Current verification result (2026-10-04)

71 Rust library tests, 221 .NET tests and the UI build passed before the final benchmark missing-data test was added. Go adapter tests passed. Opt-in live tests inside the Rust suite were not enabled by the ordinary unit-test run. This turn's elevated comparison returned ELEVATION_LAUNCH_FAILED after 122275 ms; zero cycles started and no network changes were made by that attempt. Firefox automation was stopped by the tool because URL-policy enforcement is unsupported for this Windows browser. Actual TUN readiness, rendered pages, crash rollback and acceleration remain unverified; neither a ten-cycle result nor sub-second startup is claimed.

Implemented changes: one prewarmed private-pipe Windows PowerShell worker, sequential bounded operations, lifecycle cleanup and watchdog ownership; independent rollback fallback; pure Rust AWG preflight instead of starting the adapter twice; a second HTTPS readiness attempt after 250 ms alongside UDP/DNS; first-remote-byte proof for the selected PROXY route; stage summaries and first-byte/I/O-wait events. Hysteria2 fast-open, physical QUIC binding, user obfuscation and AWG MTU remain unchanged. I/O wait is not reported as proof of queue delay. Separate HTTPS DNS/TCP/TLS instrumentation, detailed queue metrics and browser comparison still require further work/verification.
