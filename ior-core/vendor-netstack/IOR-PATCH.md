Pinned netstack-smoltcp 0.2.4 from the Cargo registry. Licenses: MIT OR Apache-2.0.
Local change: explicitly select CongestionControl::None for listener TCP sockets, preserving the prior algorithm when ior-core enables the smoltcp CUBIC feature. Default Nagle and ACK delay remain unchanged.

The normalized manifest omits example targets and example-only dev dependencies for offline library regression tests. Original manifest and example sources are retained. The crate is a workspace member; default-members remains ior-core.

Optional numeric TCP observer: per-socket counters for transferred bytes,
zero-window time and application receive-buffer backpressure. Snapshots
are aggregated every five seconds during activity and on socket closure.
The observer is disabled unless installed by the host. It does not inspect
payloads or change TCP settings.

An explicit TcpAbortHandle lets ior-core reject a routed AWG connection whose
destination family is unsupported, after name inspection. The runner emits
the smoltcp RST before recycling the socket. Ordinary drop and half-close
retain their existing graceful behavior. A virtual TCP handshake regression
checks that an abort after dropping the stream sends RST rather than only FIN.
