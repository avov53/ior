# ior AWG Noise engine

Isolated fork of BoringTun 0.7.1 (upstream Cloudflare commit
051c9d47dc9c5cb36e461b7d36dcd673820dc98b, BSD-3-Clause). The unmodified
registry dependency continues to implement ordinary WireGuard.

Wire-format reference: amnezia-vpn/amneziawg-go/v3 v3.1.20260828, MIT.
Its license is distributed as LICENSE-amneziawg-3.1.20260828.

Changes in this fork:
- custom initiation/response types are set before MAC1/MAC2 computation;
- incoming custom types are classified without rewriting authenticated bytes;
- configurable cookie type and cookie policy retain MAC verification;
- configurable authenticated data padding/type inside the existing AEAD path;
- configurable session/rekey/keepalive limits and handshake-attempt bound.

The ior-core awg_rust module implements AWG padding, ChaCha20 header protection,
bounded junk/I1-I5 generation, parameter validation and the UDP driver. Crypto
primitives remain the upstream ring/ChaCha20 implementations. Key material and
packet contents are not logged. This is experimental interoperability code,
not a claim of an independent security audit or measured YouTube improvement.
