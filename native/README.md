# ScreenCast Rust network core

The reusable network core for the Android and macOS clients: an independent implementation
of the SCT1 wire protocol that is byte-for-byte compatible with the C# reference in
`src/ScreenCast.Core/`. It ships `cdylib`, `staticlib` and `rlib` outputs and exposes a C ABI
(`include/screencast_core.h`, Swift module map `include/module.modulemap`, Android JNI names
matching `org.screencast.app.NativeCore`).

The crate has **no third-party dependencies** on the host targets. AES-256, GCM, SHA-256,
HMAC and HKDF are implemented in `src/crypto/` and pinned against published known-answer
vectors, so the same source builds for every platform without a C toolchain, a build script
or a network fetch.

## What is implemented

| Module | Contents |
|---|---|
| `packet` | 64-byte application header, HKDF-SHA256 per-direction keys, AES-256-GCM, 1024-packet replay window committed only after authentication |
| `control` | 32-byte control header, reliable ordered channel: cumulative + selective ACK, duplicate suppression with repeated ACK, Karn-filtered RTT, RTO clamp, fast retransmit, 8 attempts / 2 s expiry |
| `video` | 32-byte fragment prefix, stateless splitter, bounded reassembler (4 frames, 8 MiB, 8192 fragments, expiry) that never delivers a partial frame |
| `transport` | real `std::net::UdpSocket`, 4 MiB `SO_RCVBUF`/`SO_SNDBUF`, read-timeout based stop, address-family checks |
| `session` | two-socket media session, four direction-scoped key domains, video fragmentation + encryption, bounded control send queue, two receive loops, ticker, bounded event queue |
| `probe` | 32-byte encrypted probes, token-matched RTT, keepalive where **sending never refreshes liveness**, N misses marks the path down |
| `stun` | RFC 5389 Binding request/response, XOR-MAPPED-ADDRESS for IPv4/IPv6, FINGERPRINT CRC, 420 for unknown required attributes, retrying client |
| `ffi` | the C ABI used by JNI and Swift, including event polling and statistics |

## Wire compatibility with the C# implementation

| Field / algorithm | C# source | Verified by |
|---|---|---|
| 64-byte header, magic/version/channel/flags/session/epoch/stream/sequence/length/header-length/reserved | `Packets.cs` | `native/tests/vectors/header-v1.hex` fixture |
| HKDF info = label ‖ 01 ‖ sessionId ‖ epochBE4 ‖ direction ‖ channel ‖ streamBE4, salt = sessionId | `Packets.cs` | `docs/packet-vector.md` full-datagram vector |
| AES-256-GCM, nonce = epochBE4 ‖ sequenceBE8, AAD = first 48 header bytes, tag at offset 48 | `Packets.cs` | same vector; `crypto/gcm.rs` tests |
| 1024-packet replay window, commit after authentication | `Packets.cs` | `packet.rs` tests |
| 32-byte control header, `u64::MAX` ackBase sentinel, mask bit i = ackBase+1+i | `ControlCodec.cs` | `control.rs` codec tests |
| window 64/64, cumulative + selective ACK, duplicate Data re-ACKed | `ReliableControlChannel.cs` | `control.rs` channel tests |
| RTO = clamp(SRTT + 4·RTTVAR, 30 ms, 1 s), Karn's rule, backoff capped at four doublings | `ReliableControlChannel.cs` | `control.rs` tests |
| 8 attempts or 2 s → expire and report the event type | `ReliableControlChannel.cs` | `control.rs` tests |
| 32-byte video fragment prefix and limits | `VideoFraming.cs` | `video.rs` tests |
| 32-byte probe payload, echo of the requester's clock | `PathProbe.cs` | `probe.rs` tests |
| STUN header, cookie, XOR address, FINGERPRINT | `StunBindingCodec.cs` | `stun.rs` tests |

The full-datagram vector in `docs/packet-vector.md` is reproduced byte for byte in
`packet::tests::known_answer_vector_matches_packet_vector_md`:

```text
5343543101010000000102030405060708090A0B0C0D0E0F0000000100000000000000000000000000030040000000000A92411A02B67657D95A187A5A25A623C8226B
```

## C ABI

