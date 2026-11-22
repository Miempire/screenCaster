//! C ABI for the Android JNI and macOS Swift platform layers.
//!
//! Rules this module guarantees:
//!
//! * No panic may cross the boundary. Every entry point validates its pointers and
//!   arguments and returns a negative status code instead of unwinding. (`catch_unwind`
//!   is only a development-profile net: the release profile is built with `panic = "abort"`.)
//! * No Rust `String`, `Vec`, trait object or generic type is ever passed by value. The only
//!   shared types are `#[repr(C)]` structs and opaque pointers.
//! * Pointers returned by [`sc_session_poll`] stay valid until the next poll that produces a
//!   video frame/control body or until [`sc_frame_release`] is called. The caller must copy
//!   the bytes out before then.
//!
//! Status codes are negative; a successful `sc_session_bind` returns the bound video port.

use std::net::{IpAddr, SocketAddr};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;
use std::str::FromStr;
use std::sync::Mutex;

use crate::probe::PathStatus;
use crate::session::{
    MediaSession, PathComponent, SessionConfig, SessionError, SessionEvent, SessionStats,
};
use crate::video::MAX_FRAME_BYTES;

/// Success.
pub const SC_OK: i32 = 0;
/// A required pointer argument was null.
pub const SC_ERR_NULL: i32 = -1;
/// An argument was syntactically invalid (address, port, event type, empty frame).
pub const SC_ERR_INVALID_ARGUMENT: i32 = -2;
/// The session has no bound sockets yet.
pub const SC_ERR_NOT_BOUND: i32 = -3;
/// The session is already bound.
pub const SC_ERR_ALREADY_BOUND: i32 = -4;
/// The operating system rejected a socket operation.
pub const SC_ERR_IO: i32 = -5;
/// The session is in the wrong state for this call (no peer, control window full, ...).
pub const SC_ERR_STATE: i32 = -6;
/// The payload exceeds the protocol ceiling.
pub const SC_ERR_TOO_LARGE: i32 = -7;
/// Internal invariant violated; the call had no effect.
pub const SC_ERR_PROTOCOL: i32 = -8;

/// No event available.
pub const SC_EVENT_NONE: i32 = 0;
/// A complete encoded video frame.
pub const SC_EVENT_VIDEO_FRAME: i32 = 1;
/// A reliable control event delivered exactly once, in order.
pub const SC_EVENT_CONTROL: i32 = 2;
/// A path status change for one component.
pub const SC_EVENT_PATH: i32 = 3;
/// A reliable control event expired; the platform must release held keys.
pub const SC_EVENT_CONTROL_EXPIRED: i32 = 4;

/// One event handed to the platform layer.
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct ScEvent {
    /// One of the `SC_EVENT_*` constants.
    pub kind: i32,
    /// `SC_EVENT_PATH`: one of the `SC_PATH_*` constants. Otherwise 0.
    pub status: i32,
    /// `SC_EVENT_PATH`: 0 video, 1 control. Otherwise 0.
    pub component: i32,
    /// `SC_EVENT_CONTROL` / `SC_EVENT_CONTROL_EXPIRED`: the control event type.
    pub event_type: u16,
    /// `SC_EVENT_VIDEO_FRAME`: key/config/discontinuity bits.
    pub flags: u8,
    /// `SC_EVENT_VIDEO_FRAME`: 0 unknown, 1 H.264, 2 AV1.
    pub codec: u8,
    /// `SC_EVENT_VIDEO_FRAME`: sender frame id.
    pub frame_id: u64,
    /// `SC_EVENT_VIDEO_FRAME`: sender capture timestamp in microseconds.
    pub capture_timestamp_us: u64,
    /// `SC_EVENT_PATH`: smoothed RTT in microseconds, `UINT64_MAX` when unknown.
    pub rtt_us: u64,
    /// `SC_EVENT_VIDEO_FRAME`: configuration id.
    pub config_id: u32,
    /// Length of the payload behind `data`.
    pub data_len: u32,
    /// Payload pointer, or null when the event carries no bytes.
    pub data: *const u8,
    /// Reserved, always zero.
    pub reserved: u32,
}

