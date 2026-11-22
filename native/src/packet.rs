//! SCT1 application datagram: 64-byte header plus AES-256-GCM payload.
//!
//! Byte-for-byte compatible with `src/ScreenCast.Core/Packets.cs`:
//!
//! | offset | length | field |
//! |---:|---:|---|
//! | 0 | 4 | magic `SCT1` |
//! | 4 | 1 | version = 1 |
//! | 5 | 1 | channel (0 signalling, 1 video, 2 reliable control, 3 probe, 4 audio) |
//! | 6 | 2 | flags (bit 0 = ACK-only) |
//! | 8 | 16 | session id, 16 raw bytes |
//! | 24 | 4 | key epoch, starts at 1 |
//! | 28 | 4 | stream id |
//! | 32 | 8 | packet sequence, per direction/channel/stream/epoch |
//! | 40 | 2 | payload length (ciphertext length == plaintext length) |
//! | 42 | 2 | header length = 64 |
//! | 44 | 4 | reserved = 0 |
//! | 48 | 16 | AES-GCM tag |
//! | 64 | N | ciphertext |

use std::sync::Mutex;

use crate::crypto::gcm::{AesGcm256, NONCE, TAG};
use crate::crypto::hkdf;

/// Fixed application header length in bytes.
pub const HEADER_LENGTH: usize = 64;
/// Maximum UDP payload for a ScreenCast application datagram.
pub const MAX_DATAGRAM_LENGTH: usize = 1200;
/// Largest plaintext that still fits one datagram (1200 - 64).
pub const MAX_PAYLOAD_LENGTH: usize = MAX_DATAGRAM_LENGTH - HEADER_LENGTH;
/// Replay window width in packets.
pub const REPLAY_WINDOW: u64 = 1024;
/// Protocol version carried in the header.
pub const VERSION: u8 = 1;
/// ASCII "SCT1".
pub const MAGIC: [u8; 4] = *b"SCT1";
/// HKDF label for the per-direction media keys.
pub const HKDF_LABEL: &[u8] = b"ScreenCast/AES-256-GCM";

/// Logical transport channel.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum PacketChannel {
    Signaling = 0,
    Video = 1,
    ReliableControl = 2,
    Probe = 3,
    Audio = 4,
}

impl PacketChannel {
    pub fn from_u8(value: u8) -> Option<Self> {
        match value {
            0 => Some(Self::Signaling),
            1 => Some(Self::Video),
            2 => Some(Self::ReliableControl),
            3 => Some(Self::Probe),
            4 => Some(Self::Audio),
            _ => None,
        }
    }

    pub const fn as_u8(self) -> u8 {
        self as u8
    }
}

/// Key derivation direction. Host sends HostToViewer and receives ViewerToHost.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum PacketDirection {
    HostToViewer = 0,
    ViewerToHost = 1,
}

