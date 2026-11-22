//! Reliable ordered control channel: 32-byte header, cumulative plus selective ACK,
//! duplicate suppression, retransmission with Karn's rule, bounded expiry.
//!
//! Wire layout (identical to `ControlCodec.cs`):
//!
//! | offset | length | field |
//! |---:|---:|---|
//! | 0 | 1 | kind: 1 = Data, 2 = ACK |
//! | 1 | 3 | reserved, must be zero |
//! | 4 | 8 | message sequence (Data only; reused verbatim on retransmission) |
//! | 12 | 8 | ackBase |
//! | 20 | 8 | ackMask |
//! | 28 | 2 | event type |
//! | 30 | 2 | body length |
//! | 32 | N | body |
//!
//! `ackBase == u64::MAX` is the "no contiguous message yet" sentinel, so `ackBase + 1`
//! reads as message 0 and `ackMask` bit i means `ackBase + 1 + i`. A retransmission keeps
//! its message number but travels in a freshly encrypted datagram, so the AEAD nonce never
//! repeats. A duplicate Data frame executes its side effect once and is still acknowledged:
//! otherwise one lost ACK would make the sender declare failure for an event that arrived.

use std::collections::BTreeMap;
use std::sync::Mutex;

use crate::packet::{HEADER_LENGTH, MAX_DATAGRAM_LENGTH};

/// Control header length in bytes.
pub const HEADER_SIZE: usize = 32;
/// Kind byte for a Data frame.
pub const KIND_DATA: u8 = 1;
/// Kind byte for an ACK-only frame.
pub const KIND_ACK: u8 = 2;
/// Largest body that still fits one protected datagram (1200 - 64 - 32).
pub const MAX_BODY_LENGTH: usize = MAX_DATAGRAM_LENGTH - HEADER_LENGTH - HEADER_SIZE;
/// Reliable send window and receive reorder window width.
pub const WINDOW_SIZE: usize = 64;
/// Retransmission attempt ceiling before the message is declared lost.
pub const MAX_ATTEMPTS: u32 = 8;
/// Absolute per-message deadline in microseconds.
pub const MESSAGE_LIFETIME_MICROS: i64 = 2_000_000;
/// RTO used before the first RTT sample.
pub const INITIAL_RTO_MICROS: i64 = 200_000;
/// Lower RTO clamp.
pub const MIN_RTO_MICROS: i64 = 30_000;
/// Upper RTO clamp.
pub const MAX_RTO_MICROS: i64 = 1_000_000;
/// Sentinel meaning "no contiguous message received yet".
pub const SENTINEL_ACK_BASE: u64 = u64::MAX;

/// Input event / control command identifiers.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u16)]
pub enum ControlEventType {
    Key = 1,
    MouseMove = 2,
    MouseButton = 3,
    Wheel = 4,
    Touch = 5,
    ReleaseAll = 6,
    RequestKeyFrame = 7,
    SetQuality = 8,
    PathSwitch = 9,
    ControlLease = 10,
}

impl ControlEventType {
    pub fn from_u16(value: u16) -> Option<Self> {
        Some(match value {
            1 => Self::Key,
            2 => Self::MouseMove,
            3 => Self::MouseButton,
            4 => Self::Wheel,
            5 => Self::Touch,
            6 => Self::ReleaseAll,
            7 => Self::RequestKeyFrame,
            8 => Self::SetQuality,
            9 => Self::PathSwitch,
            10 => Self::ControlLease,
            _ => return None,
        })
    }

