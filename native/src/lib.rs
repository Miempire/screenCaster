//! ScreenCast network core: the Rust implementation of the SCT1 wire protocol.
//!
//! This crate is the reusable network core for the Android and macOS clients. It is
//! byte-for-byte compatible with the C# reference implementation in
//! `src/ScreenCast.Core/` and is verified against the same known-answer vectors.
//!
//! Implemented:
//!
//! * [`packet`] - 64-byte application header, HKDF-SHA256 key derivation, AES-256-GCM,
//!   1024-packet replay window committed only after authentication.
//! * [`control`] - 32-byte control header and a reliable ordered channel with cumulative
//!   plus selective ACK, duplicate suppression, Karn-filtered RTT, RTO and expiry.
//! * [`video`] - 32-byte fragment prefix, stateless splitter, bounded reassembler that
//!   never delivers a partial frame.
//! * [`transport`] - a real `std::net::UdpSocket` with a 4 MiB receive buffer.
//! * [`session`] - the two-socket media session with four direction-scoped key domains,
//!   single-owner receive loops, a bounded control send queue and a ticker.
//! * [`probe`] - encrypted path probes and keepalive supervision that never treats a send
//!   as liveness.
//! * [`stun`] - RFC 5389 Binding request/response with XOR-MAPPED-ADDRESS (IPv4/IPv6).
//! * [`ffi`] - the C ABI used by Android JNI and macOS Swift.
//!
//! Not implemented here (deliberately out of scope): the SCSH signalling handshake and
//! key agreement, TURN relay client, codecs, capture and input injection. Those live in the
//! platform layers or are still pending.

pub mod classifier;
pub mod clock;
pub mod control;
pub mod crypto;
pub mod ffi;
pub mod packet;
pub mod probe;
pub mod session;
pub mod stun;
pub mod transport;
pub mod video;

/// JNI bridge for the Android client. Only compiled for the Android target, where the `jni`
/// crate dependency is available; host builds and tests are unaffected.
#[cfg(target_os = "android")]
pub mod android_jni;

use std::sync::atomic::{AtomicU8, Ordering};

pub use classifier::{classify, DatagramKind};
pub use control::{ControlEventType, ControlFrame, ReliableControlChannel};
pub use ffi::{
    sc_frame_release, sc_session_bind, sc_session_create, sc_session_destroy,
    sc_session_local_ports, sc_session_poll, sc_session_send_control, sc_session_send_frame,
    sc_session_set_peer, sc_session_start, sc_session_stats, ScEvent, ScSession, ScStats,
};
pub use packet::{
    Header, PacketChannel, PacketDirection, PacketProtector, ParseError, ProtectError, ReplayWindow,
};
pub use probe::{PathProbe, PathStatus, ProbeMessage};
pub use session::{
    MediaSession, PathComponent, SessionConfig, SessionError, SessionEvent, SessionStats,
};
pub use video::{
    AssembledVideoFrame, VideoAssembler, VideoCodec, VideoFragmentHeader, VideoFragmenter,
};

/// ABI version packed as major << 16 | minor << 8 | patch (0.2.0 = 512).
///
/// 0.2.0 adds the session API (`sc_session_*`). The three 0.1.0 functions are unchanged.
pub const ABI_VERSION: u32 = 0x0000_0200;

/// Session role.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum Role {
    Host = 0,
    Viewer = 1,
}

impl TryFrom<u8> for Role {
    type Error = ();
    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            0 => Ok(Self::Host),
            1 => Ok(Self::Viewer),
            _ => Err(()),
        }
    }
}

/// Process-local selection only: it never starts a capture or a network session.
pub struct RoleState(AtomicU8);

impl Default for RoleState {
    fn default() -> Self {
        Self(AtomicU8::new(Role::Viewer as u8))
    }
}

impl RoleState {
    pub fn set(&self, role: Role) {
        self.0.store(role as u8, Ordering::Relaxed);
    }

    pub fn get(&self) -> Role {
        Role::try_from(self.0.load(Ordering::Relaxed)).expect("only valid roles stored")
    }
}

static ROLE: RoleState = RoleState(AtomicU8::new(Role::Viewer as u8));

/// Packed ABI version; see [`ABI_VERSION`].
#[no_mangle]
pub extern "C" fn sc_core_version() -> u32 {
    ABI_VERSION
}

/// Returns 0 for Host or 1 for Viewer.
#[no_mangle]
pub extern "C" fn sc_get_role() -> u8 {
    ROLE.get() as u8
}

/// Returns 0 on success or -1 on invalid role; invalid input preserves state.
#[no_mangle]
pub extern "C" fn sc_set_role(role: u8) -> i32 {
    match Role::try_from(role) {
        Ok(role) => {
            ROLE.set(role);
            0
        }
        Err(()) => -1,
    }
}

#[cfg(target_os = "android")]
mod android {
    use jni::{objects::JClass, sys::jint, JNIEnv};

    #[no_mangle]
    pub extern "system" fn Java_org_screencast_app_NativeCore_version(
        _env: JNIEnv,
        _class: JClass,
    ) -> jint {
        super::sc_core_version() as jint
    }