```c
sc_session_t *sc_session_create(const uint8_t *secret32, const uint8_t *session_id16,
                                uint32_t epoch, bool is_host);
int32_t sc_session_bind(sc_session_t *, uint16_t video_port, uint16_t control_port);
int32_t sc_session_set_peer(sc_session_t *, const char *video_addr, uint16_t video_port,
                            const char *control_addr, uint16_t control_port);
int32_t sc_session_start(sc_session_t *);
int32_t sc_session_send_frame(sc_session_t *, const uint8_t *data, uint32_t len,
                              uint64_t frame_id, uint64_t ts_us, uint32_t config_id,
                              uint8_t flags, uint8_t codec);
int32_t sc_session_send_control(sc_session_t *, uint16_t event_type, const uint8_t *body,
                                uint16_t len);
int32_t sc_session_poll(sc_session_t *, sc_event_t *out);
void sc_session_stats(sc_session_t *, sc_stats_t *out);
int32_t sc_session_local_ports(sc_session_t *, uint16_t *video_port, uint16_t *control_port);
void sc_frame_release(sc_session_t *);
void sc_session_destroy(sc_session_t *);
```

Guarantees:

* No panic, exception or Rust `String`/`Vec`/trait object crosses the boundary. Every entry
  point validates its pointers and returns a negative `SC_ERR_*` code. (`catch_unwind` is a
  development-profile net only: the release profile is built with `panic = "abort"`.)
* `sc_session_bind` returns the bound video port (positive) or a negative status; read both
  ports with `sc_session_local_ports`.
* `sc_session_poll` returns 1 with `out` filled, 0 when idle, negative on error. The `data`
  pointer stays valid until the next poll that returns a frame/body, or `sc_frame_release`.
* Events: video frame (frame id, timestamp, config id, flags, codec, bytes), control event,
  control event expired (the platform must release held keys), and path status changes.

The original 0.1.0 functions (`sc_core_version`, `sc_get_role`, `sc_set_role`) are unchanged;
`sc_core_version()` now reports `0.2.0` = 512.

## Build and test

```sh
cargo fmt --manifest-path native/Cargo.toml -- --check
cargo clippy --manifest-path native/Cargo.toml --locked --all-targets -- -D warnings
cargo test --manifest-path native/Cargo.toml --locked
```

On the local Windows/MinGW machine the isolated toolchain needs the MCF runtime appended at
link time (`RUSTFLAGS='-C link-arg=-lmcfgthread'`); CI runs on Linux where that is not needed.

Optional external checks (not run by `cargo test`):

```sh
# The C header compiles as C11 and C++17 and its layout asserts hold.
gcc -std=c11  -Wall -Wextra -Werror -c -I native/include native/tools/header_smoke.c -o header_smoke.o
g++ -std=c++17 -Wall -Wextra -Werror -x c++ -c -I native/include native/tools/header_smoke.c -o header_smoke.o

# The built shared library exports the ABI and drives a full session out of process.
cargo build --release
python native/tools/abi_smoke.py native/target/release/screencast_core.dll
```

## Test coverage

`cargo test` runs 102 tests:

* 89 unit tests - crypto known-answer vectors (AES-256 FIPS-197, GCM NIST, SHA-256, HMAC,
  HKDF RFC 5869), header parsing, the packet vector, replay/window boundaries, control
  codec and channel behaviour, video framing, probes, STUN and the UDP transport.
* 6 C ABI tests - struct layout, argument validation, null handling and a full encrypted
  round trip through `sc_session_*`.
* 7 real-UDP integration tests - two sessions exchanging a 4000-byte and a 20000-byte frame
  and a control event over loopback, forged tag dropped while the session survives, replay
  rejected without a second frame, missing fragment never delivered, keepalive probes
  reported alive, ordered control delivery.

## Not implemented here

* The SCSH signalling handshake, key agreement, cookies and Finished confirmation.
* TURN allocation/relay (STUN Binding discovery is implemented; TURN is not).
* Hole punching, candidate selection and path switching (`LinkController` equivalents); the
  session takes explicit peer endpoints.
* Jitter buffer, adaptive latency, FEC, codecs, capture, decoding and input injection.
* Multi-session dispatch on one socket: one session owns one socket pair.
