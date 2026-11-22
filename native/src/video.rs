//! Video fragmentation and bounded reassembly.
//!
//! 32-byte fragment prefix (identical to `VideoFraming.cs`):
//!
//! | offset | length | field |
//! |---:|---:|---|
//! | 0 | 8 | frameId |
//! | 8 | 8 | captureTimestampUs (sender monotonic clock) |
//! | 16 | 4 | configId |
//! | 20 | 2 | fragmentIndex |
//! | 22 | 2 | fragmentCount |
//! | 24 | 4 | frameLength |
//! | 28 | 1 | flags: 1 = key, 2 = configuration, 4 = discontinuity |
//! | 29 | 1 | codec: 1 = H.264, 2 = AV1 |
//! | 30 | 2 | reserved |
//!
//! A partial frame is never handed to a decoder. Video tolerates loss by dropping the
//! frame and requesting a new key frame, never by retransmitting fragments.

use std::collections::BTreeMap;

use crate::packet::{HEADER_LENGTH, MAX_DATAGRAM_LENGTH};

/// Fragment prefix length in bytes.
pub const FRAGMENT_HEADER_SIZE: usize = 32;
/// Largest fragment payload that fits one protected datagram.
pub const MAX_FRAGMENT_PAYLOAD: usize = MAX_DATAGRAM_LENGTH - HEADER_LENGTH - FRAGMENT_HEADER_SIZE;
/// Maximum concurrently buffered partial frames.
pub const MAX_CONCURRENT_FRAMES: usize = 4;
/// Maximum reassembled frame size (8 MiB).
pub const MAX_FRAME_BYTES: usize = 8 * 1024 * 1024;
/// Maximum fragments in one frame.
pub const MAX_FRAGMENTS_PER_FRAME: usize = 8192;
/// Default time a partial frame may live before it is discarded.
pub const DEFAULT_FRAME_LIFETIME_MICROS: i64 = 500_000;

/// Codec identifier carried per fragment.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum VideoCodec {
    Unknown = 0,
    H264 = 1,
    Av1 = 2,
}

impl VideoCodec {
    pub fn from_u8(value: u8) -> Self {
        match value {
            1 => Self::H264,
            2 => Self::Av1,
            _ => Self::Unknown,
        }
    }
}