/// Session counters.
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct ScStats {
    pub video_datagrams_received: u64,
    pub video_datagrams_rejected: u64,
    pub control_datagrams_received: u64,
    pub control_datagrams_rejected: u64,
    pub control_frames_dropped: u64,
    pub assembled_frames: u64,
    pub dropped_frames: u64,
    pub dropped_fragments: u64,
    pub video_bytes_sent: u64,
    pub control_bytes_sent: u64,
    pub replay_rejected: u64,
    pub probes_sent: u64,
    pub probes_answered: u64,
    pub events_dropped: u64,
    pub partial_frames: u64,
    /// Smoothed RTT in microseconds, -1 when unknown.
    pub video_rtt_us: f64,
    /// Smoothed RTT in microseconds, -1 when unknown.
    pub control_rtt_us: f64,
    /// 0 probing, 1 alive, 2 down.
    pub video_path_status: i32,
    /// 0 probing, 1 alive, 2 down.
    pub control_path_status: i32,
    pub video_local_port: u32,
    pub control_local_port: u32,
    pub reserved: u32,
}

/// Opaque session handle; the C header declares it as `sc_session_t`.
pub struct ScSession {
    config: SessionConfig,
    state: Mutex<SessionState>,
}

struct SessionState {
    session: Option<MediaSession>,
    storage: Vec<u8>,
}

impl ScSession {
    fn new(config: SessionConfig) -> Self {
        Self {
            config,
            state: Mutex::new(SessionState {
                session: None,
                storage: Vec::new(),
            }),
        }
    }
}

fn status_of(error: &SessionError) -> i32 {
    match error {
        SessionError::Io(_) => SC_ERR_IO,
        SessionError::PeerNotSet => SC_ERR_STATE,
        SessionError::EmptyFrame => SC_ERR_INVALID_ARGUMENT,
        SessionError::FrameTooLarge => SC_ERR_TOO_LARGE,
        SessionError::InvalidControlBody => SC_ERR_TOO_LARGE,
        SessionError::ControlWindowFull => SC_ERR_STATE,
        SessionError::ControlSequenceExhausted => SC_ERR_STATE,
        SessionError::Crypto(_) => SC_ERR_PROTOCOL,
        SessionError::Video(_) => SC_ERR_INVALID_ARGUMENT,
        SessionError::SameSocket => SC_ERR_INVALID_ARGUMENT,
    }
}

fn guard(body: impl FnOnce() -> i32) -> i32 {
    match catch_unwind(AssertUnwindSafe(body)) {
        Ok(status) => status,
        Err(_) => SC_ERR_PROTOCOL,
    }
}

fn path_status_code(status: PathStatus) -> i32 {
    match status {
        PathStatus::Probing => 0,
        PathStatus::Alive => 1,
        PathStatus::Down => 2,
    }
}

fn parse_address(address: &str, port: u16) -> Option<SocketAddr> {
    let ip = IpAddr::from_str(address).ok()?;
    Some(SocketAddr::new(ip, port))
}

/// Copy a caller-provided address string.
///
/// # Safety
/// `address` must be a valid NUL-terminated C string or null.
unsafe fn read_address(address: *const std::ffi::c_char) -> Option<String> {
    if address.is_null() {
        return None;
    }
    // SAFETY: the caller guarantees a NUL-terminated string.
    let text = unsafe { std::ffi::CStr::from_ptr(address) };
    text.to_str().ok().map(str::to_owned)
}

/// Create a session.
///
/// # Safety
/// `secret32` must point to at least 32 readable bytes and `session_id16` to at least 16
/// readable bytes. Both must stay valid for the duration of the call.
#[no_mangle]
pub unsafe extern "C" fn sc_session_create(
    secret32: *const u8,
    session_id16: *const u8,
    epoch: u32,
    is_host: bool,
) -> *mut ScSession {
    if secret32.is_null() || session_id16.is_null() || epoch == 0 {
        return ptr::null_mut();
    }

    let created = catch_unwind(AssertUnwindSafe(|| {
        let mut secret = [0u8; 32];
        let mut session_id = [0u8; 16];
        // SAFETY: the caller guarantees the readable extents documented above.
        unsafe {
            ptr::copy_nonoverlapping(secret32, secret.as_mut_ptr(), 32);
            ptr::copy_nonoverlapping(session_id16, session_id.as_mut_ptr(), 16);
        }
        ScSession::new(SessionConfig::new(secret, session_id, epoch, is_host))
    }));

    match created {
        Ok(session) => Box::into_raw(Box::new(session)),
        Err(_) => ptr::null_mut(),
    }
}

