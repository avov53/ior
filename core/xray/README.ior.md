# Bundled Xray backend

`xray.exe` is Xray-core v26.9.9 (upstream pre-release) for Windows x64,
downloaded from the official `XTLS/Xray-core` GitHub release `v26.9.9`
(`Xray-windows-64.zip`). The archive SHA-256 was verified before extraction:

`244DEABA2098C2964E49BBA90DF3707777E5F5F428A82D2F29604015F24BEEC2`

The executable, Wintun library and their upstream licenses are redistributed.
`geoip.dat` and `geosite.dat` are copied from the same verified official archive
for Xray's built-in geographic route rules. Their SHA-256 hashes are
`4149E607530F91DA697BAD4696F8C59F0A475AF38E69405E4124438C9886C721`
and `2064A1A4074E145D5022AC49F2C30341E7B7CB6C7948DA4FDCA973B3FA8411B2`.
The old v26.2.4 executable remains in the repository as `xray-26.2.4.exe`, but
is not included in publish output. ior's adapter supports local HTTP/SOCKS
Proxy mode for selected VLESS, VMess, Trojan, Shadowsocks and un-obfuscated
Hysteria2 profiles. Xray 26.9.9 removed `allowInsecure`; such profiles are
rejected until a certificate SHA-256 pin can be stored. The Hysteria2 Proxy
path passed an isolated loopback TCP and UDP test. TUN configuration has only
passed offline validation; starting TUN remains disabled until Windows 10/11
route, DNS and crash-recovery tests pass.
