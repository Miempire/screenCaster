//! JNI bridge for `org.screencast.app.NativeCore` (Android viewer / control end).
//!
//! This file is the **only** glue between Java and the Rust C ABI in
//! `native/include/screencast_core.h`. It deliberately contains no protocol logic: every
//! entry point validates its Java arguments, calls one `sc_session_*` function and converts
//! the result back into a JVM value.
//!
//! ## Install (one line, applied by whoever owns `native/`)
//!
//! ```text
//! cp apps/android/native-jni/android_jni.rs native/src/android_jni.rs
//! ```
//!
//! and add to `native/src/lib.rs` (next to the existing `mod android` block):
//!
//! ```text
//! #[cfg(target_os = "android")]
//! pub mod android_jni;
//! ```
//!
//! The existing `mod android` block keeps exporting `version`, `getRole` and `setRole`;
//! this module exports the ten session symbols and never redefines those three, so the two
//! modules can coexist without duplicate `Java_org_screencast_app_NativeCore_*` symbols.
//!
//! ## Memory contract
//!
//! `sc_event_t::data` is owned by the session and is only valid until the next poll that
//! returns a payload or `sc_frame_release`. `nativePoll` therefore copies the bytes into a
//! fresh `byte[]` **before** releasing the frame buffer; no raw pointer ever crosses into
//! Java, and Java never has to release anything.
//!
//! ## Why the scalar fields travel in a `long[]`
//!
//! Polling happens at frame rate. Allocating one Java object per event (or boxing `u64`
//! fields) would put the GC directly in the video path. The caller passes a preallocated
//! `long[12]` and gets the payload as the return value, so a poll costs one array header
//! plus the payload copy and nothing else.

use jni::objects::{JByteArray, JClass, JIntArray, JLongArray, JString};
use jni::sys::{jboolean, jbyteArray, jint, jlong, jsize};
use jni::JNIEnv;

use crate::ffi::{
    sc_frame_release, sc_session_bind, sc_session_create, sc_session_destroy,
    sc_session_local_ports, sc_session_poll, sc_session_send_control, sc_session_send_frame,
    sc_session_set_peer, sc_session_start, sc_session_stats, ScEvent, ScSession, ScStats,
    SC_ERR_INVALID_ARGUMENT, SC_ERR_NULL, SC_ERR_TOO_LARGE,
};

/// Number of `long` slots `nativePoll` writes; mirrors `NativeCore.POLL_SLOTS`.
const POLL_SLOTS: usize = 12;

/// Number of `long` slots `nativeStats` writes; mirrors `NativeCore.STATS_SLOTS`.
const STATS_SLOTS: usize = 22;

/// Reinterpret a Java `long` handle as a session pointer.
fn session_from(handle: jlong) -> *mut ScSession {
    handle as usize as *mut ScSession
}

/// Reinterpret a session pointer as a Java `long` handle; null stays 0.
fn handle_from(session: *mut ScSession) -> jlong {
    session as usize as jlong
}

/// Read a fixed-size `byte[]` (secret or session id) into an array, rejecting wrong lengths.
fn read_fixed<const N: usize>(env: &JNIEnv, array: &JByteArray) -> Option<[u8; N]> {
    if array.is_null() {
        return None;
    }

    let bytes = env.convert_byte_array(array).ok()?;
    if bytes.len() != N {
        return None;
    }

    let mut out = [0u8; N];
    out.copy_from_slice(&bytes);
    Some(out)
}

/// Read a `byte[]` of at most `u16::MAX` bytes; a null array is an empty body.
fn read_body(env: &JNIEnv, array: &JByteArray) -> Option<Vec<u8>> {
    if array.is_null() {
        return Some(Vec::new());
    }

    let bytes = env.convert_byte_array(array).ok()?;
    if bytes.len() > usize::from(u16::MAX) {
        return None;
    }

    Some(bytes)
}

/// `static native long nativeCreate(byte[] secret32, byte[] sessionId16, int epoch, boolean isHost)`
#[no_mangle]
pub extern "system" fn Java_org_screencast_app_NativeCore_nativeCreate(
    env: JNIEnv,
    _class: JClass,
    secret: JByteArray,
    session_id: JByteArray,
    epoch: jint,
    is_host: jboolean,
) -> jlong {
    let Some(secret) = read_fixed::<32>(&env, &secret) else {
        return 0;
    };
    let Some(session_id) = read_fixed::<16>(&env, &session_id) else {
        return 0;
    };
    if epoch < 1 {
        return 0;
    }

    // SAFETY: both buffers are 32/16 bytes long and live for the duration of the call; the
    // session copies them into its own configuration.
    let session = unsafe {
        sc_session_create(
            secret.as_ptr(),
            session_id.as_ptr(),
            epoch as u32,
            is_host != 0,
        )
    };
    handle_from(session)
}