    pub const fn as_u16(self) -> u16 {
        self as u16
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ControlError {
    /// Body longer than one datagram can carry.
    BodyTooLarge,
}

/// One decoded control frame.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ControlFrame {
    pub is_ack: bool,
    pub sequence: u64,
    pub ack_base: u64,
    pub ack_mask: u64,
    pub event_type: u16,
    pub body: Vec<u8>,
}

/// Encode a Data frame.
pub fn encode_data(
    sequence: u64,
    ack_base: u64,
    ack_mask: u64,
    event_type: u16,
    body: &[u8],
) -> Result<Vec<u8>, ControlError> {
    if body.len() > MAX_BODY_LENGTH {
        return Err(ControlError::BodyTooLarge);
    }

    let mut frame = vec![0u8; HEADER_SIZE + body.len()];
    frame[0] = KIND_DATA;
    frame[4..12].copy_from_slice(&sequence.to_be_bytes());
    frame[12..20].copy_from_slice(&ack_base.to_be_bytes());
    frame[20..28].copy_from_slice(&ack_mask.to_be_bytes());
    frame[28..30].copy_from_slice(&event_type.to_be_bytes());
    frame[30..32].copy_from_slice(&(body.len() as u16).to_be_bytes());
    frame[HEADER_SIZE..].copy_from_slice(body);
    Ok(frame)
}

/// Encode an ACK-only frame (header only, no body, no event type).
pub fn encode_ack(ack_base: u64, ack_mask: u64) -> Vec<u8> {
    let mut frame = vec![0u8; HEADER_SIZE];
    frame[0] = KIND_ACK;
    frame[12..20].copy_from_slice(&ack_base.to_be_bytes());
    frame[20..28].copy_from_slice(&ack_mask.to_be_bytes());
    frame
}

/// Decode one control frame. Returns `None` for anything malformed.
pub fn decode(frame: &[u8]) -> Option<ControlFrame> {
    if frame.len() < HEADER_SIZE {
        return None;
    }

    let kind = frame[0];
    if kind != KIND_DATA && kind != KIND_ACK {
        return None;
    }

    if frame[1] != 0 || frame[2] != 0 || frame[3] != 0 {
        return None;
    }

    let body_length = u16::from_be_bytes([frame[30], frame[31]]) as usize;
    if body_length > MAX_BODY_LENGTH || frame.len() != HEADER_SIZE + body_length {
        return None;
    }

    let ack_base = u64::from_be_bytes(frame[12..20].try_into().expect("bounded field"));
    let ack_mask = u64::from_be_bytes(frame[20..28].try_into().expect("bounded field"));

    if kind == KIND_ACK {
        if body_length != 0 {
            return None;
        }
        return Some(ControlFrame {
            is_ack: true,
            sequence: 0,
            ack_base,
            ack_mask,
            event_type: 0,
            body: Vec::new(),
        });
    }

    Some(ControlFrame {
        is_ack: false,
        sequence: u64::from_be_bytes(frame[4..12].try_into().expect("bounded field")),
        ack_base,
        ack_mask,
        event_type: u16::from_be_bytes([frame[28], frame[29]]),
        body: frame[HEADER_SIZE..].to_vec(),
    })
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EnqueueError {
    /// The 64-message send window is full.
    WindowFull,
    /// Message numbers are exhausted; a rekey/new channel is required.
    SequenceExhausted,
    /// The body does not fit one datagram.
    BodyTooLarge,
}

struct PendingSend {
    event_type: u16,
    body: Vec<u8>,
    first_sent_micros: i64,
    last_sent_micros: i64,
    attempts: u32,
    retransmitted: bool,
    fast_retransmitted: bool,
}

struct ChannelState {
    pending: BTreeMap<u64, PendingSend>,
    next_sequence: u64,
    sequence_exhausted: bool,
    ack_base: u64,
    ack_mask: u64,
    out_of_order: BTreeMap<u64, (u16, Vec<u8>)>,
    srtt: f64,
    rtt_var: f64,
    rto: i64,
}

impl ChannelState {
    fn new() -> Self {
        Self {
            pending: BTreeMap::new(),
            next_sequence: 0,
            sequence_exhausted: false,
            ack_base: SENTINEL_ACK_BASE,
            ack_mask: 0,
            out_of_order: BTreeMap::new(),
            srtt: -1.0,
            rtt_var: 0.0,
            rto: INITIAL_RTO_MICROS,
        }
    }
}

type SendFrame = Box<dyn Fn(&[u8]) + Send + Sync>;
type OnDelivered = Box<dyn Fn(u16, &[u8]) + Send + Sync>;
type OnExpired = Box<dyn Fn(u16) + Send + Sync>;

/// One direction of one control component.
///
/// The caller supplies the already-encrypted outbound path (`send_frame`) and performs
/// decryption before calling [`on_frame`]. Callbacks run while the channel lock is held,
/// so they must only enqueue work and must never re-enter this channel.
pub struct ReliableControlChannel {
    send_frame: SendFrame,
    on_delivered: OnDelivered,
    on_expired: Option<OnExpired>,
    state: Mutex<ChannelState>,
}

impl ReliableControlChannel {
    pub fn new(
        send_frame: impl Fn(&[u8]) + Send + Sync + 'static,
        on_delivered: impl Fn(u16, &[u8]) + Send + Sync + 'static,
        on_expired: Option<Box<dyn Fn(u16) + Send + Sync>>,
    ) -> Self {
        Self {
            send_frame: Box::new(send_frame),
            on_delivered: Box::new(on_delivered),
            on_expired,
            state: Mutex::new(ChannelState::new()),
        }
    }

    pub fn pending_count(&self) -> usize {
        self.state.lock().expect("channel mutex").pending.len()
    }

    pub fn ack_base(&self) -> u64 {
        self.state.lock().expect("channel mutex").ack_base
    }

    pub fn ack_mask(&self) -> u64 {
        self.state.lock().expect("channel mutex").ack_mask
    }

    pub fn current_rto_micros(&self) -> i64 {
        self.state.lock().expect("channel mutex").rto
    }

    pub fn smoothed_rtt_micros(&self) -> f64 {
        self.state.lock().expect("channel mutex").srtt
    }

    pub fn buffered_out_of_order(&self) -> usize {
        self.state.lock().expect("channel mutex").out_of_order.len()
    }

    /// Queue one event and send it immediately.
    pub fn try_enqueue(
        &self,
        event_type: u16,
        body: &[u8],
        now_micros: i64,
    ) -> Result<(), EnqueueError> {
        if body.len() > MAX_BODY_LENGTH {
            return Err(EnqueueError::BodyTooLarge);
        }

        let mut state = self.state.lock().expect("channel mutex");
        if state.sequence_exhausted || state.pending.len() >= WINDOW_SIZE {
            return Err(EnqueueError::WindowFull);
        }

        let sequence = state.next_sequence;
        if sequence == u64::MAX {
            // Message numbers must never wrap: wrapping would alias an old event.
            state.sequence_exhausted = true;
            return Err(EnqueueError::SequenceExhausted);
        }
        state.next_sequence += 1;

        state.pending.insert(
            sequence,
            PendingSend {
                event_type,
                body: body.to_vec(),
                first_sent_micros: now_micros,
                last_sent_micros: now_micros,
                attempts: 0,
                retransmitted: false,
                fast_retransmitted: false,
            },
        );

        let frame = encode_data(sequence, state.ack_base, state.ack_mask, event_type, body)
            .expect("body length was validated");
        (self.send_frame)(&frame);
        Ok(())
    }

    /// Handle one already-authenticated, already-decrypted control frame.
    pub fn on_frame(&self, frame: &[u8], now_micros: i64) {
        let Some(message) = decode(frame) else {
            return;
        };

        if message.is_ack {
            self.on_ack(message.ack_base, message.ack_mask, now_micros);
        } else {
            self.on_data(&message, now_micros);
        }
    }

    /// Drive retransmission and expiry. Call from a 5-10 ms timer.
    pub fn tick(&self, now_micros: i64) {
        let mut state = self.state.lock().expect("channel mutex");
        if state.pending.is_empty() {
            return;
        }

        let mut expired: Vec<u64> = Vec::new();
        let mut retransmit: Vec<u64> = Vec::new();
        for (sequence, item) in state.pending.iter() {
            if now_micros - item.first_sent_micros >= MESSAGE_LIFETIME_MICROS
                || item.attempts >= MAX_ATTEMPTS
            {
                expired.push(*sequence);
                continue;
            }

            if now_micros - item.last_sent_micros >= backoff_for(item, state.rto) {
                retransmit.push(*sequence);
            }
        }

        let mut frames: Vec<Vec<u8>> = Vec::with_capacity(retransmit.len());
        for sequence in retransmit {
            let ack_base = state.ack_base;
            let ack_mask = state.ack_mask;
            if let Some(item) = state.pending.get_mut(&sequence) {
                item.attempts += 1;
                item.last_sent_micros = now_micros;
                item.retransmitted = true;
                frames.push(
                    encode_data(sequence, ack_base, ack_mask, item.event_type, &item.body)
                        .expect("stored body length was validated"),
                );
            }
        }

        for frame in &frames {
            (self.send_frame)(frame);
        }

        for sequence in expired {
            if let Some(failed) = state.pending.remove(&sequence) {
                if let Some(on_expired) = &self.on_expired {
                    // The caller must release held keys: an input event that never
                    // arrived cannot be assumed inert.
                    on_expired(failed.event_type);
                }
            }
        }
    }

    fn on_ack(&self, ack_base: u64, ack_mask: u64, now_micros: i64) {
        let mut state = self.state.lock().expect("channel mutex");
        apply_acknowledgement(&mut state, ack_base, ack_mask, now_micros);
        fast_retransmit(&mut state, ack_base, ack_mask, now_micros, &self.send_frame);
    }

    fn on_data(&self, message: &ControlFrame, now_micros: i64) {
        let mut state = self.state.lock().expect("channel mutex");
        apply_acknowledgement(&mut state, message.ack_base, message.ack_mask, now_micros);

        let is_new = mark_received(&mut state, message.sequence);
        if is_new {
            state
                .out_of_order
                .insert(message.sequence, (message.event_type, message.body.clone()));
            deliver_contiguous(&mut state, &self.on_delivered);
        }

        // Always acknowledge, including duplicates: the peer may only be missing our ACK.
        let ack = encode_ack(state.ack_base, state.ack_mask);
        (self.send_frame)(&ack);
    }
}

/// Per-message exponential backoff capped at four doublings, so one stuck message does
/// not flood the link while later messages keep flowing.
fn backoff_for(item: &PendingSend, rto: i64) -> i64 {
    let shifts = item.attempts.min(4);
    let value = rto.checked_mul(1i64 << shifts).unwrap_or(MAX_RTO_MICROS);
    value.min(MAX_RTO_MICROS)
}

fn apply_acknowledgement(state: &mut ChannelState, ack_base: u64, ack_mask: u64, now_micros: i64) {
    if state.pending.is_empty() {
        return;
    }

    let confirmed: Vec<u64> = state
        .pending
        .keys()
        .copied()
        .filter(|sequence| is_confirmed(ack_base, ack_mask, *sequence))
        .collect();

    for sequence in confirmed {
        remove_if_confirmed(state, sequence, now_micros);
    }
}

/// Whether this acknowledgement covers `sequence`. The sentinel `ack_base` shifts the
/// meaning of every mask bit down by one.
fn is_confirmed(ack_base: u64, ack_mask: u64, sequence: u64) -> bool {
    if ack_base == SENTINEL_ACK_BASE {
        return sequence < WINDOW_SIZE as u64 && (ack_mask & (1u64 << sequence)) != 0;
    }

    if sequence == ack_base {
        return true;
    }

    if sequence > ack_base {
        let ahead = sequence - ack_base;
        return ahead <= WINDOW_SIZE as u64 && (ack_mask & (1u64 << (ahead - 1))) != 0;
    }

    let behind = ack_base - sequence;
    behind <= WINDOW_SIZE as u64 && (ack_mask & (1u64 << (behind - 1))) != 0
}

fn remove_if_confirmed(state: &mut ChannelState, sequence: u64, now_micros: i64) {
    let Some(item) = state.pending.remove(&sequence) else {
        return;
    };

    // Only a never-retransmitted message yields an unambiguous RTT sample (Karn's rule).
    if !item.retransmitted {
        sample_rtt(state, now_micros - item.first_sent_micros);
    }
}

fn fast_retransmit(
    state: &mut ChannelState,
    ack_base: u64,
    ack_mask: u64,
    now_micros: i64,
    send_frame: &SendFrame,
) {
    if state.pending.is_empty() {
        return;
    }

    // Highest message the peer has seen. A hole below it is missing, not merely slow.
    let mut highest_seen = if ack_base == SENTINEL_ACK_BASE {
        0
    } else {
        ack_base
    };
    for bit in (0..WINDOW_SIZE).rev() {
        if (ack_mask & (1u64 << bit)) != 0 {
            let candidate = if ack_base == SENTINEL_ACK_BASE {
                bit as u64
            } else {
                ack_base.wrapping_add(1).wrapping_add(bit as u64)
            };
            if candidate > highest_seen {
                highest_seen = candidate;
            }
            break;
        }
    }

    if highest_seen == 0 {
        return;
    }

    let mut frames: Vec<Vec<u8>> = Vec::new();
    let ack_base_out = state.ack_base;
    let ack_mask_out = state.ack_mask;
    for (sequence, item) in state.pending.iter_mut() {
        if *sequence < highest_seen
            && !item.fast_retransmitted
            && !is_confirmed(ack_base, ack_mask, *sequence)
        {
            item.fast_retransmitted = true;
            item.attempts += 1;
            item.last_sent_micros = now_micros;
            item.retransmitted = true;
            frames.push(
                encode_data(
                    *sequence,
                    ack_base_out,
                    ack_mask_out,
                    item.event_type,
                    &item.body,
                )
                .expect("stored body length was validated"),
            );
        }
    }

    for frame in &frames {
        (send_frame)(frame);
    }
}

fn mark_received(state: &mut ChannelState, sequence: u64) -> bool {
    if state.ack_base == SENTINEL_ACK_BASE {
        let distance = sequence.wrapping_add(1); // overflows to zero only for u64::MAX
        if distance == 0 || distance > WINDOW_SIZE as u64 {
            return false;
        }

        let bit = 1u64 << (distance - 1);
        if (state.ack_mask & bit) != 0 {
            return false;
        }

        state.ack_mask |= bit;
        return true;
    }

    if sequence > state.ack_base {
        let distance = sequence - state.ack_base;
        if distance > WINDOW_SIZE as u64 {
            return false;
        }

        let bit = 1u64 << (distance - 1);
        if (state.ack_mask & bit) != 0 {
            return false;
        }

        state.ack_mask |= bit;
        return true;
    }

    if sequence == state.ack_base {
        return false;
    }

    let behind = state.ack_base - sequence;
    if behind > WINDOW_SIZE as u64 {
        return false;
    }

    let old_bit = 1u64 << (behind - 1);
    if (state.ack_mask & old_bit) != 0 {
        return false;
    }

    state.ack_mask |= old_bit;
    true
}

fn deliver_contiguous(state: &mut ChannelState, on_delivered: &OnDelivered) {
    while (state.ack_mask & 1) != 0 {
        let confirmed = if state.ack_base == SENTINEL_ACK_BASE {
            0
        } else {
            state.ack_base + 1
        };
        state.ack_base = confirmed;
        state.ack_mask >>= 1;

        if let Some((event_type, body)) = state.out_of_order.remove(&confirmed) {
            on_delivered(event_type, &body);
        }
    }
}

fn sample_rtt(state: &mut ChannelState, sample_micros: i64) {
    if sample_micros <= 0 {
        return;
    }

    let sample = sample_micros as f64;
    if state.srtt < 0.0 {
        state.srtt = sample;
        state.rtt_var = sample / 2.0;
    } else {
        let delta = sample - state.srtt;
        state.srtt += delta / 8.0;
        state.rtt_var += (delta.abs() - state.rtt_var) / 4.0;
    }

    let rto = state.srtt + (4.0 * state.rtt_var).max(1000.0);
    state.rto = (rto as i64).clamp(MIN_RTO_MICROS, MAX_RTO_MICROS);
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicI64, Ordering};
    use std::sync::Arc;

    struct FakeClock(AtomicI64);

    impl FakeClock {
        fn new() -> Arc<Self> {
            Arc::new(Self(AtomicI64::new(0)))
        }

        fn now(&self) -> i64 {
            self.0.load(Ordering::Relaxed)
        }

        fn advance(&self, micros: i64) {
            self.0.fetch_add(micros, Ordering::Relaxed);
        }
    }

    type Delivered = Vec<(u16, Vec<u8>)>;

    struct Endpoint {
        frames: Arc<Mutex<Vec<Vec<u8>>>>,
        delivered: Arc<Mutex<Delivered>>,
        expired: Arc<Mutex<Vec<u16>>>,
        channel: ReliableControlChannel,
    }

    fn endpoint() -> Endpoint {
        let frames = Arc::new(Mutex::new(Vec::new()));
        let delivered = Arc::new(Mutex::new(Vec::new()));
        let expired = Arc::new(Mutex::new(Vec::new()));

        let frames_out = Arc::clone(&frames);
        let delivered_out = Arc::clone(&delivered);
        let expired_out = Arc::clone(&expired);
        let channel = ReliableControlChannel::new(
            move |frame: &[u8]| frames_out.lock().expect("frames").push(frame.to_vec()),
            move |event_type: u16, body: &[u8]| {
                delivered_out
                    .lock()
                    .expect("delivered")
                    .push((event_type, body.to_vec()));
            },
            Some(Box::new(move |event_type: u16| {
                expired_out.lock().expect("expired").push(event_type);
            })),
        );

        Endpoint {
            frames,
            delivered,
            expired,
            channel,
        }
    }

    fn frames_of(endpoint: &Endpoint) -> Vec<Vec<u8>> {
        endpoint.frames.lock().expect("frames").clone()
    }

    fn take_frames(endpoint: &Endpoint) -> Vec<Vec<u8>> {
        std::mem::take(&mut *endpoint.frames.lock().expect("frames"))
    }

    fn delivered_of(endpoint: &Endpoint) -> Delivered {
        endpoint.delivered.lock().expect("delivered").clone()
    }

    #[test]
    fn data_and_ack_roundtrip_through_the_codec() {
        let body = [1u8, 2, 3, 4, 5];
        let frame = encode_data(
            7,
            SENTINEL_ACK_BASE,
            0b101,
            ControlEventType::Key.as_u16(),
            &body,
        )
        .expect("encode");
        assert_eq!(frame.len(), HEADER_SIZE + body.len());

        let message = decode(&frame).expect("decode");
        assert!(!message.is_ack);
        assert_eq!(message.sequence, 7);
        assert_eq!(message.ack_base, SENTINEL_ACK_BASE);
        assert_eq!(message.ack_mask, 0b101);
        assert_eq!(message.event_type, ControlEventType::Key.as_u16());
        assert_eq!(message.body, body.to_vec());

        let ack = encode_ack(3, 0xF);
        assert_eq!(ack.len(), HEADER_SIZE);
        let decoded = decode(&ack).expect("decode ack");
        assert!(decoded.is_ack);
        assert_eq!(decoded.ack_base, 3);
        assert_eq!(decoded.ack_mask, 0xF);
        assert!(decoded.body.is_empty());
    }

    #[test]
    fn codec_rejects_malformed_frames() {
        let body = [9u8, 9];
        let frame = encode_data(0, SENTINEL_ACK_BASE, 0, 1, &body).expect("encode");

        assert!(decode(&frame[..HEADER_SIZE - 1]).is_none());

        let mut length_lie = frame.clone();
        length_lie[30..32].copy_from_slice(&9u16.to_be_bytes());
        assert!(decode(&length_lie).is_none());

        let mut bad_kind = frame.clone();
        bad_kind[0] = 9;
        assert!(decode(&bad_kind).is_none());

        let mut bad_reserved = frame.clone();
        bad_reserved[1] = 1;
        assert!(decode(&bad_reserved).is_none());

        let mut ack_with_body = encode_ack(0, 0);
        ack_with_body[30..32].copy_from_slice(&1u16.to_be_bytes());
        ack_with_body.push(0);
        assert!(decode(&ack_with_body).is_none());
    }

    #[test]
    fn body_budget_is_the_datagram_budget() {
        assert_eq!(MAX_BODY_LENGTH, 1200 - 64 - 32);
        let oversized = vec![0u8; MAX_BODY_LENGTH + 1];
        assert_eq!(
            encode_data(0, SENTINEL_ACK_BASE, 0, 1, &oversized),
            Err(ControlError::BodyTooLarge)
        );
        assert!(encode_data(0, SENTINEL_ACK_BASE, 0, 1, &vec![0u8; MAX_BODY_LENGTH]).is_ok());
    }

    #[test]
    fn out_of_order_data_is_buffered_not_delivered_early() {
        let clock = FakeClock::new();
        let sender = endpoint();
        let receiver = endpoint();

        sender
            .channel
            .try_enqueue(ControlEventType::Key.as_u16(), &[10], clock.now())
            .expect("enqueue 0");
        sender
            .channel
            .try_enqueue(ControlEventType::MouseButton.as_u16(), &[20], clock.now())
            .expect("enqueue 1");
        sender
            .channel
            .try_enqueue(ControlEventType::Wheel.as_u16(), &[30], clock.now())
            .expect("enqueue 2");

        let outbound = frames_of(&sender);
        assert_eq!(outbound.len(), 3);
        assert_eq!(sender.channel.pending_count(), 3);

        // Deliver the second before the first.
        receiver.channel.on_frame(&outbound[1], clock.now());
        assert!(
            delivered_of(&receiver).is_empty(),
            "gap must block delivery"
        );
        assert_eq!(receiver.channel.buffered_out_of_order(), 1);

        let acks = frames_of(&receiver);
        assert_eq!(acks.len(), 1, "receiver must ACK");
        sender.channel.on_frame(&acks[0], clock.now());
        assert_eq!(
            sender.channel.pending_count(),
            2,
            "only the ACKed message leaves"
        );

        // Now the missing first message releases the buffered pair.
        receiver.channel.on_frame(&outbound[0], clock.now());
        let delivered = delivered_of(&receiver);
        assert_eq!(delivered.len(), 2);
        assert_eq!(delivered[0].0, ControlEventType::Key.as_u16());
        assert_eq!(delivered[1].0, ControlEventType::MouseButton.as_u16());

        receiver.channel.on_frame(&outbound[2], clock.now());
        let delivered = delivered_of(&receiver);
        assert_eq!(delivered.len(), 3);
        assert_eq!(delivered[2].0, ControlEventType::Wheel.as_u16());
    }

    #[test]
    fn duplicate_data_is_acknowledged_again_but_executed_once() {
        let clock = FakeClock::new();
        let sender = endpoint();
        let receiver = endpoint();

        sender
            .channel
            .try_enqueue(ControlEventType::Key.as_u16(), &[0x1e], clock.now())
            .expect("enqueue");
        let outbound = frames_of(&sender);

        receiver.channel.on_frame(&outbound[0], clock.now());
        assert_eq!(delivered_of(&receiver).len(), 1);

        take_frames(&receiver);
        receiver.channel.on_frame(&outbound[0], clock.now());
        assert_eq!(delivered_of(&receiver).len(), 1, "no second side effect");
        assert_eq!(frames_of(&receiver).len(), 1, "ACK must repeat");
    }

    #[test]
    fn lost_ack_triggers_retransmission_with_the_same_message_number() {
        let clock = FakeClock::new();
        let sender = endpoint();
        let receiver = endpoint();

        sender
            .channel
            .try_enqueue(ControlEventType::Key.as_u16(), &[0xab], clock.now())
            .expect("enqueue");
        assert_eq!(sender.channel.pending_count(), 1);

        // The first ACK is lost entirely. Advance past the initial RTO.
        clock.advance(INITIAL_RTO_MICROS + 1);
        take_frames(&sender);
        sender.channel.tick(clock.now());

        let retransmitted = frames_of(&sender);
        assert_eq!(retransmitted.len(), 1, "RTO must trigger a resend");
        let message = decode(&retransmitted[0]).expect("decode");
        assert_eq!(message.sequence, 0, "same control message number");

        receiver.channel.on_frame(&retransmitted[0], clock.now());
        let delivered = delivered_of(&receiver);
        assert_eq!(delivered.len(), 1);
        assert_eq!(delivered[0].0, ControlEventType::Key.as_u16());

        let ack = frames_of(&receiver);
        sender.channel.on_frame(&ack[0], clock.now());
        assert_eq!(sender.channel.pending_count(), 0);
    }

    #[test]
    fn karn_rule_keeps_retransmitted_messages_out_of_the_rtt_sample() {
        let clock = FakeClock::new();
        let sender = endpoint();
        let receiver = endpoint();

        sender
            .channel
            .try_enqueue(ControlEventType::Key.as_u16(), &[1], clock.now())
            .expect("enqueue");
        take_frames(&sender);
        clock.advance(INITIAL_RTO_MICROS + 1);
        sender.channel.tick(clock.now());
        let retransmitted = frames_of(&sender);

        receiver.channel.on_frame(&retransmitted[0], clock.now());
        let ack = frames_of(&receiver);
        clock.advance(5_000);
        sender.channel.on_frame(&ack[0], clock.now());

        assert_eq!(
            sender.channel.smoothed_rtt_micros(),
            -1.0,
            "a retransmitted message must not produce an RTT sample"
        );
        assert_eq!(sender.channel.current_rto_micros(), INITIAL_RTO_MICROS);
    }

    #[test]
    fn first_send_yields_an_rtt_sample_and_clamped_rto() {
        let clock = FakeClock::new();
        let sender = endpoint();
        let receiver = endpoint();

        sender
            .channel
            .try_enqueue(ControlEventType::Key.as_u16(), &[1], clock.now())
            .expect("enqueue");
        let outbound = frames_of(&sender);

        clock.advance(20_000);
        receiver.channel.on_frame(&outbound[0], clock.now());
        let ack = frames_of(&receiver);
        clock.advance(1_000);
        sender.channel.on_frame(&ack[0], clock.now());

        assert!((sender.channel.smoothed_rtt_micros() - 21_000.0).abs() < 1.0);
        // srtt + max(4 * rttvar, 1000) = 21000 + 42000, inside the 30 ms..1 s clamp.
        assert_eq!(sender.channel.current_rto_micros(), 63_000);
    }

    #[test]
    fn window_boundary_rejects_the_sixty_fifth_message() {
        let clock = FakeClock::new();
        let sender = endpoint();

        for index in 0..WINDOW_SIZE {
            sender
                .channel
                .try_enqueue(
                    ControlEventType::MouseMove.as_u16(),
                    &[index as u8],
                    clock.now(),
                )
                .expect("within window");
        }
        assert_eq!(sender.channel.pending_count(), WINDOW_SIZE);
        assert_eq!(
            sender
                .channel
                .try_enqueue(ControlEventType::MouseMove.as_u16(), &[0], clock.now()),
            Err(EnqueueError::WindowFull)
        );
    }

    #[test]
    fn receive_window_rejects_data_beyond_sixty_four_messages() {
        let clock = FakeClock::new();
        let sender = endpoint();
        let receiver = endpoint();

        // Message 65 cannot be represented in the 64-bit mask, so it is dropped and the
        // receiver still answers with its current ACK.
        let far = encode_data(
            65,
            SENTINEL_ACK_BASE,
            0,
            ControlEventType::Key.as_u16(),
            &[1],
        )
        .expect("encode");
        take_frames(&receiver);
        receiver.channel.on_frame(&far, clock.now());
        assert!(delivered_of(&receiver).is_empty());
        assert_eq!(
            frames_of(&receiver).len(),
            1,
            "out-of-window data still gets an ACK"
        );

        // Message 63 is inside the window.
        let near = encode_data(
            63,
            SENTINEL_ACK_BASE,
            0,
            ControlEventType::Key.as_u16(),
            &[2],
        )
        .expect("encode");
        receiver.channel.on_frame(&near, clock.now());
        assert!(
            delivered_of(&receiver).is_empty(),
            "hole below still blocks delivery"
        );
        assert_eq!(receiver.channel.ack_mask(), 1u64 << 63);

        let _ = sender;
    }

    #[test]
    fn sentinel_ack_base_confirms_message_zero_selectively() {
        let clock = FakeClock::new();
        let sender = endpoint();
        let receiver = endpoint();

        for index in 0..3 {
            sender
                .channel
                .try_enqueue(ControlEventType::Key.as_u16(), &[index as u8], clock.now())
                .expect("enqueue");
        }

        // Receiver sees only message 1 while the sentinel is still in force.
        let outbound = frames_of(&sender);
        receiver.channel.on_frame(&outbound[1], clock.now());
        let ack = frames_of(&receiver);
        let decoded = decode(&ack[0]).expect("decode");
        assert_eq!(decoded.ack_base, SENTINEL_ACK_BASE);
        assert_eq!(decoded.ack_mask, 0b10);

        sender.channel.on_frame(&ack[0], clock.now());
        assert_eq!(
            sender.channel.pending_count(),
            2,
            "the selective ACK must clear message 1 without an RTO"
        );
    }

    #[test]
    fn fast_retransmit_resends_the_hole_below_the_highest_seen_message() {
        let clock = FakeClock::new();
        let sender = endpoint();
        let receiver = endpoint();

        for index in 0..3 {
            sender
                .channel
                .try_enqueue(ControlEventType::Key.as_u16(), &[index as u8], clock.now())
                .expect("enqueue");
        }
        let outbound = frames_of(&sender);

        // Message 0 is lost; the receiver reports 1 and 2 selectively.
        receiver.channel.on_frame(&outbound[1], clock.now());
        receiver.channel.on_frame(&outbound[2], clock.now());
        let acks = frames_of(&receiver);

        take_frames(&sender);
        sender.channel.on_frame(&acks[1], clock.now());
        let resent = frames_of(&sender);
        assert_eq!(resent.len(), 1, "only the hole is retransmitted");
        assert_eq!(decode(&resent[0]).expect("decode").sequence, 0);
    }

    #[test]
    fn message_expires_after_the_attempt_ceiling_and_reports_the_event_type() {
        let clock = FakeClock::new();
        let sender = endpoint();

        sender
            .channel
            .try_enqueue(ControlEventType::MouseButton.as_u16(), &[1], clock.now())
            .expect("enqueue");

        // Drive eight retransmissions; each doubles the backoff up to four shifts.
        for _ in 0..MAX_ATTEMPTS {
            clock.advance(MAX_RTO_MICROS + 1);
            sender.channel.tick(clock.now());
        }
        clock.advance(MAX_RTO_MICROS + 1);
        sender.channel.tick(clock.now());

        assert_eq!(
            sender.channel.pending_count(),
            0,
            "message must be released"
        );
        assert_eq!(
            *sender.expired.lock().expect("expired"),
            vec![ControlEventType::MouseButton.as_u16()],
            "the upper layer must release held keys"
        );
    }

    #[test]
    fn message_expires_at_the_two_second_deadline() {
        let clock = FakeClock::new();
        let sender = endpoint();

        sender
            .channel
            .try_enqueue(ControlEventType::Key.as_u16(), &[1], clock.now())
            .expect("enqueue");
        clock.advance(MESSAGE_LIFETIME_MICROS);
        sender.channel.tick(clock.now());

        assert_eq!(sender.channel.pending_count(), 0);
        assert_eq!(sender.expired.lock().expect("expired").len(), 1);
    }

    #[test]
    fn backoff_is_capped_at_four_doublings() {
        let item = PendingSend {
            event_type: 1,
            body: vec![],
            first_sent_micros: 0,
            last_sent_micros: 0,
            attempts: 7,
            retransmitted: false,
            fast_retransmitted: false,
        };
        // Four doublings of the 30 ms floor is still below the 1 s ceiling.
        assert_eq!(backoff_for(&item, MIN_RTO_MICROS), 480_000);
        // The ceiling applies when the RTO itself is large.
        assert_eq!(backoff_for(&item, MAX_RTO_MICROS), MAX_RTO_MICROS);

        let item = PendingSend {
            attempts: 1,
            ..item
        };
        assert_eq!(backoff_for(&item, 30_000), 60_000);
    }

    #[test]
    fn unknown_event_type_still_round_trips() {
        let frame = encode_data(0, SENTINEL_ACK_BASE, 0, 0xbeef, &[7]).expect("encode");
        let message = decode(&frame).expect("decode");
        assert_eq!(message.event_type, 0xbeef);
        assert_eq!(ControlEventType::from_u16(0xbeef), None);
        assert_eq!(
            ControlEventType::from_u16(10),
            Some(ControlEventType::ControlLease)
        );
    }
}
