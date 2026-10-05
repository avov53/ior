# Protected testing implementation — 2026-09-30

This is an incomplete implementation of the agreed plan, not a certified release.

## Implemented in this change

- Xray launch run IDs; allowlisted, bounded local JSONL events for bootstrap, physical interface, rule compilation, configuration validation, startup, probes and rollback.
- Manual ZIP export from the Journal page. Includes binary SHA-256, events, and read-only IPv4 route/DNS snapshots before TUN, after TUN, and after rollback. Addresses/interfaces are pseudonymized consistently within an export. Raw configs, state, logs, URLs and credentials are excluded.
- Known profile secrets and full URLs are redacted in UI logs.
- An authenticated, loopback SOCKS5 TCP/UDP DIRECT helper. Production sockets and bootstrap DNS are IPv4-only, bound to the physical address and Windows IP_UNICAST_IF. No unbound fallback. TCP sessions capped at 256; UDP destinations capped at 64 per authenticated association; control disconnect closes the association.
- Explicit Advanced-settings opt-in for experimental Xray TUN DIRECT. Basic Happ/ior/Clash rules supported by the existing compiler retain order and targets. Physical HTTPS/TCP and DNS/UDP probes run before and after TUN. A failed probe stops the core and rolls back owned host routes. This is an experimental test path, not verified live support.
- Separate watchdog receives ownership metadata through inherited stdin. Parent identity and child PID/start time are checked. On parent death it kills only its owned core and recovers only a matching run's host-route journal. Existing Xray-owned adapter/routing cleanup still relies on core termination. This is NOT a complete DNS/system-proxy watchdog.
- RoutePlanV2 storage for exact ordered Clash rules, stable rule IDs, parameters, complete group and rule-provider definitions, and nested logical conditions. Unknown parameters fail compilation. Complex groups/providers/logical matchers are preserved but NOT yet executed.
- Rust inherited bidirectional control channel (start/status/stop), bounded frames, versioned readiness/capability events and secret-free error output. C# client tested against the actual Rust executable. Stopping/closing the channel closes the TCP ingress and accepted flows.

## Verification

- Frontend `npm run build`: passed.
- .NET suite: 127 tests passed, including opt-in real native IPC, watchdog parent-crash test, read-only Windows route/DNS snapshot and installed Xray config validation.
- Rust suite: 22 tests passed, including certificate-verified TLS/VLESS reference relay and refusal to downgrade TLS. Release binary built.
- Windows 10/11 packages published separately under `verification-publish/20260930-protected`; existing `dist` unchanged. Windows 10 TUN support remains gated.
- Live DIRECT/TUN test requested UAC; elevation was cancelled. No network changes or live DIRECT certification occurred in that attempt.

## Still required for the full plan

- Native ior-core Reality/Vision, Hysteria2, protocol UDP, live ordered routing and Windows Wintun/userspace stack. UI connections for these combinations remain unavailable; no other engine is substituted.
- Provider fetching, validated atomic/offline cache and execution, exact selector/group behavior, logical/no-resolve/geodata semantics and differential Happ/Mihomo tests. A pinned extended Xray route handler has not been built.
- Full capability-status matrix and user confirmation for all running-route mutations.
- Complete crash journal for owned DNS/system-proxy changes; network-switch/sleep/crash integration tests; close the small process-start/ownership-ack crash window.
- Live Xray DIRECT TCP/UDP verification and restoration on Windows 11; Windows 10 separately.

## Live test (requires renewed approval/UAC)

Build the .NET tests first, then run `scripts/Test-XrayTunHelper.ps1` elevated with `-ProjectRoot` pointing to this repository. The test reads the local existing profile without exporting it, uses a synthetic Happ DIRECT route, checks actual TCP/UDP passing through the helper, and cleans its temporary generated config and owned host routes in `finally`. Never run while another Xray connection is active. Do not copy state/configs or subscription credentials into reports.