/// `static native int nativeBind(long handle, int videoPort, int controlPort)`
#[no_mangle]
pub extern "system" fn Java_org_screencast_app_NativeCore_nativeBind(
    _env: JNIEnv,
    _class: JClass,
    handle: jlong,
    video_port: jint,
    control_port: jint,
) -> jint {
    if handle == 0 {
        return SC_ERR_NULL;
    }
    let (Ok(video_port), Ok(control_port)) =
        (u16::try_from(video_port), u16::try_from(control_port))
    else {
        return SC_ERR_INVALID_ARGUMENT;
    };

    // SAFETY: the handle came from nativeCreate and the caller keeps it alive until destroy.
    unsafe { sc_session_bind(session_from(handle), video_port, control_port) }
}

/// `static native int nativeSetPeer(long handle, String videoAddr, int videoPort, String controlAddr, int controlPort)`
#[no_mangle]
pub extern "system" fn Java_org_screencast_app_NativeCore_nativeSetPeer(
    mut env: JNIEnv,
    _class: JClass,
    handle: jlong,
    video_address: JString,
    video_port: jint,
    control_address: JString,
    control_port: jint,
) -> jint {
    if handle == 0 {
        return SC_ERR_NULL;
    }
    let (Ok(video_port), Ok(control_port)) =
        (u16::try_from(video_port), u16::try_from(control_port))
    else {
        return SC_ERR_INVALID_ARGUMENT;
    };

    let Ok(video_text) = env.get_string(&video_address) else {
        return SC_ERR_INVALID_ARGUMENT;
    };
    let Ok(control_text) = env.get_string(&control_address) else {
        return SC_ERR_INVALID_ARGUMENT;
    };
    let video_text: String = video_text.into();
    let control_text: String = control_text.into();

    // The C ABI takes NUL-terminated strings; an interior NUL is a caller bug, not a session
    // state, so reject it instead of truncating silently.
    let (Ok(video_cstr), Ok(control_cstr)) = (
        std::ffi::CString::new(video_text),
        std::ffi::CString::new(control_text),
    ) else {
        return SC_ERR_INVALID_ARGUMENT;
    };

    // SAFETY: both C strings outlive the call and the session copies the addresses.
    unsafe {
        sc_session_set_peer(
            session_from(handle),
            video_cstr.as_ptr(),
            video_port,
            control_cstr.as_ptr(),
            control_port,
        )
    }
}

/// `static native int nativeStart(long handle)`
#[no_mangle]
pub extern "system" fn Java_org_screencast_app_NativeCore_nativeStart(
    _env: JNIEnv,
    _class: JClass,
    handle: jlong,
) -> jint {
    if handle == 0 {
        return SC_ERR_NULL;
    }

    // SAFETY: the handle came from nativeCreate and has not been destroyed.
    unsafe { sc_session_start(session_from(handle)) }
}

/// `static native int nativeSendControl(long handle, int eventType, byte[] body)`
#[no_mangle]
pub extern "system" fn Java_org_screencast_app_NativeCore_nativeSendControl(
    env: JNIEnv,
    _class: JClass,
    handle: jlong,
    event_type: jint,
    body: JByteArray,
) -> jint {
    if handle == 0 {
        return SC_ERR_NULL;
    }
    let Ok(event_type) = u16::try_from(event_type) else {
        return SC_ERR_INVALID_ARGUMENT;
    };
    let Some(body) = read_body(&env, &body) else {
        return SC_ERR_TOO_LARGE;
    };

    // SAFETY: the handle is live and `body` is a valid slice for the duration of the call.
    unsafe {
        sc_session_send_control(
            session_from(handle),
            event_type,
            body.as_ptr(),
            body.len() as u16,
        )
    }
}