/// Frame flags bit field.
pub mod flags {
    /// Key frame (IDR for H.264).
    pub const KEY_FRAME: u8 = 1;
    /// Configuration / parameter-set change.
    pub const CONFIGURATION: u8 = 2;
    /// Reference chain break; the decoder must reset.
    pub const DISCONTINUITY: u8 = 4;
    /// No flags set.
    pub const NONE_FLAGS: u8 = 0;
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct VideoFragmentHeader {
    pub frame_id: u64,
    pub capture_timestamp_micros: u64,
    pub config_id: u32,
    pub fragment_index: u16,
    pub fragment_count: u16,
    pub frame_length: u32,
    pub flags: u8,
    pub codec: u8,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VideoError {
    /// Fragment header is shorter than 32 bytes.
    Truncated,
    /// Fragment index is outside the declared count.
    InvalidIndex,
    /// Declared fragment count is zero or above the 8192 ceiling.
    InvalidCount,
    /// Declared frame length is zero or above the 8 MiB ceiling.
    InvalidFrameLength,
    /// The fragment payload exceeds one datagram.
    FragmentTooLarge,
    /// Destination buffer is too small.
    DestinationTooSmall,
    /// Empty frame.
    EmptyFrame,
}

impl VideoFragmentHeader {
    pub fn write(&self, destination: &mut [u8]) -> Result<(), VideoError> {
        if destination.len() < FRAGMENT_HEADER_SIZE {
            return Err(VideoError::DestinationTooSmall);
        }

        destination[0..8].copy_from_slice(&self.frame_id.to_be_bytes());
        destination[8..16].copy_from_slice(&self.capture_timestamp_micros.to_be_bytes());
        destination[16..20].copy_from_slice(&self.config_id.to_be_bytes());
        destination[20..22].copy_from_slice(&self.fragment_index.to_be_bytes());
        destination[22..24].copy_from_slice(&self.fragment_count.to_be_bytes());
        destination[24..28].copy_from_slice(&self.frame_length.to_be_bytes());
        destination[28] = self.flags;
        destination[29] = self.codec;
        destination[30] = 0;
        destination[31] = 0;
        Ok(())
    }

    pub fn parse(source: &[u8]) -> Result<Self, VideoError> {
        if source.len() < FRAGMENT_HEADER_SIZE {
            return Err(VideoError::Truncated);
        }

        Ok(Self {
            frame_id: u64::from_be_bytes(source[0..8].try_into().expect("bounded field")),
            capture_timestamp_micros: u64::from_be_bytes(
                source[8..16].try_into().expect("bounded field"),
            ),
            config_id: u32::from_be_bytes(source[16..20].try_into().expect("bounded field")),
            fragment_index: u16::from_be_bytes([source[20], source[21]]),
            fragment_count: u16::from_be_bytes([source[22], source[23]]),
            frame_length: u32::from_be_bytes(source[24..28].try_into().expect("bounded field")),
            flags: source[28],
            codec: source[29],
        })
    }

    /// Structural sanity checks shared by the fragmenter and the assembler.
    pub fn validate(&self) -> Result<(), VideoError> {
        if self.fragment_count == 0 || self.fragment_count as usize > MAX_FRAGMENTS_PER_FRAME {
            return Err(VideoError::InvalidCount);
        }
        if self.fragment_index >= self.fragment_count {
            return Err(VideoError::InvalidIndex);
        }
        if self.frame_length == 0 || self.frame_length as usize > MAX_FRAME_BYTES {
            return Err(VideoError::InvalidFrameLength);
        }
        Ok(())
    }

    pub fn is_key_frame(&self) -> bool {
        self.flags & flags::KEY_FRAME != 0
    }

    pub fn is_configuration(&self) -> bool {
        self.flags & flags::CONFIGURATION != 0
    }

    pub fn is_discontinuity(&self) -> bool {
        self.flags & flags::DISCONTINUITY != 0
    }

    pub fn codec(&self) -> VideoCodec {
        VideoCodec::from_u8(self.codec)
    }
}

/// One fully reassembled frame.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AssembledVideoFrame {
    pub frame_id: u64,
    pub capture_timestamp_micros: u64,
    pub config_id: u32,
    pub flags: u8,
    pub codec: u8,
    pub data: Vec<u8>,
}

impl AssembledVideoFrame {
    pub fn is_key_frame(&self) -> bool {
        self.flags & flags::KEY_FRAME != 0
    }

    pub fn codec(&self) -> VideoCodec {
        VideoCodec::from_u8(self.codec)
    }
}

struct PartialFrame {
    header: VideoFragmentHeader,
    created_at_micros: i64,
    fragments: Vec<Option<Vec<u8>>>,
    received: usize,
}

impl PartialFrame {
    fn new(header: VideoFragmentHeader, created_at_micros: i64) -> Self {
        Self {
            header,
            created_at_micros,
            fragments: vec![None; header.fragment_count as usize],
            received: 0,
        }
    }

    fn received_bytes(&self) -> usize {
        self.fragments
            .iter()
            .map(|fragment| fragment.as_ref().map_or(0, Vec::len))
            .sum()
    }
}

/// Bounded reassembler: at most four concurrent frames, 8 MiB each, 8192 fragments.
pub struct VideoAssembler {
    partial: BTreeMap<u64, PartialFrame>,
    buffered_bytes: usize,
    dropped_fragments: u64,
    dropped_frames: u64,
    frame_lifetime_micros: i64,
}

impl Default for VideoAssembler {
    fn default() -> Self {
        Self::new(DEFAULT_FRAME_LIFETIME_MICROS)
    }
}

impl VideoAssembler {
    pub fn new(frame_lifetime_micros: i64) -> Self {
        Self {
            partial: BTreeMap::new(),
            buffered_bytes: 0,
            dropped_fragments: 0,
            dropped_frames: 0,
            frame_lifetime_micros,
        }
    }