/// Bind both media sockets.
///
/// Returns the bound video port (a positive value) on success, or a negative `SC_ERR_*`
/// code. Use [`sc_session_local_ports`] to read both ports.
///
/// # Safety
/// `session` must be a pointer returned by [`sc_session_create`] that has not been destroyed.
#[no_mangle]
pub unsafe extern "C" fn sc_session_bind(
    session: *mut ScSession,
    video_port: u16,
    control_port: u16,
) -> i32 {
    guard(|| {
        // SAFETY: documented by the caller contract.
        let Some(session) = (unsafe { session.as_ref() }) else {
            return SC_ERR_NULL;
        };
        if video_port != 0 && video_port == control_port {
            return SC_ERR_INVALID_ARGUMENT;
        }

        let mut state = session.state.lock().expect("session mutex");
        if state.session.is_some() {
            return SC_ERR_ALREADY_BOUND;
        }

        match MediaSession::bind(session.config.clone(), video_port, control_port) {
            Ok(media) => {
                let port = media.local_ports().0 as i32;
                state.session = Some(media);
                port
            }
            Err(error) => status_of(&error),
        }
    })
}

/// Set both peer endpoints. Addresses are literal IPv4 or IPv6 text (no DNS resolution).
///
/// # Safety
/// `session` must be a live handle and both address pointers must be NUL-terminated strings.
#[no_mangle]
pub unsafe extern "C" fn sc_session_set_peer(
    session: *mut ScSession,
    video_address: *const std::ffi::c_char,
    video_port: u16,
    control_address: *const std::ffi::c_char,
    control_port: u16,
) -> i32 {
    guard(|| {
        // SAFETY: documented by the caller contract.
        let Some(session) = (unsafe { session.as_ref() }) else {
            return SC_ERR_NULL;
        };
        // SAFETY: documented by the caller contract.
        let (Some(video_text), Some(control_text)) =
            (unsafe { read_address(video_address) }, unsafe {
                read_address(control_address)
            })
        else {
            return SC_ERR_NULL;
        };

        let (Some(video), Some(control)) = (
            parse_address(&video_text, video_port),
            parse_address(&control_text, control_port),
        ) else {
            return SC_ERR_INVALID_ARGUMENT;
        };

        let state = session.state.lock().expect("session mutex");
        let Some(media) = state.session.as_ref() else {
            return SC_ERR_NOT_BOUND;
        };
        media.set_peers(video, control);
        SC_OK
    })
}

/// Start the receive/send threads. Idempotent.
///
/// # Safety
/// `session` must be a live handle.
#[no_mangle]
pub unsafe extern "C" fn sc_session_start(session: *mut ScSession) -> i32 {
    guard(|| {
        // SAFETY: documented by the caller contract.
        let Some(session) = (unsafe { session.as_ref() }) else {
            return SC_ERR_NULL;
        };
        let state = session.state.lock().expect("session mutex");
        let Some(media) = state.session.as_ref() else {
            return SC_ERR_NOT_BOUND;
        };
        match media.start() {
            Ok(()) => SC_OK,
            Err(error) => status_of(&error),
        }
    })
}

/// Fragment, encrypt and send one encoded frame.
///
/// # Safety
/// `session` must be a live handle and `data` must point to `len` readable bytes.
#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn sc_session_send_frame(
    session: *mut ScSession,
    data: *const u8,
    len: u32,
    frame_id: u64,
    timestamp_us: u64,
    config_id: u32,
    flags: u8,
    codec: u8,
) -> i32 {
    guard(|| {
        // SAFETY: documented by the caller contract.
        let Some(session) = (unsafe { session.as_ref() }) else {
            return SC_ERR_NULL;
        };
        if len == 0 {
            return SC_ERR_INVALID_ARGUMENT;
        }
        if len as usize > MAX_FRAME_BYTES {
            return SC_ERR_TOO_LARGE;
        }
        if data.is_null() {
            return SC_ERR_NULL;
        }

        // SAFETY: the caller guarantees `len` readable bytes.
        let frame = unsafe { std::slice::from_raw_parts(data, len as usize) };
        let state = session.state.lock().expect("session mutex");
        let Some(media) = state.session.as_ref() else {
            return SC_ERR_NOT_BOUND;
        };
        match media.send_video_frame(frame, frame_id, timestamp_us, config_id, flags, codec) {
            Ok(_) => SC_OK,
            Err(error) => status_of(&error),
        }
    })
}