/// `static native int nativeSendFrame(long handle, byte[] data, long frameId, long tsUs, int configId, int flags, int codec)`
///
/// The viewer never sends video, so the Android app does not call this today; it is exported
/// because the C ABI is complete and a future Host build needs the same bridge.
#[no_mangle]
pub extern "system" fn Java_org_screencast_app_NativeCore_nativeSendFrame(
    env: JNIEnv,
    _class: JClass,
    handle: jlong,
    data: JByteArray,
    frame_id: jlong,
    timestamp_us: jlong,
    config_id: jint,
    flags: jint,
    codec: jint,
) -> jint {
    if handle == 0 {
        return SC_ERR_NULL;
    }
    if data.is_null() {
        return SC_ERR_INVALID_ARGUMENT;
    }
    let Ok(bytes) = env.convert_byte_array(&data) else {
        return SC_ERR_INVALID_ARGUMENT;
    };
    if bytes.is_empty() {
        return SC_ERR_INVALID_ARGUMENT;
    }
    let Ok(len) = u32::try_from(bytes.len()) else {
        return SC_ERR_TOO_LARGE;
    };
    let (Ok(flags), Ok(codec)) = (u8::try_from(flags), u8::try_from(codec)) else {
        return SC_ERR_INVALID_ARGUMENT;
    };

    // SAFETY: the handle is live and `bytes` is a valid slice for the duration of the call.
    unsafe {
        sc_session_send_frame(
            session_from(handle),
            bytes.as_ptr(),
            len,
            frame_id as u64,
            timestamp_us as u64,
            config_id as u32,
            flags,
            codec,
        )
    }
}

/// `static native byte[] nativePoll(long handle, long[] out)`
///
/// Returns the payload bytes of one event, or null when no event is pending or on error.
/// `out` is always filled with [`POLL_SLOTS`] values so the caller can distinguish
/// "no event" (out[0] == 0) from "event without payload" (out[0] == 1, payload null).
#[no_mangle]
pub extern "system" fn Java_org_screencast_app_NativeCore_nativePoll(
    env: JNIEnv,
    _class: JClass,
    handle: jlong,
    out: JLongArray,
) -> jbyteArray {
    if handle == 0 || out.is_null() {
        return std::ptr::null_mut();
    }
    match env.get_array_length(&out) {
        Ok(length) if length >= POLL_SLOTS as jsize => {}
        _ => return std::ptr::null_mut(),
    }

    let mut event = ScEvent {
        kind: 0,
        status: 0,
        component: 0,
        event_type: 0,
        flags: 0,
        codec: 0,
        frame_id: 0,
        capture_timestamp_us: 0,
        rtt_us: 0,
        config_id: 0,
        data_len: 0,
        data: std::ptr::null(),
        reserved: 0,
    };

    // SAFETY: the handle is live and `event` is a writable `ScEvent` for the duration of the call.
    let result = unsafe { sc_session_poll(session_from(handle), &mut event) };

    let mut slots = [0i64; POLL_SLOTS];
    slots[0] = i64::from(result);
    if result != 1 {
        // No event (0) or an error code: leave the remaining slots zeroed.
        let _ = env.set_long_array_region(&out, 0, &slots);
        return std::ptr::null_mut();
    }

    slots[1] = i64::from(event.kind);
    slots[2] = i64::from(event.status);
    slots[3] = i64::from(event.component);
    slots[4] = i64::from(event.event_type);
    slots[5] = i64::from(event.flags);
    slots[6] = i64::from(event.codec);
    slots[7] = event.frame_id as i64;
    slots[8] = event.capture_timestamp_us as i64;
    slots[9] = event.rtt_us as i64;
    slots[10] = i64::from(event.config_id);
    slots[11] = i64::from(event.data_len);

    // Copy the payload out before releasing the buffer: the pointer is only valid until the
    // next poll or sc_frame_release, and Java must never see it.
    let payload = if event.data.is_null() || event.data_len == 0 {
        None
    } else {
        // SAFETY: the session guarantees `data_len` readable bytes behind `data`.
        let bytes = unsafe { std::slice::from_raw_parts(event.data, event.data_len as usize) };
        Some(bytes.to_vec())
    };

    // SAFETY: the handle is live; releasing is idempotent for the session's storage.
    unsafe { sc_frame_release(session_from(handle)) };

    if env.set_long_array_region(&out, 0, &slots).is_err() {
        return std::ptr::null_mut();
    }

    match payload {
        Some(bytes) => match env.byte_array_from_slice(&bytes) {
            Ok(array) => array.into_raw(),
            Err(_) => std::ptr::null_mut(),
        },
        None => std::ptr::null_mut(),
    }
}