    pub fn buffered_bytes(&self) -> usize {
        self.buffered_bytes
    }

    pub fn partial_frame_count(&self) -> usize {
        self.partial.len()
    }

    pub fn dropped_fragments(&self) -> u64 {
        self.dropped_fragments
    }

    pub fn dropped_frames(&self) -> u64 {
        self.dropped_frames
    }

    /// Feed one authenticated fragment. Returns a complete frame when this fragment
    /// finished it, otherwise `None`. Expired partial frames are discarded first so
    /// memory stays bounded even when the peer stops sending.
    pub fn push(&mut self, plaintext: &[u8], now_micros: i64) -> Option<AssembledVideoFrame> {
        // Sweep before parsing so a malformed datagram still ages out stale partials.
        self.sweep_expired(now_micros);

        let Ok(header) = VideoFragmentHeader::parse(plaintext) else {
            self.dropped_fragments += 1;
            return None;
        };

        if header.validate().is_err() {
            self.dropped_fragments += 1;
            return None;
        }

        let payload = &plaintext[FRAGMENT_HEADER_SIZE..];
        if payload.len() > MAX_FRAGMENT_PAYLOAD {
            self.dropped_fragments += 1;
            return None;
        }

        let conflict = self.partial.get(&header.frame_id).is_some_and(|existing| {
            existing.header.fragment_count != header.fragment_count
                || existing.header.frame_length != header.frame_length
                || existing.header.codec != header.codec
        });
        if conflict {
            // Conflicting metadata for the same frame id: treat the whole frame as lost.
            self.release_frame(header.frame_id);
            self.dropped_frames += 1;
            self.dropped_fragments += 1;
            return None;
        }

        if !self.partial.contains_key(&header.frame_id) {
            if self.partial.len() >= MAX_CONCURRENT_FRAMES {
                // Evict the oldest partial frame rather than growing without bound.
                self.evict_oldest();
            }
            self.partial
                .insert(header.frame_id, PartialFrame::new(header, now_micros));
        }

        let index = header.fragment_index as usize;
        if self
            .partial
            .get(&header.frame_id)
            .is_some_and(|frame| frame.fragments[index].is_some())
        {
            // Duplicate fragment: it adds no information.
            return None;
        }

        if self.buffered_bytes + payload.len() > MAX_CONCURRENT_FRAMES * MAX_FRAME_BYTES {
            self.release_frame(header.frame_id);
            self.dropped_frames += 1;
            return None;
        }

        let (received, expected) = {
            let frame = self
                .partial
                .get_mut(&header.frame_id)
                .expect("the partial frame exists at this point");
            frame.fragments[index] = Some(payload.to_vec());
            frame.received += 1;
            (frame.received, header.fragment_count as usize)
        };
        self.buffered_bytes += payload.len();

        if received < expected {
            return None;
        }

        let frame = self.partial.remove(&header.frame_id)?;
        self.buffered_bytes = self.buffered_bytes.saturating_sub(frame.received_bytes());
        Some(complete(frame))
    }

    fn sweep_expired(&mut self, now_micros: i64) {
        if self.partial.is_empty() {
            return;
        }

        let expired: Vec<u64> = self
            .partial
            .iter()
            .filter(|(_, frame)| now_micros - frame.created_at_micros >= self.frame_lifetime_micros)
            .map(|(frame_id, _)| *frame_id)
            .collect();

        for frame_id in expired {
            self.release_frame(frame_id);
            self.dropped_frames += 1;
        }
    }

    fn evict_oldest(&mut self) {
        let oldest = self
            .partial
            .iter()
            .min_by_key(|(_, frame)| frame.created_at_micros)
            .map(|(frame_id, _)| *frame_id);

        if let Some(frame_id) = oldest {
            self.release_frame(frame_id);
            self.dropped_frames += 1;
        }
    }

