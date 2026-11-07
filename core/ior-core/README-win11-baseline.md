# Windows 11 native baseline

`ior-core-win11.exe` is the previously tested Windows 11 binary from the local
1.2.6 package (the same native binary shipped in public v1.2.5). Its SHA-256 is
recorded in `ior-core-win11.sha256` and checked by Build-WindowsDistribution.ps1.

The Windows 11 package retains this binary because the later candidate's local
performance comparison did not establish absence of regression. Windows 10 uses
the rebuilt source binary with the opt-in DIRECT IPv4 fallback. The new optional
network flag is omitted for Windows 11 to preserve contract compatibility.

This baseline is a necessary packaging dependency, not a complete application
distribution. It is not an automatic download or an additional selectable engine.
