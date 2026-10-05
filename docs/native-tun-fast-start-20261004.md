# Experimental AWG / Hysteria2 TUN fast start

AWG and Hysteria2 use fast startup when the experimental flag is enabled. Profile checks, physical server egress, DIRECT checks, the owned network journal and rollback remain mandatory. A verified certificate, successful HTTPS response with nonempty content and native evidence of remote bytes on the PROXY route are required before readiness.

TCP DNS and UDP DNS probes start alongside HTTPS, each with a two-second deadline. Their status is independent (`ready`, `unverified`, `error`), with a safe code and elapsed time. A failed optional probe does not stop a successful HTTPS tunnel. User cancellation, HTTPS failure and invalid physical egress still stop startup and trigger the existing rollback. Xray, Reality and strict qualification retain their existing readiness policy. Optional results are runtime-only and reset on disconnect.

The native request bridge acknowledges a connection operation immediately, then delivers its terminal result through state snapshots. Its 15-second response timer applies to the acknowledgment, not the entire TUN startup.

## Verification on Windows 11

229 .NET tests and the UI build passed. Five readiness tests cover failed UDP with successful DNS/HTTPS, bounded optional timeouts, failed HTTPS, delayed HTTPS and user cancellation. Existing physical-interface and lease tests cover the guard and rollback mechanisms.

A live run (ID `6e9f3398823643bfb51c499c269f4abb`) passed mandatory HTTPS via the native PROXY route and verified network restoration after normal shutdown and process crash for both protocols. It used a controlled qualification route, not browser performance workloads. Results exclude UAC waiting:

| Protocol | Normal start | Second start / crash check | HTTPS phase | DNS / UDP |
|---|---:|---:|---:|---|
| AmneziaWG | 6953 ms | 5964 ms | 4858 / 4558 ms | unverified, two-second timeout |
| Hysteria2 | 5909 ms | 6255 ms | 4310 / 4294 ms | unverified, two-second timeout |

No owned core process remained after the run. Route/DNS/proxy snapshots and owned adapter/journal checks passed normal and crash restoration. These four starts establish functional fast startup, not a ten-run performance comparison or sub-second startup. DNS/UDP readiness remains unconfirmed by these probes.

Run the same check with `scripts/Test-NativeFastStart.ps1 -ProjectRoot <repository>` after a Debug build. It loads saved profiles without persisting modifications, requires UAC, rejects an already active VPN, and writes secret-free readiness results beside the private validation progress report. Strict qualification is still available separately.