/// Queue one reliable control event.
///
/// # Safety
/// `session` must be a live handle; `body` must point to `len` readable bytes when `len > 0`.
#[no_mangle]
pub unsafe extern "C" fn sc_session_send_control(
    session: *mut ScSession,
    event_type: u16,
    body: *const u8,
    len: u16,
) -> i32 {
    guard(|| {
        // SAFETY: documented by the caller contract.
        let Some(session) = (unsafe { session.as_ref() }) else {
            return SC_ERR_NULL;
        };
        if event_type == 0 {
            return SC_ERR_INVALID_ARGUMENT;
        }
        if len as usize > crate::control::MAX_BODY_LENGTH {
            return SC_ERR_TOO_LARGE;
        }
        if len > 0 && body.is_null() {
            return SC_ERR_NULL;
        }

        let payload: &[u8] = if len == 0 {
            &[]
        } else {
            // SAFETY: the caller guarantees `len` readable bytes.
            unsafe { std::slice::from_raw_parts(body, len as usize) }
        };

        let state = session.state.lock().expect("session mutex");
        let Some(media) = state.session.as_ref() else {
            return SC_ERR_NOT_BOUND;
        };
        match media.send_control(event_type, payload) {
            Ok(()) => SC_OK,
            Err(error) => status_of(&error),
        }
    })
}

/// Poll one event without blocking.
///
/// Returns 1 when `out` was filled, 0 when no event is pending and a negative `SC_ERR_*`
/// code otherwise.
///
/// # Safety
/// `session` must be a live handle and `out` must point to a writable [`ScEvent`].
#[no_mangle]
pub unsafe extern "C" fn sc_session_poll(session: *mut ScSession, out: *mut ScEvent) -> i32 {
    guard(|| {
        // SAFETY: documented by the caller contract.
        let Some(session) = (unsafe { session.as_ref() }) else {
            return SC_ERR_NULL;
        };
        if out.is_null() {
            return SC_ERR_NULL;
        }

        let mut state = session.state.lock().expect("session mutex");
        let Some(media) = state.session.as_ref() else {
            return SC_ERR_NOT_BOUND;
        };
        let Some(event) = media.poll_event() else {
            return 0;
        };

        let mut event_out = ScEvent {
            kind: SC_EVENT_NONE,
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
            data: ptr::null(),
            reserved: 0,
        };

        match event {
            SessionEvent::VideoFrame(frame) => {
                event_out.kind = SC_EVENT_VIDEO_FRAME;
                event_out.flags = frame.flags;
                event_out.codec = frame.codec;
                event_out.frame_id = frame.frame_id;
                event_out.capture_timestamp_us = frame.capture_timestamp_micros;
                event_out.config_id = frame.config_id;
                state.storage = frame.data;
                event_out.data_len = state.storage.len() as u32;
                event_out.data = state.storage.as_ptr();
            }
            SessionEvent::Control { event_type, body } => {
                event_out.kind = SC_EVENT_CONTROL;
                event_out.event_type = event_type;
                state.storage = body;
                event_out.data_len = state.storage.len() as u32;
                event_out.data = state.storage.as_ptr();
            }
            SessionEvent::ControlExpired { event_type } => {
                event_out.kind = SC_EVENT_CONTROL_EXPIRED;
                event_out.event_type = event_type;
                state.storage.clear();
            }
            SessionEvent::PathStatus {
                component,
                status,
                rtt_micros,
            } => {
                event_out.kind = SC_EVENT_PATH;
                event_out.component = match component {
                    PathComponent::Video => 0,
                    PathComponent::Control => 1,
                };
                event_out.status = path_status_code(status);
                event_out.rtt_us = if rtt_micros < 0.0 {
                    u64::MAX
                } else {
                    rtt_micros as u64
                };
                state.storage.clear();
            }
        }

        // SAFETY: the caller guarantees a writable ScEvent.
        unsafe { ptr::write(out, event_out) };
        1
    })
}

