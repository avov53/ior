# Native Reality TUN validation

The native router handles TCP/UDP from Wintun, preserves ordered PROXY/DIRECT/REJECT rules, uses bounded DNS-name metadata and TCP name sniffing, and detects recapture of its own external socket tuples. Windows network changes use the existing owned lease/watchdog, including a temporary owned DNS policy.

Experimental Reality TUN on Windows 11 is enabled directly by the experimental core testing setting. No qualification report or separate settings validation button is required. The ordinary connection path still validates the profile and routes, checks physical egress and actual HTTPS/DNS/UDP traffic, and rolls back owned network changes on failure.

Windows 10 and Hysteria2 TUN require separate integration work and remain closed. The diagnostic qualification report is retained for developer tests only and does not control normal connection availability.

The baseline-gate implementation passed 179 .NET tests and the frontend production build on 2026-10-03. The packaged command was also checked without an administrator token: it reports `ADMINISTRATOR_REQUIRED` and exits before network changes. This is a failure-path check, not a successful live TUN test.

To try Reality TUN, enable experimental core testing in Settings, select ior-core, VLESS Reality and TUN on the dashboard, then connect normally. Startup failures are shown with their actual reason; a missing `tun-validation.json` cannot block this path.

In the source workspace the equivalent command is:

```powershell
& 'C:\Users\Роман\Desktop\ior\scripts\Test-NativeTun.ps1' -ProjectRoot 'C:\Users\Роман\Desktop\ior' -Reality -Discord -Routing
```

The script requests Windows elevation itself and runs the packaged validator. Results and progress are written under `%APPDATA%\ior\diagnostics\native-tun-validation\<run ID>`. It uses the current private application profile without copying its credentials into the repository. The normal and crash checks are always performed; the legacy switches remain accepted.

Separate manual checks are still required for full Discord loading, YouTube video playback, sleep/resume and Wi-Fi changes with network restoration. Fetching YouTube HTML is not a video playback test. These checks are deliberately not marked passed by the automated harness.
