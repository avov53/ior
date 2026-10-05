# AWG: implementation and verification, 2026-10-04

## Implemented

- Tunnel DNS TTL cache and bounded singleflight; transaction IDs remain independent.
- Proxy-only AAAA suppression for IPv4-only tunnel profiles; DIRECT is not globally restricted.
- IPv6 inner TCP/UDP and AAAA resolution for dual-stack WG/AWG profiles with a configured local IPv6 address and IPv6 AllowedIPs.
- Domain TCP IPv6/IPv4 connection race with a 250 ms IPv4 delay and cancellation of the losing attempt.
- Bounded IPv4/IPv6 fragment reassembly, outbound UDP fragmentation, explicit rate-limited ICMP rejection of failed TUN UDP flows.
- Explicit official sing-box + official AWG adapter composition through an authenticated loopback SOCKS5 TCP/UDP bridge. No Xray fallback.
- Bridge credentials use private IPC; sing-box's required transient config file has current-user-only ACL and is deleted on bridge shutdown. Credentials are excluded from persisted AppState.

## Verification

- Final regular checks: Rust 68 tests passed; .NET 216 tests passed; official AWG Go adapter tests and frontend build passed. Opt-in live tests return without running unless their environment flags are enabled; the regular total is not a live-test count.
- Separate Rust run with an Xray reference executable passed VMess/Shadowsocks/Trojan interoperability tests.
- Live user AWG profile: bridge HTTPS and UDP DNS passed. The composed official sing-box HTTPS and UDP DNS probe passed on repeat; an earlier attempt failed. This is not a sustained stability result.
- Live native AWG TUN: first traffic/routing cycle completed; repeated cycle timed out on a DIRECT HTTPS control request. Full qualification failed. Cleanup left no test processes or remaining route journal. Crash rollback qualification was not reached.
- Windows 11 and Windows 10 packages built. Windows 10 live tests were not performed.

## Still incomplete

- Public dual-stack/IPv6-only AWG server and Windows IPv6 routes/DNS/rollback qualification. A local official AWG server interoperability test passed with IPv6 outer endpoint, dual-stack and IPv6-only inner TCP/UDP, A/AAAA DNS and reconnect. This is not Windows system IPv6 qualification.
- Real path-MTU changes and retransmission testing; handling arbitrary IPv6 extension headers is deliberately not claimed.
- Fix and qualify repeat TUN DIRECT control-request timeout; sing-box AWG TUN remains closed with a specific validation-pending reason.
- Browser pre/post VPN, HTTPS/SVCB IPv6 hints, real image/video/Updating timings, sleep/network-change scenarios. Ten alternating old/new AWG and official sing-box Hysteria2 Proxy runs were performed, but these measured public HTML and readiness, not browser rendering or TUN startup. The report is `verification-publish/awg-latency-20261004.json`; it predates the final TCP-drain and randomized-session fixes. Old AWG had three failures and Hysteria2 had four startup failures. Improvements were mixed and do not meet the overall 15% median / 5% p95 acceptance criterion.

No universal speed improvement or complete IPv6/TUN qualification is claimed for this build. Individual measured improvements and failed acceptance criteria are recorded below. Existing server parameters were not changed.

## Additional implementation in this iteration

- Versioned private IPC API 3 adds an ordered DNS-server list and physical IPv4/IPv6 addresses/indices; old versions 1/2 remain accepted. DNS/profile address-family and AllowedIPs compatibility is validated before changing the network.
- DNS racing uses a second resolver after 500 ms, immediate fallback after an error, validated answers and cancellation of losers. Per-name singleflight and TTL caching remain bounded. TCP DNS SERVFAIL/REFUSED does not hide a usable second resolver.
- Multiple resolved addresses are staggered at 250 ms; ordered routing is checked for each candidate. Bootstrap supports A/AAAA and outer IPv6 endpoints.
- Tunnel TX capacity is reserved before packet construction; RX overflow is recorded. TCP is scheduled by socket/network events, with a bounded per-flow app writer. Buffered receive data is drained rather than stranded waiting for a new packet. Each tunnel session randomizes TCP ports and sequence seeds to avoid reconnect TIME_WAIT collisions.
- Effective Windows TUN MTU is the smaller of app/profile MTU (1280 for the current user profile); application and verification are batched with DNS policy setup. Network command durations and startup stages are recorded without raw commands or secrets; repeated unavailable-family errors retain their first destination and aggregate counts.
- Bounded path-MTU cache accepts only lower MTU and checksum-valid ICMP quotes belonging to a live owned flow. Tests cover checksum corruption, destination ownership, truncated quotes and cache behavior. IPv6 extension parsing supports bounded hop-by-hop/destination options, routing with zero segments-left and fragment reassembly; unsupported/invalid headers are rejected.
- Official adapter API 2 binds both physical socket families and emits only handshake timing and byte counters from its private UAPI. Adapter SHA-256: `7418c25915f59050d97f669d6cfbc81ec2cd220ba02ac08e0ff0f84f43f88bd7` (pinned Go 1.27.1). Keys and obfuscation are unchanged.
- Separate TCP/UDP readiness probes still require real traffic. Repeated NRPT inspection was removed only where the immediately preceding network transaction already proved owned and effective policy.

The one-second reconnect target is not verified. No unverified capability gate was removed. Windows 10 packages are build artifacts, not evidence of a successful Windows 10 network test.

The final Windows TUN retry was canceled during elevation (`Start-Process -Verb RunAs` returned user cancellation), before the validator process started. That attempt made no network changes. An earlier actual TUN run still failed DIRECT HTTPS with a 15-second timeout and successfully removed its network journal; crash qualification was not reached. The validator now names that phase `NATIVE_DIRECT_HTTPS_PROBE_FAILED` rather than an unspecified HTTP timeout.

## Second performance run

`verification-publish/awg-latency-final-20261004.json` records ten alternating runs per engine, binary hashes and sanitized failures. This used Rust SHA-256 `4d1a77a18cfe3d842a552be461b6e24c22580f3227eb09fdbfe5e009fec5eb98`. The later removal of the IPv4-only bootstrap validation in TunDns is unit-tested separately and is not represented by that benchmark binary hash.

New AWG: all ten startup runs and all sixty cold/warm page requests succeeded. Old AWG had one cold YouTube failure. Hysteria2 failed six startup probes, leaving only four usable page cycles, so it is not a complete ten-cycle comparison. The aggregate integration test failed because it requires zero failures across all three engines; the report is intentionally preserved.

| Proxy metric (ms) | Old median / p95 | New median / p95 |
| --- | --- | --- |
| Readiness | 2112.5 / 6251 | 719 / 10966 |
| Telegram cold HTML | 2145 / 2678 | 659 / 1061 |
| Roblox cold HTML | 3126.5 / 7993 | 1561 / 1929 |
| YouTube cold HTML | 3558 / 5599 (9 successes) | 2787 / 4598 |

The readiness median improved, but its worst tail regressed; therefore overall speed acceptance **did not pass**. These are public HTML requests through Proxy, not TUN connection times, browser images, YouTube playback or Telegram Updating. Browser and full Windows IPv6/TUN/sleep/Wi-Fi validation remain required.