/// Release the buffer behind the most recent [`sc_session_poll`] payload pointer.
///
/// # Safety
/// `session` must be a live handle or null.
#[no_mangle]
pub unsafe extern "C" fn sc_frame_release(session: *mut ScSession) {
    if session.is_null() {
        return;
    }
    // SAFETY: documented by the caller contract.
    let session = unsafe { &*session };
    let mut state = session.state.lock().expect("session mutex");
    state.storage = Vec::new();
}

/// Copy the session counters.
///
/// # Safety
/// `session` must be a live handle and `out` must point to a writable [`ScStats`].
#[no_mangle]
pub unsafe extern "C" fn sc_session_stats(session: *mut ScSession, out: *mut ScStats) {
    if session.is_null() || out.is_null() {
        return;
    }
    // SAFETY: documented by the caller contract.
    let session = unsafe { &*session };

    let state = session.state.lock().expect("session mutex");
    let snapshot: SessionStats = state
        .session
        .as_ref()
        .map(MediaSession::stats)
        .unwrap_or_default();

    let stats = ScStats {
        video_datagrams_received: snapshot.video_datagrams_received,
        video_datagrams_rejected: snapshot.video_datagrams_rejected,
        control_datagrams_received: snapshot.control_datagrams_received,
        control_datagrams_rejected: snapshot.control_datagrams_rejected,
        control_frames_dropped: snapshot.control_frames_dropped,
        assembled_frames: snapshot.assembled_frames,
        dropped_frames: snapshot.dropped_frames,
        dropped_fragments: snapshot.dropped_fragments,
        video_bytes_sent: snapshot.video_bytes_sent,
        control_bytes_sent: snapshot.control_bytes_sent,
        replay_rejected: snapshot.replay_rejected,
        probes_sent: snapshot.probes_sent,
        probes_answered: snapshot.probes_answered,
        events_dropped: snapshot.events_dropped,
        partial_frames: snapshot.partial_frames,
        video_rtt_us: snapshot.video_rtt_micros,
        control_rtt_us: snapshot.control_rtt_micros,
        video_path_status: path_status_code(snapshot.video_path_status),
        control_path_status: path_status_code(snapshot.control_path_status),
        video_local_port: u32::from(snapshot.video_local_port),
        control_local_port: u32::from(snapshot.control_local_port),
        reserved: 0,
    };

    // SAFETY: the caller guarantees a writable ScStats.
    unsafe { ptr::write(out, stats) };
}

/// Copy both bound local ports.
///
/// # Safety
/// `session` must be a live handle; the port pointers may be null to skip one of them.
#[no_mangle]
pub unsafe extern "C" fn sc_session_local_ports(
    session: *mut ScSession,
    video_port: *mut u16,
    control_port: *mut u16,
) -> i32 {
    guard(|| {
        // SAFETY: documented by the caller contract.
        let Some(session) = (unsafe { session.as_ref() }) else {
            return SC_ERR_NULL;
        };
        let state = session.state.lock().expect("session mutex");
        let Some(media) = state.session.as_ref() else {
            return SC_ERR_NOT_BOUND;
        };
        let (video, control) = media.local_ports();
        if !video_port.is_null() {
            // SAFETY: the caller guarantees a writable u16.
            unsafe { ptr::write(video_port, video) };
        }
        if !control_port.is_null() {
            // SAFETY: the caller guarantees a writable u16.
            unsafe { ptr::write(control_port, control) };
        }
        SC_OK
    })
}

/// Stop the threads and free the session. Null is a no-op.
///
/// # Safety
/// `session` must be a pointer returned by [`sc_session_create`] and must not be used again.
#[no_mangle]
pub unsafe extern "C" fn sc_session_destroy(session: *mut ScSession) {
    if session.is_null() {
        return;
    }
    // SAFETY: the caller transfers ownership of exactly one Box back to us.
    let boxed = unsafe { Box::from_raw(session) };
    if let Ok(state) = boxed.state.lock() {
        if let Some(media) = state.session.as_ref() {
            media.stop();
        }
    }
    drop(boxed);
}