impl PacketDirection {
    pub const fn as_u8(self) -> u8 {
        self as u8
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ParseError {
    Truncated,
    Oversized,
    Magic,
    Version,
    Channel,
    Flags,
    Epoch,
    HeaderLength,
    Reserved,
    PayloadLength,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Header {
    pub channel: PacketChannel,
    pub flags: u16,
    pub session_id: [u8; 16],
    pub key_epoch: u32,
    pub stream_id: u32,
    pub packet_sequence: u64,
    pub payload_length: u16,
    pub tag: [u8; TAG],
}

impl Header {
    /// True when the ACK-only flag is set.
    pub const fn is_ack_only(&self) -> bool {
        self.flags & 1 != 0
    }

    /// Structural parse of a complete datagram. Fields are untrusted until the AEAD tag
    /// is verified by [`PacketProtector::decode`].
    pub fn parse(datagram: &[u8]) -> Result<Self, ParseError> {
        if datagram.len() < HEADER_LENGTH {
            return Err(ParseError::Truncated);
        }
        if datagram.len() > MAX_DATAGRAM_LENGTH {
            return Err(ParseError::Oversized);
        }
        if datagram[0..4] != MAGIC {
            return Err(ParseError::Magic);
        }
        if datagram[4] != VERSION {
            return Err(ParseError::Version);
        }
        let channel = PacketChannel::from_u8(datagram[5]).ok_or(ParseError::Channel)?;
        let flags = u16::from_be_bytes([datagram[6], datagram[7]]);
        if flags & !1 != 0 {
            return Err(ParseError::Flags);
        }
        let key_epoch = read_u32(datagram, 24);
        if key_epoch == 0 {
            return Err(ParseError::Epoch);
        }
        if u16::from_be_bytes([datagram[42], datagram[43]]) != HEADER_LENGTH as u16 {
            return Err(ParseError::HeaderLength);
        }
        if read_u32(datagram, 44) != 0 {
            return Err(ParseError::Reserved);
        }
        let payload_length = u16::from_be_bytes([datagram[40], datagram[41]]);
        if payload_length as usize > MAX_PAYLOAD_LENGTH {
            return Err(ParseError::PayloadLength);
        }
        if datagram.len() != HEADER_LENGTH + usize::from(payload_length) {
            return Err(ParseError::PayloadLength);
        }

        let mut session_id = [0u8; 16];
        session_id.copy_from_slice(&datagram[8..24]);
        let mut tag = [0u8; TAG];
        tag.copy_from_slice(&datagram[48..64]);

        Ok(Self {
            channel,
            flags,
            session_id,
            key_epoch,
            stream_id: read_u32(datagram, 28),
            packet_sequence: read_u64(datagram, 32),
            payload_length,
            tag,
        })
    }

    /// Write the 48 authenticated header bytes plus a zero tag placeholder.
    ///
    /// The caller must write the real tag at offset 48 afterwards; keeping the tag out of
    /// this function makes it impossible to authenticate a tag that was never computed.
    pub fn write(&self, destination: &mut [u8]) -> Result<(), ParseError> {
        if destination.len() < HEADER_LENGTH {
            return Err(ParseError::Truncated);
        }
        if self.key_epoch == 0
            || self.payload_length as usize > MAX_PAYLOAD_LENGTH
            || self.flags & !1 != 0
        {
            return Err(ParseError::PayloadLength);
        }

        destination[..HEADER_LENGTH].fill(0);
        destination[0..4].copy_from_slice(&MAGIC);
        destination[4] = VERSION;
        destination[5] = self.channel.as_u8();
        destination[6..8].copy_from_slice(&self.flags.to_be_bytes());
        destination[8..24].copy_from_slice(&self.session_id);
        destination[24..28].copy_from_slice(&self.key_epoch.to_be_bytes());
        destination[28..32].copy_from_slice(&self.stream_id.to_be_bytes());
        destination[32..40].copy_from_slice(&self.packet_sequence.to_be_bytes());
        destination[40..42].copy_from_slice(&self.payload_length.to_be_bytes());
        destination[42..44].copy_from_slice(&(HEADER_LENGTH as u16).to_be_bytes());
        // 44..48 reserved stays zero.
        Ok(())
    }
}

fn read_u32(source: &[u8], offset: usize) -> u32 {
    u32::from_be_bytes([
        source[offset],
        source[offset + 1],
        source[offset + 2],
        source[offset + 3],
    ])
}

fn read_u64(source: &[u8], offset: usize) -> u64 {
    u64::from_be_bytes([
        source[offset],
        source[offset + 1],
        source[offset + 2],
        source[offset + 3],
        source[offset + 4],
        source[offset + 5],
        source[offset + 6],
        source[offset + 7],
    ])
}

/// 1024-packet sliding replay bitmap. A packet is only committed after its tag verifies,
/// so an attacker cannot poison the window with forged sequence numbers.
#[derive(Debug, Clone)]
pub struct ReplayWindow {
    received: [bool; REPLAY_WINDOW as usize],
    initialized: bool,
    highest: u64,
}

impl Default for ReplayWindow {
    fn default() -> Self {
        Self::new()
    }
}

impl ReplayWindow {
    pub const fn new() -> Self {
        Self {
            received: [false; REPLAY_WINDOW as usize],
            initialized: false,
            highest: 0,
        }
    }

    pub const fn highest(&self) -> u64 {
        self.highest
    }

    /// Whether this sequence is new enough to be worth authenticating.
    pub fn can_accept(&self, sequence: u64) -> bool {
        if !self.initialized {
            return true;
        }
        if sequence > self.highest {
            return true;
        }
        let distance = self.highest - sequence;
        distance < REPLAY_WINDOW && !self.received[(sequence % REPLAY_WINDOW) as usize]
    }

    /// Record an authenticated sequence number.
    pub fn commit(&mut self, sequence: u64) {
        if !self.initialized {
            self.initialized = true;
            self.highest = sequence;
        } else if sequence > self.highest {
            let distance = sequence - self.highest;
            if distance >= REPLAY_WINDOW {
                self.received = [false; REPLAY_WINDOW as usize];
            } else {
                for step in 1..=distance {
                    let index = (self.highest.wrapping_add(step) % REPLAY_WINDOW) as usize;
                    self.received[index] = false;
                }
            }
            self.highest = sequence;
        }
        self.received[(sequence % REPLAY_WINDOW) as usize] = true;
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProtectError {
    /// Structural header failure (length, magic, version, reserved bytes).
    Malformed,
    /// Session id, epoch, stream or channel does not match this protector.
    WrongContext,
    /// Sequence is older than the replay window or already seen.
    Replay,
    /// AEAD tag verification failed.
    Authentication,
    /// The 64-bit packet sequence space is exhausted; a rekey is required.
    SequenceExhausted,
    /// The plaintext does not fit one datagram.
    TooLarge,
}

struct ProtectorState {
    replay: ReplayWindow,
    next_sequence: u64,
    exhausted: bool,
}

/// One authenticated session/direction/channel/stream/epoch.
///
/// The key is derived once in the constructor from HKDF-SHA256:
/// `info = "ScreenCast/AES-256-GCM" || 0x01 || sessionId16 || epochBE4 || direction1 ||
/// channel1 || streamBE4`, `salt = sessionId`, nonce `= epochBE4 || sequenceBE8`, and the
/// first 48 header bytes are the AAD.
pub struct PacketProtector {
    session_id: [u8; 16],
    key_epoch: u32,
    stream_id: u32,
    channel: PacketChannel,
    gcm: AesGcm256,
    state: Mutex<ProtectorState>,
}

impl PacketProtector {
    /// Derive the direction-scoped key and build a fresh protector.
    pub fn new(
        shared_secret: &[u8; 32],
        session_id: &[u8; 16],
        key_epoch: u32,
        direction: PacketDirection,
        channel: PacketChannel,
        stream_id: u32,
    ) -> Self {
        let mut info = Vec::with_capacity(HKDF_LABEL.len() + 27);
        info.extend_from_slice(HKDF_LABEL);
        info.push(1);
        info.extend_from_slice(session_id);
        info.extend_from_slice(&key_epoch.to_be_bytes());
        info.push(direction.as_u8());
        info.push(channel.as_u8());
        info.extend_from_slice(&stream_id.to_be_bytes());

        let mut key = [0u8; 32];
        hkdf(session_id, shared_secret, &info, &mut key);
        let gcm = AesGcm256::new(&key);
        key.fill(0);

        Self {
            session_id: *session_id,
            key_epoch,
            stream_id,
            channel,
            gcm,
            state: Mutex::new(ProtectorState {
                replay: ReplayWindow::new(),
                next_sequence: 0,
                exhausted: false,
            }),
        }
    }

    pub fn channel(&self) -> PacketChannel {
        self.channel
    }

    pub fn stream_id(&self) -> u32 {
        self.stream_id
    }

    pub fn key_epoch(&self) -> u32 {
        self.key_epoch
    }

    /// Encrypt one plaintext into a complete datagram.
    pub fn encode(&self, plaintext: &[u8], ack_only: bool) -> Result<Vec<u8>, ProtectError> {
        if plaintext.len() > MAX_PAYLOAD_LENGTH {
            return Err(ProtectError::TooLarge);
        }

        let mut state = self.state.lock().expect("protector mutex");
        if state.exhausted {
            return Err(ProtectError::SequenceExhausted);
        }

        let sequence = state.next_sequence;
        // Reserve the sequence before any crypto so a failure can never reuse a nonce.
        if state.next_sequence == u64::MAX {
            state.exhausted = true;
        } else {
            state.next_sequence += 1;
        }

        let header = Header {
            channel: self.channel,
            flags: if ack_only { 1 } else { 0 },
            session_id: self.session_id,
            key_epoch: self.key_epoch,
            stream_id: self.stream_id,
            packet_sequence: sequence,
            payload_length: plaintext.len() as u16,
            tag: [0u8; TAG],
        };

        let mut datagram = vec![0u8; HEADER_LENGTH + plaintext.len()];
        header
            .write(&mut datagram)
            .map_err(|_| ProtectError::Malformed)?;

        let mut nonce = [0u8; NONCE];
        nonce[0..4].copy_from_slice(&self.key_epoch.to_be_bytes());
        nonce[4..12].copy_from_slice(&sequence.to_be_bytes());

        let (prefix, body) = datagram.split_at_mut(HEADER_LENGTH);
        let mut tag = [0u8; TAG];
        self.gcm
            .seal(&nonce, &prefix[..48], plaintext, body, &mut tag);
        prefix[48..64].copy_from_slice(&tag);
        Ok(datagram)
    }

    /// Verify and decrypt one datagram. The replay window advances only on success.
    pub fn decode(&self, datagram: &[u8]) -> Result<Vec<u8>, ProtectError> {
        let header = Header::parse(datagram).map_err(|_| ProtectError::Malformed)?;
        let mut state = self.state.lock().expect("protector mutex");

        if header.session_id != self.session_id
            || header.key_epoch != self.key_epoch
            || header.stream_id != self.stream_id
            || header.channel != self.channel
        {
            return Err(ProtectError::WrongContext);
        }

        if !state.replay.can_accept(header.packet_sequence) {
            return Err(ProtectError::Replay);
        }

        let mut nonce = [0u8; NONCE];
        nonce[0..4].copy_from_slice(&header.key_epoch.to_be_bytes());
        nonce[4..12].copy_from_slice(&header.packet_sequence.to_be_bytes());

        let mut plaintext = vec![0u8; header.payload_length as usize];
        if !self.gcm.open(
            &nonce,
            &datagram[..48],
            &datagram[HEADER_LENGTH..],
            &header.tag,
            &mut plaintext,
        ) {
            plaintext.fill(0);
            return Err(ProtectError::Authentication);
        }

        state.replay.commit(header.packet_sequence);
        Ok(plaintext)
    }

    /// Sequence number the next `encode` will use, for diagnostics.
    pub fn next_sequence(&self) -> u64 {
        self.state.lock().expect("protector mutex").next_sequence
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hex(text: &str) -> Vec<u8> {
        (0..text.len())
            .step_by(2)
            .map(|index| u8::from_str_radix(&text[index..index + 2], 16).expect("hex"))
            .collect()
    }

    fn to_hex(bytes: &[u8]) -> String {
        use std::fmt::Write;
        let mut out = String::with_capacity(bytes.len() * 2);
        for byte in bytes {
            let _ = write!(out, "{byte:02x}");
        }
        out
    }

    fn secret() -> [u8; 32] {
        let bytes = hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        bytes.as_slice().try_into().expect("32 bytes")
    }

    fn session_id() -> [u8; 16] {
        let bytes = hex("000102030405060708090a0b0c0d0e0f");
        bytes.as_slice().try_into().expect("16 bytes")
    }

    /// The authoritative known-answer vector from `docs/packet-vector.md`, produced and
    /// verified by the C# Core implementation.
    #[test]
    fn known_answer_vector_matches_packet_vector_md() {
        let protector = PacketProtector::new(
            &secret(),
            &session_id(),
            1,
            PacketDirection::HostToViewer,
            PacketChannel::Video,
            0,
        );

        let plaintext = hex("010203");
        let datagram = protector.encode(&plaintext, false).expect("encode");

        let expected = "5343543101010000000102030405060708090A0B0C0D0E0F0000000100000000000000000000000000030040000000000A92411A02B67657D95A187A5A25A623C8226B";
        assert_eq!(
            to_hex(&datagram),
            expected.to_lowercase(),
            "full datagram must match docs/packet-vector.md byte for byte"
        );

        // Spot-check the documented fields independently of the hex dump.
        assert_eq!(&datagram[0..4], b"SCT1".as_slice());
        assert_eq!(datagram[4], 1);
        assert_eq!(datagram[5], 1, "video channel");
        assert_eq!(u16::from_be_bytes([datagram[6], datagram[7]]), 0);
        assert_eq!(datagram[8..24], session_id()[..]);
        assert_eq!(read_u32(&datagram, 24), 1, "epoch");
        assert_eq!(read_u32(&datagram, 28), 0, "stream id");
        assert_eq!(read_u64(&datagram, 32), 0, "first packet sequence");
        assert_eq!(u16::from_be_bytes([datagram[40], datagram[41]]), 3);
        assert_eq!(u16::from_be_bytes([datagram[42], datagram[43]]), 64);
        assert_eq!(read_u32(&datagram, 44), 0, "reserved");
        assert_eq!(
            to_hex(&datagram[48..64]),
            "0a92411a02b67657d95a187a5a25a623",
            "tag"
        );
        assert_eq!(to_hex(&datagram[64..]), "c8226b", "ciphertext");
    }

    #[test]
    fn encode_then_decode_roundtrip_advances_sequence() {
        let send = PacketProtector::new(
            &secret(),
            &session_id(),
            1,
            PacketDirection::HostToViewer,
            PacketChannel::Video,
            1,
        );
        let receive = PacketProtector::new(
            &secret(),
            &session_id(),
            1,
            PacketDirection::HostToViewer,
            PacketChannel::Video,
            1,
        );

        for sequence in 0..8u64 {
            assert_eq!(send.next_sequence(), sequence);
            let plaintext = format!("payload-{sequence}").into_bytes();
            let datagram = send.encode(&plaintext, false).expect("encode");
            assert_eq!(read_u64(&datagram, 32), sequence);
            assert_eq!(receive.decode(&datagram).expect("decode"), plaintext);
        }
    }

    #[test]
    fn every_direction_channel_stream_and_epoch_is_isolated() {
        let base = PacketProtector::new(
            &secret(),
            &session_id(),
            1,
            PacketDirection::HostToViewer,
            PacketChannel::Video,
            1,
        );
        let datagram = base.encode(b"isolated", false).expect("encode");

        // The direction is not on the wire: a peer using the wrong direction derives a
        // different key, so the datagram fails authentication instead of context matching.
        let mirrored = PacketProtector::new(
            &secret(),
            &session_id(),
            1,
            PacketDirection::ViewerToHost,
            PacketChannel::Video,
            1,
        );
        assert_eq!(
            mirrored.decode(&datagram),
            Err(ProtectError::Authentication)
        );

        for (channel, stream, epoch) in [
            (PacketChannel::ReliableControl, 1u32, 1u32),
            (PacketChannel::Video, 2, 1),
            (PacketChannel::Video, 1, 2),
        ] {
            let other = PacketProtector::new(
                &secret(),
                &session_id(),
                epoch,
                PacketDirection::HostToViewer,
                channel,
                stream,
            );
            assert_eq!(
                other.decode(&datagram),
                Err(ProtectError::WrongContext),
                "channel {channel:?} stream {stream} epoch {epoch} must not decrypt"
            );
        }
    }

    #[test]
    fn replay_is_rejected_and_does_not_poison_the_window() {
        let send = PacketProtector::new(
            &secret(),
            &session_id(),
            1,
            PacketDirection::HostToViewer,
            PacketChannel::Video,
            1,
        );
        let receive = PacketProtector::new(
            &secret(),
            &session_id(),
            1,
            PacketDirection::HostToViewer,
            PacketChannel::Video,
            1,
        );

        let first = send.encode(b"one", false).expect("encode");
        let second = send.encode(b"two", false).expect("encode");
        assert_eq!(receive.decode(&first).expect("decode"), b"one".to_vec());
        assert_eq!(receive.decode(&second).expect("decode"), b"two".to_vec());
        assert_eq!(receive.decode(&first), Err(ProtectError::Replay));
        assert_eq!(receive.decode(&second), Err(ProtectError::Replay));
    }

    #[test]
    fn forged_tag_is_rejected_without_committing_the_sequence() {
        let send = PacketProtector::new(
            &secret(),
            &session_id(),
            1,
            PacketDirection::HostToViewer,
            PacketChannel::Video,
            1,
        );
        let receive = PacketProtector::new(
            &secret(),
            &session_id(),
            1,
            PacketDirection::HostToViewer,
            PacketChannel::Video,
            1,
        );

        let datagram = send.encode(b"authentic", false).expect("encode");
        let mut forged = datagram.clone();
        forged[48] ^= 0xff;
        assert_eq!(receive.decode(&forged), Err(ProtectError::Authentication));

        // The sequence must not have been consumed by the failed authentication.
        assert_eq!(
            receive.decode(&datagram).expect("decode"),
            b"authentic".to_vec()
        );
    }

    #[test]
    fn replay_window_boundary_matches_dotnet_semantics() {
        let mut window = ReplayWindow::new();
        assert!(window.can_accept(0));
        window.commit(0);
        assert!(!window.can_accept(0));
        assert!(window.can_accept(1));
        window.commit(1);

        assert!(window.can_accept(5000));
        window.commit(5000);
        assert_eq!(window.highest(), 5000);

        // 1023 packets behind the highest is inside the window; 1024 is not.
        assert!(window.can_accept(5000 - 1023));
        assert!(!window.can_accept(5000 - 1024));

        window.commit(9000);
        assert!(!window.can_accept(5000), "a jump clears the bitmap");
        assert!(
            !window.can_accept(9000),
            "the committed sequence is rejected"
        );
        assert!(window.can_accept(9001));
    }

    #[test]
    fn rejects_structurally_invalid_datagrams() {
        let protector = PacketProtector::new(
            &secret(),
            &session_id(),
            1,
            PacketDirection::HostToViewer,
            PacketChannel::Video,
            1,
        );
        let datagram = protector.encode(b"x", false).expect("encode");

        assert_eq!(
            protector.decode(&datagram[..63]),
            Err(ProtectError::Malformed)
        );

        let mut bad_version = datagram.clone();
        bad_version[4] = 2;
        assert_eq!(protector.decode(&bad_version), Err(ProtectError::Malformed));

        let mut bad_reserved = datagram.clone();
        bad_reserved[44] = 1;
        assert_eq!(
            protector.decode(&bad_reserved),
            Err(ProtectError::Malformed)
        );

        let mut bad_length = datagram.clone();
        bad_length[40..42].copy_from_slice(&9u16.to_be_bytes());
        assert_eq!(protector.decode(&bad_length), Err(ProtectError::Malformed));

        let mut oversized = vec![0u8; MAX_DATAGRAM_LENGTH + 1];
        oversized[..4].copy_from_slice(&MAGIC);
        assert_eq!(protector.decode(&oversized), Err(ProtectError::Malformed));
    }

    #[test]
    fn header_write_and_parse_roundtrip() {
        let header = Header {
            channel: PacketChannel::Probe,
            flags: 1,
            session_id: session_id(),
            key_epoch: 0x0102_0304,
            stream_id: 0x0506_0708,
            packet_sequence: 0x0102_0304_0506_0708,
            payload_length: 32,
            tag: [0u8; TAG],
        };
        let mut buffer = vec![0u8; HEADER_LENGTH + 32];
        header.write(&mut buffer).expect("write");
        let parsed = Header::parse(&buffer).expect("parse");
        assert_eq!(parsed.channel, PacketChannel::Probe);
        assert_eq!(parsed.flags, 1);
        assert_eq!(parsed.session_id, session_id());
        assert_eq!(parsed.key_epoch, 0x0102_0304);
        assert_eq!(parsed.stream_id, 0x0506_0708);
        assert_eq!(parsed.packet_sequence, 0x0102_0304_0506_0708);
        assert_eq!(parsed.payload_length, 32);
        assert!(parsed.is_ack_only());
    }

    #[test]
    fn empty_and_maximum_payload_are_accepted() {
        let protector = PacketProtector::new(
            &secret(),
            &session_id(),
            1,
            PacketDirection::HostToViewer,
            PacketChannel::Video,
            1,
        );
        let receive = PacketProtector::new(
            &secret(),
            &session_id(),
            1,
            PacketDirection::HostToViewer,
            PacketChannel::Video,
            1,
        );

        let empty = protector.encode(&[], true).expect("empty");
        assert_eq!(empty.len(), HEADER_LENGTH);
        assert_eq!(receive.decode(&empty).expect("decode"), Vec::<u8>::new());

        let maximum = vec![0xa5u8; MAX_PAYLOAD_LENGTH];
        let datagram = protector.encode(&maximum, false).expect("max");
        assert_eq!(datagram.len(), MAX_DATAGRAM_LENGTH);
        assert_eq!(receive.decode(&datagram).expect("decode"), maximum);

        assert_eq!(
            protector.encode(&vec![0u8; MAX_PAYLOAD_LENGTH + 1], false),
            Err(ProtectError::TooLarge)
        );
    }
}