    #[no_mangle]
    pub extern "system" fn Java_org_screencast_app_NativeCore_getRole(
        _env: JNIEnv,
        _class: JClass,
    ) -> jint {
        super::sc_get_role() as jint
    }

    #[no_mangle]
    pub extern "system" fn Java_org_screencast_app_NativeCore_setRole(
        _env: JNIEnv,
        _class: JClass,
        role: jint,
    ) -> jint {
        match u8::try_from(role) {
            Ok(role) => super::sc_set_role(role),
            Err(_) => -1,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture() -> Vec<u8> {
        let mut bytes = vec![0; 67];
        bytes[..4].copy_from_slice(b"SCT1");
        bytes[4] = 1;
        bytes[5] = 2;
        bytes[6..8].copy_from_slice(&1_u16.to_be_bytes());
        bytes[8..24].copy_from_slice(&[0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]);
        bytes[24..28].copy_from_slice(&0x0102_0304_u32.to_be_bytes());
        bytes[28..32].copy_from_slice(&0x0506_0708_u32.to_be_bytes());
        bytes[32..40].copy_from_slice(&0x0102_0304_0506_0708_u64.to_be_bytes());
        bytes[40..42].copy_from_slice(&3_u16.to_be_bytes());
        bytes[42..44].copy_from_slice(&64_u16.to_be_bytes());
        bytes[48..64].fill(0xab);
        bytes[64..].copy_from_slice(&[0xca, 0xfe, 0x01]);
        bytes
    }

    #[test]
    fn matches_shared_wire_vector() {
        let hex = include_str!("../tests/vectors/header-v1.hex").trim();
        let bytes: Vec<u8> = (0..hex.len())
            .step_by(2)
            .map(|index| u8::from_str_radix(&hex[index..index + 2], 16).expect("hex"))
            .collect();
        assert_eq!(bytes, fixture());
        assert!(Header::parse(&bytes).is_ok());
    }

    #[test]
    fn reads_network_order_and_raw_session_id() {
        let header = Header::parse(&fixture()).expect("parse");
        assert_eq!(header.channel, PacketChannel::ReliableControl);
        assert_eq!(header.flags, 1);
        assert_eq!(
            header.session_id,
            [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]
        );
        assert_eq!(header.key_epoch, 0x0102_0304);
        assert_eq!(header.stream_id, 0x0506_0708);
        assert_eq!(header.packet_sequence, 0x0102_0304_0506_0708);
        assert_eq!(header.payload_length, 3);
        assert_eq!(header.tag, [0xab; 16]);
    }

    #[test]
    fn rejects_each_invalid_field() {
        for (offset, value, error) in [
            (0, 0, ParseError::Magic),
            (4, 2, ParseError::Version),
            (5, 5, ParseError::Channel),
            (7, 2, ParseError::Flags),
            (43, 63, ParseError::HeaderLength),
            (44, 1, ParseError::Reserved),
            (41, 4, ParseError::PayloadLength),
        ] {
            let mut bytes = fixture();
            bytes[offset] = value;
            assert_eq!(Header::parse(&bytes), Err(error));
        }

        let mut bytes = fixture();
        bytes[24..28].fill(0);
        assert_eq!(Header::parse(&bytes), Err(ParseError::Epoch));
    }

    #[test]
    fn rejects_every_truncated_header_and_trailing_bytes() {
        let bytes = fixture();
        for length in 0..64 {
            assert_eq!(Header::parse(&bytes[..length]), Err(ParseError::Truncated));
        }
        assert_eq!(Header::parse(&bytes[..66]), Err(ParseError::PayloadLength));
        let mut trailing = bytes;
        trailing.push(0);
        assert_eq!(Header::parse(&trailing), Err(ParseError::PayloadLength));
    }

    #[test]
    fn permits_empty_and_maximum_payload() {
        let mut bytes = fixture();
        bytes.resize(64, 0);
        bytes[40..42].copy_from_slice(&0_u16.to_be_bytes());
        assert!(Header::parse(&bytes).is_ok());

        bytes.resize(1200, 0);
        bytes[40..42].copy_from_slice(&1136_u16.to_be_bytes());
        assert!(Header::parse(&bytes).is_ok());

        bytes.push(0);
        assert_eq!(Header::parse(&bytes), Err(ParseError::Oversized));
    }

    #[test]
    fn role_state_and_abi_validate_inputs() {
        let state = RoleState::default();
        assert_eq!(state.get(), Role::Viewer);
        state.set(Role::Host);
        assert_eq!(state.get(), Role::Host);
        state.set(Role::Viewer);
        assert_eq!(state.get(), Role::Viewer);

        assert_eq!(sc_core_version(), ABI_VERSION);
        assert_eq!(sc_core_version(), 512);
        assert_eq!(sc_set_role(0), 0);
        assert_eq!(sc_get_role(), 0);
        assert_eq!(sc_set_role(2), -1);
        assert_eq!(sc_get_role(), 0);
        assert_eq!(sc_set_role(1), 0);
        assert_eq!(sc_get_role(), 1);
    }
}
