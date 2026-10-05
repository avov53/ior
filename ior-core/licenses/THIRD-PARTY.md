# Native networking dependencies

Exact versions and checksums are recorded in Cargo.lock. Git dependencies are pinned by commit, not a moving branch.

- rsteria2 0.1.1 — MIT; LICENSE-rsteria2 included. Crate archive SHA-256 `1abe0be87d7e1a8450a64dba96a009906f5b8a5a5cbe87f6770d3d34d71da9bf`. Source is retained in `vendor-hysteria`; local changes add an optional physical local address and Windows interface index to QUIC endpoint creation, including reconnects. Protocol/authentication/congestion defaults remain upstream. The fork and transitive dependency versions are recorded in the source tree and Cargo.lock.
- reality-rs rustls, rustls-aws-lc-rs and rustls-util — vendored from commit 326f75f8f0340aea1c1fc55244ff7647c6cc6605; MIT option selected; LICENSE-reality-rs included. Local changes delegate hybrid REALITY key extraction to X25519, advertise hybrid browser groups, and authenticate synthetic certificates by their REALITY HMAC rather than WebPKI SAN. Camouflage fallback remains rejected. reality_bridge.rs is derived from its async bridge.
- tun-rs 2.8.11 — Apache-2.0.
- netstack-smoltcp 0.2.4 — MIT OR Apache-2.0; includes smoltcp as its user-space IP stack.
- smoltcp 0.12.0 — 0BSD, LICENSE-smoltcp-0.12.0 included. Local pinned copy in vendor-smoltcp clamps an exhausted scaled receive window to zero instead of subtracting TCP sequence numbers with underflow; adds read-only numeric diagnostics and bounded per-socket out-of-order assembly configuration. Lab capacity is 32, production retains four. TCP sockets explicitly default to four to preserve local TUN behavior during lab feature unification. Details and regressions in vendor-smoltcp/IOR-PATCHES.md; ACK delay and congestion defaults are unchanged.
- socket2 0.6.4 — MIT OR Apache-2.0.
- regex 1.11.2 and its regex-automata, regex-syntax, aho-corasick dependencies — MIT OR Apache-2.0; exact versions and checksums in Cargo.lock.
- futures 0.3.34 — MIT OR Apache-2.0.
- windows-sys 0.61.2 — MIT OR Apache-2.0.
- VMess protocol codec — vendored from shoes 0.2.2, MIT, crate archive SHA-256 `bbaf9328b146d04937153c98b8f02d6839a8f0b92e97eb898ce31637069a63b9`. Only protocol/I/O modules are included, not the shoes application. Local changes replace uninitialized allocations with initialized buffers and reject zero-length writes. LICENSE-vmess-shoes-0.2.2 included.
- shadowsocks 1.25.0 — MIT, LICENSE-shadowsocks-1.25.0 included. AEAD/AEAD2022 TCP/UDP; versions and archive checksums of its cryptographic dependencies are in Cargo.lock. ior bounds first TCP writes and verifies UDP session IDs/replay windows.
- boringtun 0.7.1 — BSD-3-Clause, upstream commit `051c9d47dc9c5cb36e461b7d36dcd673820dc98b`, LICENSE-boringtun-0.7.1 included. User-space WireGuard encryption; ior's smoltcp sockets share its encrypted IP interface.


Cryptography is supplied by rustls, AWS-LC, ring and their pinned dependencies. No custom cryptographic primitives are implemented. The patched Reality library passed HTTPS, Discord Gateway WebSocket and XUDP DNS on the available remote server. This does not imply complete Vision optimization, Windows TUN recovery validation, or production readiness.