    fn release_frame(&mut self, frame_id: u64) {
        if let Some(frame) = self.partial.remove(&frame_id) {
            self.buffered_bytes = self.buffered_bytes.saturating_sub(frame.received_bytes());
        }
    }
}

fn complete(frame: PartialFrame) -> AssembledVideoFrame {
    let total: usize = frame.received_bytes();
    let mut data = Vec::with_capacity(total);
    for fragment in frame.fragments.iter().flatten() {
        data.extend_from_slice(fragment);
    }

    AssembledVideoFrame {
        frame_id: frame.header.frame_id,
        capture_timestamp_micros: frame.header.capture_timestamp_micros,
        config_id: frame.header.config_id,
        flags: frame.header.flags,
        codec: frame.header.codec,
        data,
    }
}

/// Stateless splitter. The caller owns the frame id, configuration id and flags, so a new
/// key frame or configuration change is always explicit.
pub struct VideoFragmenter;

impl VideoFragmenter {
    /// Number of fragments needed for `frame_length` bytes.
    pub fn fragment_count_for(frame_length: usize) -> Result<u16, VideoError> {
        if frame_length == 0 {
            return Err(VideoError::EmptyFrame);
        }
        if frame_length > MAX_FRAME_BYTES {
            return Err(VideoError::InvalidFrameLength);
        }

        let count = frame_length.div_ceil(MAX_FRAGMENT_PAYLOAD);
        if count > MAX_FRAGMENTS_PER_FRAME {
            return Err(VideoError::InvalidCount);
        }
        Ok(count as u16)
    }

