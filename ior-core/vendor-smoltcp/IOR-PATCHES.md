# Local smoltcp 0.12.0 patch

Source: crates.io smoltcp 0.12.0, unchanged license in LICENSE-0BSD.txt.
The registry cache is not modified. The workspace patch ensures the local
TUN netstack and the outbound stack use the same pinned implementation.

`Socket::last_scaled_window` clamps an exhausted advertised receive window
to zero using TCP sequence-number ordering, including wraparound. Subtracting
the next ACK from an already exhausted right edge previously panicked with
`attempt to subtract sequence numbers with underflow` under a slow reader.
The regression exercises window scaling and sequence-number wraparound.

No changes to Nagle, ACK delay, congestion control, MTU or cipher parameters.
`Socket::diagnostics` provides a read-only numeric snapshot of the RTT
estimate, advertised windows and queued bytes. It does not drive polling
or modify the TCP state. The initial RTT estimate is not a measured ping.

The lab build has 32 bounded ranges; the production build retains four.
TCP sockets explicitly retain
the original limit of four unless the host opts a closed socket into a higher
limit. `Assembler::set_segment_limit` rejects zero, values above the compiled
capacity, and changes with queued ranges. This prevents Cargo feature
unification from changing local TUN or other protocols. Each socket adds a
fixed 448 extra bytes of range storage in the lab on 64-bit targets,
plus one limit field;
no unbounded allocation is introduced.
`reassembly_dropped_packets` counts payloads rejected because that range
limit is exhausted. Tests cover preserved state on overflow, advancing the
next expected segment while full, and refusing live-socket reconfiguration.
The vendored crate is excluded from the parent workspace; its pinned test
dependencies use its own Cargo.lock. Library tests use only the production
IP/TCP/UDP features, with `--no-default-features`.
