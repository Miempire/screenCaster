# Android JNI session layer

`android_jni.rs` is the bridge between `org.screencast.app.NativeCore` and the Rust C ABI in
`native/include/screencast_core.h`. It is the only piece of the Android client that has to live
inside the Rust crate, and `native/` is frozen for this round, so it is kept here as a
**drop-in module** instead of being edited into the crate.

## Why it is needed

The frozen crate exports three JNI symbols (`version`, `getRole`, `setRole`) and fourteen C
symbols (`sc_session_*`). Java cannot call a C symbol directly: a `native` method is resolved by
name as `Java_<package>_<class>_<method>`, so the session API needs one thin JNI function per C
function. Without it `System.loadLibrary` still succeeds and the app starts, but the first
session call throws `UnsatisfiedLinkError`; `NativeCore.isSessionBridgePresent()` detects that
and the UI shows `reason_bridge_missing` instead of crashing.

## Install (two edits, both outside `apps/android/`)

```sh
cp apps/android/native-jni/android_jni.rs native/src/android_jni.rs
```

In `native/src/lib.rs`, next to the existing `mod android` block:

```rust
#[cfg(target_os = "android")]
pub mod android_jni;
```

Nothing else changes. The module never redefines `version`/`getRole`/`setRole`, so it coexists
with the existing block and no `Java_org_screencast_app_NativeCore_*` symbol is duplicated.

## Symbol map

| Java (`org.screencast.app.NativeCore`) | JNI symbol | C ABI |
|---|---|---|
| `version()` | `Java_org_screencast_app_NativeCore_version` | `sc_core_version` (already in the crate) |
| `getRole()` | `..._getRole` | `sc_get_role` (already in the crate) |
| `setRole(int)` | `..._setRole` | `sc_set_role` (already in the crate) |
| `nativeCreate(byte[],byte[],int,boolean)` | `..._nativeCreate` | `sc_session_create` |
| `nativeBind(long,int,int)` | `..._nativeBind` | `sc_session_bind` |
| `nativeSetPeer(long,String,int,String,int)` | `..._nativeSetPeer` | `sc_session_set_peer` |
| `nativeStart(long)` | `..._nativeStart` | `sc_session_start` |
| `nativeSendControl(long,int,byte[])` | `..._nativeSendControl` | `sc_session_send_control` |
| `nativeSendFrame(long,byte[],long,long,int,int,int)` | `..._nativeSendFrame` | `sc_session_send_frame` |
| `nativePoll(long,long[])` | `..._nativePoll` | `sc_session_poll` + `sc_frame_release` |
| `nativeStats(long,long[])` | `..._nativeStats` | `sc_session_stats` |
| `nativeLocalPorts(long,int[])` | `..._nativeLocalPorts` | `sc_session_local_ports` |
| `nativeDestroy(long)` | `..._nativeDestroy` | `sc_session_destroy` |

All fourteen C functions are covered: `sc_frame_release` is called inside `nativePoll` right
after the payload is copied, so Java never holds a pointer the session owns.

## Data layout contracts

`nativePoll(long handle, long[] out)` fills a caller-owned `long[12]` and returns the payload
`byte[]` (or `null`). A `long[]` is used instead of an event object because polling happens at
frame rate and boxing the `u64` fields would put the GC in the video path.

| slot | field | slot | field |
|---|---|---|---|
| 0 | result: 1 event, 0 none, negative error | 6 | `codec` |
| 1 | `kind` | 7 | `frame_id` |
| 2 | `status` | 8 | `capture_timestamp_us` |
| 3 | `component` | 9 | `rtt_us` |
| 4 | `event_type` | 10 | `config_id` |
| 5 | `flags` | 11 | `data_len` |

`nativeStats(long handle, long[] out)` fills a `long[22]`: slots 0..14 are the counters in
declaration order, 15/16 are the two `double` RTT fields as raw bits
(`Double.longBitsToDouble`), 17/18 the path status, 19/20 the local ports, 21 reserved.

## Verification performed on the frozen crate

`android_jni.rs` was type-checked, clippy-checked and rustfmt-checked on the host by compiling it
as a module of a scratch crate that re-exports `screencast-core` (the `jni` dependency is cached
locally, so no network was needed):

```text
cargo check   --offline : Finished, 0 warnings
cargo clippy  --offline -- -D warnings : Finished, 0 warnings
rustfmt --edition 2021 --check : clean
```

The host target is not the Android target, so `cargo build --target aarch64-linux-android` is
still CI's job; the code has no `cfg` branches of its own and only depends on the `jni` crate the
crate already pulls in for `target_os = "android"`.