    /// Write the plaintext of fragment `index` into `destination`; returns its length.
    #[allow(clippy::too_many_arguments)]
    pub fn write_fragment(
        frame_data: &[u8],
        frame_id: u64,
        capture_timestamp_micros: u64,
        config_id: u32,
        frame_flags: u8,
        codec: u8,
        index: u16,
        destination: &mut [u8],
    ) -> Result<usize, VideoError> {
        let count = Self::fragment_count_for(frame_data.len())?;
        if index >= count {
            return Err(VideoError::InvalidIndex);
        }

        let start = index as usize * MAX_FRAGMENT_PAYLOAD;
        let length = MAX_FRAGMENT_PAYLOAD.min(frame_data.len() - start);
        if destination.len() < FRAGMENT_HEADER_SIZE + length {
            return Err(VideoError::DestinationTooSmall);
        }

        let header = VideoFragmentHeader {
            frame_id,
            capture_timestamp_micros,
            config_id,
            fragment_index: index,
            fragment_count: count,
            frame_length: frame_data.len() as u32,
            flags: frame_flags,
            codec,
        };
        header.write(destination)?;
        destination[FRAGMENT_HEADER_SIZE..FRAGMENT_HEADER_SIZE + length]
            .copy_from_slice(&frame_data[start..start + length]);
        Ok(FRAGMENT_HEADER_SIZE + length)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample_frame(length: usize) -> Vec<u8> {
        (0..length).map(|index| (index % 251) as u8).collect()
    }

    fn split(frame_data: &[u8], frame_id: u64) -> Vec<Vec<u8>> {
        let count = VideoFragmenter::fragment_count_for(frame_data.len()).expect("count");
        let mut buffer = vec![0u8; FRAGMENT_HEADER_SIZE + MAX_FRAGMENT_PAYLOAD];
        let mut out = Vec::new();
        for index in 0..count {
            let length = VideoFragmenter::write_fragment(
                frame_data,
                frame_id,
                1234,
                7,
                flags::KEY_FRAME,
                VideoCodec::H264 as u8,
                index,
                &mut buffer,
            )
            .expect("fragment");
            out.push(buffer[..length].to_vec());
        }
        out
    }

    #[test]
    fn fragment_count_math_matches_the_datagram_budget() {
        assert_eq!(MAX_FRAGMENT_PAYLOAD, 1200 - 64 - 32);
        assert_eq!(VideoFragmenter::fragment_count_for(1), Ok(1));
        assert_eq!(
            VideoFragmenter::fragment_count_for(MAX_FRAGMENT_PAYLOAD),
            Ok(1)
        );
        assert_eq!(
            VideoFragmenter::fragment_count_for(MAX_FRAGMENT_PAYLOAD + 1),
            Ok(2)
        );
        assert_eq!(VideoFragmenter::fragment_count_for(5000), Ok(5));
        assert_eq!(
            VideoFragmenter::fragment_count_for(0),
            Err(VideoError::EmptyFrame)
        );
        assert_eq!(
            VideoFragmenter::fragment_count_for(MAX_FRAME_BYTES + 1),
            Err(VideoError::InvalidFrameLength)
        );
    }

    #[test]
    fn split_and_reassemble_roundtrip_is_byte_identical() {
        let frame_data = sample_frame(5000);
        let fragments = split(&frame_data, 42);
        assert_eq!(fragments.len(), 5);

        let mut assembler = VideoAssembler::default();
        let mut assembled = None;
        for (index, fragment) in fragments.iter().enumerate() {
            let result = assembler.push(fragment, 1_000);
            if index + 1 < fragments.len() {
                assert!(result.is_none(), "incomplete frame must not be emitted");
            } else {
                assembled = result;
            }
        }

        let assembled = assembled.expect("frame must complete");
        assert_eq!(assembled.data, frame_data);
        assert_eq!(assembled.frame_id, 42);
        assert_eq!(assembled.capture_timestamp_micros, 1234);
        assert_eq!(assembled.config_id, 7);
        assert!(assembled.is_key_frame());
        assert_eq!(assembled.codec(), VideoCodec::H264);
        assert_eq!(assembler.partial_frame_count(), 0);
        assert_eq!(assembler.buffered_bytes(), 0);
    }

    #[test]
    fn out_of_order_fragments_still_reassemble_in_index_order() {
        let frame_data = sample_frame(4000);
        let mut fragments = split(&frame_data, 9);
        fragments.reverse();

        let mut assembler = VideoAssembler::default();
        let mut assembled = None;
        for fragment in &fragments {
            assembled = assembler.push(fragment, 0);
        }
        assert_eq!(assembled.expect("complete").data, frame_data);
    }

    #[test]
    fn a_missing_fragment_drops_the_whole_frame() {
        let frame_data = sample_frame(3000);
        let fragments = split(&frame_data, 9);

        let mut assembler = VideoAssembler::default();
        let mut assembled = None;
        for (index, fragment) in fragments.iter().enumerate() {
            if index == 1 {
                continue; // lose one fragment
            }
            assembled = assembler.push(fragment, 0);
        }

        assert!(
            assembled.is_none(),
            "an incomplete frame must never reach a decoder"
        );
        assert_eq!(
            assembler.partial_frame_count(),
            1,
            "the partial stays bounded"
        );
        assert!(assembler.buffered_bytes() > 0);
    }

    #[test]
    fn duplicate_fragment_adds_nothing() {
        let frame_data = sample_frame(2500);
        let fragments = split(&frame_data, 3);

        let mut assembler = VideoAssembler::default();
        assert!(assembler.push(&fragments[0], 0).is_none());
        let buffered = assembler.buffered_bytes();
        assert!(assembler.push(&fragments[0], 0).is_none());
        assert_eq!(assembler.buffered_bytes(), buffered, "no double counting");
    }

    #[test]
    fn conflicting_metadata_discards_the_frame() {
        let frame_data = sample_frame(2500);
        let fragments = split(&frame_data, 5);

        let mut assembler = VideoAssembler::default();
        assert!(assembler.push(&fragments[0], 0).is_none());

        let mut conflicting = fragments[1].clone();
        conflicting[22..24].copy_from_slice(&99u16.to_be_bytes());
        assert!(assembler.push(&conflicting, 0).is_none());
        assert_eq!(assembler.partial_frame_count(), 0);
        assert_eq!(assembler.dropped_frames(), 1);
        assert_eq!(assembler.buffered_bytes(), 0);
    }

    #[test]
    fn stale_partial_frame_expires_on_the_next_push() {
        let frame_data = sample_frame(5000);
        let fragments = split(&frame_data, 5);

        let mut assembler = VideoAssembler::new(100_000);
        assert!(assembler.push(&fragments[0], 0).is_none());
        assert_eq!(assembler.partial_frame_count(), 1);

        // Garbage input must still age out the stale partial.
        assert!(assembler.push(&[0, 0, 0, 0], 200_000).is_none());
        assert_eq!(assembler.partial_frame_count(), 0);
        assert_eq!(assembler.dropped_frames(), 1);
        assert_eq!(assembler.buffered_bytes(), 0);
        assert!(assembler.dropped_fragments() >= 1);
    }

    #[test]
    fn concurrent_frame_limit_evicts_the_oldest() {
        let mut assembler = VideoAssembler::default();
        let mut buffer = vec![0u8; FRAGMENT_HEADER_SIZE + MAX_FRAGMENT_PAYLOAD];

        // Four concurrent frames, each with two fragments, then a fifth.
        for frame_id in 0..5u64 {
            let frame_data = sample_frame(2000);
            let length = VideoFragmenter::write_fragment(
                &frame_data,
                frame_id,
                0,
                1,
                flags::NONE_FLAGS,
                VideoCodec::H264 as u8,
                0,
                &mut buffer,
            )
            .expect("fragment");
            assert!(assembler.push(&buffer[..length], frame_id as i64).is_none());
        }

        assert_eq!(assembler.partial_frame_count(), MAX_CONCURRENT_FRAMES);
        assert!(assembler.dropped_frames() >= 1);
    }

    #[test]
    fn oversized_and_malformed_headers_are_rejected() {
        let mut assembler = VideoAssembler::default();

        assert!(assembler.push(&[], 0).is_none());
        assert!(assembler.push(&[0u8; 31], 0).is_none());
        assert_eq!(assembler.dropped_fragments(), 2);

        let mut header = [0u8; FRAGMENT_HEADER_SIZE];
        header[22..24].copy_from_slice(&0u16.to_be_bytes()); // count = 0
        assert!(assembler.push(&header, 0).is_none());

        let mut header = [0u8; FRAGMENT_HEADER_SIZE];
        header[22..24].copy_from_slice(&(MAX_FRAGMENTS_PER_FRAME as u16 + 1).to_be_bytes());
        assert!(assembler.push(&header, 0).is_none());

        let mut header = [0u8; FRAGMENT_HEADER_SIZE];
        header[22..24].copy_from_slice(&2u16.to_be_bytes());
        header[20..22].copy_from_slice(&2u16.to_be_bytes()); // index >= count
        assert!(assembler.push(&header, 0).is_none());

        let mut header = [0u8; FRAGMENT_HEADER_SIZE];
        header[22..24].copy_from_slice(&1u16.to_be_bytes());
        header[24..28].copy_from_slice(&(MAX_FRAME_BYTES as u32 + 1).to_be_bytes());
        assert!(assembler.push(&header, 0).is_none());

        let mut header = [0u8; FRAGMENT_HEADER_SIZE];
        header[22..24].copy_from_slice(&1u16.to_be_bytes());
        header[24..28].copy_from_slice(&4u32.to_be_bytes());
        let mut oversized = header.to_vec();
        oversized.extend(vec![0u8; MAX_FRAGMENT_PAYLOAD + 1]);
        assert!(assembler.push(&oversized, 0).is_none());

        assert_eq!(assembler.partial_frame_count(), 0);
    }

    #[test]
    fn flags_and_codec_helpers_decode_bits() {
        let header = VideoFragmentHeader {
            frame_id: 1,
            capture_timestamp_micros: 2,
            config_id: 3,
            fragment_index: 0,
            fragment_count: 1,
            frame_length: 4,
            flags: flags::KEY_FRAME | flags::CONFIGURATION | flags::DISCONTINUITY,
            codec: VideoCodec::Av1 as u8,
        };
        assert!(header.is_key_frame());
        assert!(header.is_configuration());
        assert!(header.is_discontinuity());
        assert_eq!(header.codec(), VideoCodec::Av1);

        let mut buffer = [0u8; FRAGMENT_HEADER_SIZE];
        header.write(&mut buffer).expect("write");
        assert_eq!(VideoFragmentHeader::parse(&buffer).expect("parse"), header);
    }
}