/// `static native void nativeStats(long handle, long[] out)`
///
/// Layout (mirrors `NativeCore`): 0..14 counters, 15/16 RTT in microseconds as
/// `double` bits (`Double.longBitsToDouble`), 17/18 path status, 19/20 local ports,
/// 21 reserved.
#[no_mangle]
pub extern "system" fn Java_org_screencast_app_NativeCore_nativeStats(
    env: JNIEnv,
    _class: JClass,
    handle: jlong,
    out: JLongArray,
) -> jint {
    if handle == 0 || out.is_null() {
        return SC_ERR_NULL;
    }
    match env.get_array_length(&out) {
        Ok(length) if length >= STATS_SLOTS as jsize => {}
        _ => return SC_ERR_INVALID_ARGUMENT,
    }

    // SAFETY: `ScStats` is plain integer/float data, so an all-zero value is a valid instance;
    // the handle is live and `stats` is writable for the duration of the call.
    let mut stats: ScStats = unsafe { std::mem::zeroed() };
    // SAFETY: same contract as above.
    unsafe { sc_session_stats(session_from(handle), &mut stats) };

    let mut slots = [0i64; STATS_SLOTS];
    slots[0] = stats.video_datagrams_received as i64;
    slots[1] = stats.video_datagrams_rejected as i64;
    slots[2] = stats.control_datagrams_received as i64;
    slots[3] = stats.control_datagrams_rejected as i64;
    slots[4] = stats.control_frames_dropped as i64;
    slots[5] = stats.assembled_frames as i64;
    slots[6] = stats.dropped_frames as i64;
    slots[7] = stats.dropped_fragments as i64;
    slots[8] = stats.video_bytes_sent as i64;
    slots[9] = stats.control_bytes_sent as i64;
    slots[10] = stats.replay_rejected as i64;
    slots[11] = stats.probes_sent as i64;
    slots[12] = stats.probes_answered as i64;
    slots[13] = stats.events_dropped as i64;
    slots[14] = stats.partial_frames as i64;
    slots[15] = stats.video_rtt_us.to_bits() as i64;
    slots[16] = stats.control_rtt_us.to_bits() as i64;
    slots[17] = i64::from(stats.video_path_status);
    slots[18] = i64::from(stats.control_path_status);
    slots[19] = i64::from(stats.video_local_port);
    slots[20] = i64::from(stats.control_local_port);
    slots[21] = i64::from(stats.reserved);

    match env.set_long_array_region(&out, 0, &slots) {
        Ok(()) => 0,
        Err(_) => SC_ERR_INVALID_ARGUMENT,
    }
}

/// `static native int nativeLocalPorts(long handle, int[] out)`
#[no_mangle]
pub extern "system" fn Java_org_screencast_app_NativeCore_nativeLocalPorts(
    env: JNIEnv,
    _class: JClass,
    handle: jlong,
    out: JIntArray,
) -> jint {
    if handle == 0 || out.is_null() {
        return SC_ERR_NULL;
    }
    match env.get_array_length(&out) {
        Ok(length) if length >= 2 => {}
        _ => return SC_ERR_INVALID_ARGUMENT,
    }

    let mut video_port: u16 = 0;
    let mut control_port: u16 = 0;
    // SAFETY: the handle is live and both out-pointers are valid for the duration of the call.
    let status =
        unsafe { sc_session_local_ports(session_from(handle), &mut video_port, &mut control_port) };
    if status != 0 {
        return status;
    }

    let ports: [jint; 2] = [jint::from(video_port), jint::from(control_port)];
    match env.set_int_array_region(&out, 0, &ports) {
        Ok(()) => 0,
        Err(_) => SC_ERR_INVALID_ARGUMENT,
    }
}

/// `static native void nativeDestroy(long handle)`
#[no_mangle]
pub extern "system" fn Java_org_screencast_app_NativeCore_nativeDestroy(
    _env: JNIEnv,
    _class: JClass,
    handle: jlong,
) {
    if handle == 0 {
        return;
    }

    // SAFETY: the handle came from nativeCreate, is destroyed exactly once by the Java owner
    // (ViewerSession) and is never used again afterwards.
    unsafe { sc_session_destroy(session_from(handle)) };
}
