//! Path probing and keepalive supervision.
//!
//! 32-byte encrypted probe payload (identical to `PathProbe.cs`):
//!
//! | offset | length | field |
//! |---:|---:|---|
//! | 0 | 1 | kind: 1 = request, 2 = response |
//! | 1 | 7 | reserved, must be zero |
//! | 8 | 8 | token |
//! | 16 | 8 | sender monotonic microseconds |
//! | 24 | 8 | echoed requester monotonic microseconds |
//!
//! The echo carries the requester's own send timestamp back verbatim, so RTT is measured
//! entirely with the requester's monotonic clock and needs no clock synchronisation.
//!
//! Sending a probe never refreshes the "last valid response" timestamp. Only a matching,
//! authenticated response (or an inbound request) does; otherwise a path that never answers
//! could never be declared down.

use std::collections::{HashMap, VecDeque};

use crate::crypto::rand;

/// Probe payload length in bytes.
pub const PROBE_SIZE: usize = 32;
/// Maximum outstanding requests kept for token matching.
pub const MAX_OUTSTANDING: usize = 32;
/// Default number of consecutive silent intervals before a path is marked down.
pub const DEFAULT_MISSES_FOR_DOWN: i32 = 3;
/// Default keepalive interval (10 s), matching the protocol default range 5-30 s.
pub const DEFAULT_KEEPALIVE_MICROS: i64 = 10_000_000;
/// Minimum configurable keepalive interval (1 s).
pub const MIN_KEEPALIVE_MICROS: i64 = 1_000_000;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum ProbeKind {
    Request = 1,
    Response = 2,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ProbeMessage {
    pub kind: ProbeKind,
    pub token: u64,
    pub send_micros: u64,
    pub echo_micros: u64,
}

impl ProbeMessage {
    pub fn encode_request(token: u64, now_micros: i64) -> [u8; PROBE_SIZE] {
        Self::encode(ProbeKind::Request, token, now_micros as u64, 0)
    }

    pub fn encode_response(token: u64, now_micros: i64, echo_micros: u64) -> [u8; PROBE_SIZE] {
        Self::encode(ProbeKind::Response, token, now_micros as u64, echo_micros)
    }

    fn encode(kind: ProbeKind, token: u64, send: u64, echo: u64) -> [u8; PROBE_SIZE] {
        let mut payload = [0u8; PROBE_SIZE];
        payload[0] = kind as u8;
        payload[8..16].copy_from_slice(&token.to_be_bytes());
        payload[16..24].copy_from_slice(&send.to_be_bytes());
        payload[24..32].copy_from_slice(&echo.to_be_bytes());
        payload
    }

    /// Strict parse: exactly 32 bytes, a known kind and seven zero reserved bytes.
    pub fn parse(payload: &[u8]) -> Option<Self> {
        if payload.len() != PROBE_SIZE {
            return None;
        }

        let kind = match payload[0] {
            1 => ProbeKind::Request,
            2 => ProbeKind::Response,
            _ => return None,
        };

        if payload[1..8].iter().any(|byte| *byte != 0) {
            return None;
        }

        Some(Self {
            kind,
            token: u64::from_be_bytes(payload[8..16].try_into().expect("bounded field")),
            send_micros: u64::from_be_bytes(payload[16..24].try_into().expect("bounded field")),
            echo_micros: u64::from_be_bytes(payload[24..32].try_into().expect("bounded field")),
        })
    }
}

/// Link health as observed by authenticated probes.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
#[repr(u8)]
pub enum PathStatus {
    /// Not enough evidence yet.
    #[default]
    Probing = 0,
    /// Recent authenticated probes round-tripped.
    Alive = 1,
    /// Consecutive keepalives produced no valid response.
    Down = 2,
}

impl PathStatus {
    pub fn from_u8(value: u8) -> Option<Self> {
        match value {
            0 => Some(Self::Probing),
            1 => Some(Self::Alive),
            2 => Some(Self::Down),
            _ => None,
        }
    }
}

/// Keepalive, RTT and loss tracking for one component.
pub struct PathProbe {
    misses_for_down: i32,
    interval_micros: i64,
    outstanding: VecDeque<(u64, i64)>,
    lookup: HashMap<u64, i64>,
    sent: u64,
    answered: u64,
    consecutive_misses: i32,
    srtt: f64,
    silence_since: Option<i64>,
    next_probe_at: Option<i64>,
    status: PathStatus,
}

impl Default for PathProbe {
    fn default() -> Self {
        Self::new(DEFAULT_MISSES_FOR_DOWN, DEFAULT_KEEPALIVE_MICROS)
    }
}

impl PathProbe {
    pub fn new(misses_for_down: i32, keepalive_interval_micros: i64) -> Self {
        Self {
            misses_for_down: misses_for_down.max(1),
            interval_micros: keepalive_interval_micros,
            outstanding: VecDeque::new(),
            lookup: HashMap::new(),
            sent: 0,
            answered: 0,
            consecutive_misses: 0,
            srtt: -1.0,
            silence_since: None,
            next_probe_at: None,
            status: PathStatus::Probing,
        }
    }

    pub fn status(&self) -> PathStatus {
        self.status
    }

    pub fn consecutive_misses(&self) -> i32 {
        self.consecutive_misses
    }

    pub fn probes_sent(&self) -> u64 {
        self.sent
    }

    pub fn probes_answered(&self) -> u64 {
        self.answered
    }

    /// Smoothed RTT in microseconds; -1 until a response arrives.
    pub fn smoothed_rtt_micros(&self) -> f64 {
        self.srtt
    }

    /// Fraction of probes never answered, 0..1.
    pub fn loss_ratio(&self) -> f64 {
        if self.sent == 0 {
            0.0
        } else {
            1.0 - (self.answered as f64 / self.sent as f64)
        }
    }

    pub fn keepalive_interval_micros(&self) -> i64 {
        self.interval_micros
    }

    /// Set the keepalive interval; clamped to at least one second.
    pub fn set_keepalive_interval_micros(&mut self, micros: i64) {
        self.interval_micros = micros.max(MIN_KEEPALIVE_MICROS);
    }

    /// When the silence clock started: the first send, or the most recent valid response.
    pub fn silence_since_micros(&self) -> Option<i64> {
        self.silence_since
    }

    /// Build a request payload and remember its send time for token matching.
    pub fn create_request(&mut self, now_micros: i64) -> [u8; PROBE_SIZE] {
        let token = rand::u64();
        self.remember(token, now_micros);
        self.sent += 1;

        // The very first send starts the silence clock. Later sends must not refresh it.
        if self.silence_since.is_none() {
            self.silence_since = Some(now_micros);
        }

        ProbeMessage::encode_request(token, now_micros)
    }

    /// Feed a received probe payload. Returns an optional response payload to send back.
    /// A request is answered and refreshes liveness; a response completes an RTT sample.
    pub fn on_probe(&mut self, payload: &[u8], now_micros: i64) -> Option<[u8; PROBE_SIZE]> {
        let message = ProbeMessage::parse(payload)?;

        if message.kind == ProbeKind::Request {
            self.mark_alive(now_micros);
            return Some(ProbeMessage::encode_response(
                message.token,
                now_micros,
                message.send_micros,
            ));
        }

        let sent_at = self.lookup.remove(&message.token)?;
        let sample = now_micros - sent_at;
        if sample < 0 {
            return None;
        }

        self.srtt = if self.srtt < 0.0 {
            sample as f64
        } else {
            (self.srtt * 7.0 + sample as f64) / 8.0
        };
        self.answered += 1;
        self.mark_alive(now_micros);
        None
    }

    /// Drive keepalive. Returns true when the caller should send a probe request for this
    /// component. Also ages the silence clock and flips the path to [`PathStatus::Down`].
    pub fn tick(&mut self, now_micros: i64) -> bool {
        if let Some(since) = self.silence_since {
            if now_micros - since > self.interval_micros {
                self.consecutive_misses += 1;
                // Advance the reference by one interval so one long stall is not counted N times.
                self.silence_since = Some(now_micros);
                if self.consecutive_misses >= self.misses_for_down {
                    self.status = PathStatus::Down;
                }
            }
        }

        if self.next_probe_at.is_none_or(|next| now_micros >= next) {
            self.next_probe_at = Some(now_micros + self.interval_micros);
            return true;
        }

        false
    }

    fn mark_alive(&mut self, now_micros: i64) {
        self.silence_since = Some(now_micros);
        self.consecutive_misses = 0;
        self.status = PathStatus::Alive;
    }

    fn remember(&mut self, token: u64, sent_at_micros: i64) {
        // Bounded outstanding set: drop the oldest token rather than growing without limit.
        while self.outstanding.len() >= MAX_OUTSTANDING {
            if let Some((evicted, _)) = self.outstanding.pop_front() {
                self.lookup.remove(&evicted);
            }
        }

        self.outstanding.push_back((token, sent_at_micros));
        self.lookup.insert(token, sent_at_micros);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn token_of(payload: &[u8; PROBE_SIZE]) -> u64 {
        u64::from_be_bytes(payload[8..16].try_into().expect("bounded field"))
    }

    #[test]
    fn request_and_response_roundtrip_with_echo() {
        let send_time = 1_000_000i64;
        let request = ProbeMessage::encode_request(0xAABB, send_time);
        let parsed = ProbeMessage::parse(&request).expect("parse request");
        assert_eq!(parsed.kind, ProbeKind::Request);
        assert_eq!(parsed.token, 0xAABB);
        assert_eq!(parsed.send_micros, send_time as u64);
        assert_eq!(parsed.echo_micros, 0);

        let response = ProbeMessage::encode_response(0xAABB, 2_000_000, send_time as u64);
        let answered = ProbeMessage::parse(&response).expect("parse response");
        assert_eq!(answered.kind, ProbeKind::Response);
        assert_eq!(answered.token, 0xAABB);
        assert_eq!(answered.echo_micros, send_time as u64);
    }

    #[test]
    fn parser_rejects_short_wrong_kind_and_reserved_bytes() {
        let request = ProbeMessage::encode_request(1, 0);
        assert!(ProbeMessage::parse(&request[..31]).is_none());
        assert!(ProbeMessage::parse(&[0u8; 33]).is_none());

        let mut bad_kind = request;
        bad_kind[0] = 7;
        assert!(ProbeMessage::parse(&bad_kind).is_none());

        let mut bad_reserved = request;
        bad_reserved[3] = 1;
        assert!(ProbeMessage::parse(&bad_reserved).is_none());
    }

    #[test]
    fn rtt_requires_a_matching_token_and_sending_never_refreshes_liveness() {
        let mut probe = PathProbe::new(3, 1_000_000);
        assert!(probe.tick(0), "first tick sends");

        let request = probe.create_request(0);
        assert_eq!(probe.status(), PathStatus::Probing, "no response yet");

        // A response with an unknown token must not count.
        let unknown = ProbeMessage::encode_response(0xDEAD, 0, 0);
        assert!(probe.on_probe(&unknown, 10_000).is_none());
        assert_eq!(probe.probes_answered(), 0);
        assert_eq!(probe.status(), PathStatus::Probing);

        let token = token_of(&request);
        let response = ProbeMessage::encode_response(token, 0, 0);
        assert!(
            probe.on_probe(&response, 20_000).is_none(),
            "a response carries no reply"
        );
        assert!((probe.smoothed_rtt_micros() - 20_000.0).abs() < 1.0);
        assert_eq!(probe.status(), PathStatus::Alive);
        assert_eq!(probe.consecutive_misses(), 0);
        assert_eq!(probe.probes_answered(), 1);
    }

    #[test]
    fn an_inbound_request_is_answered_and_counts_as_liveness() {
        let mut probe = PathProbe::new(3, 1_000_000);
        let request = ProbeMessage::encode_request(0x1234, 55);
        let reply = probe.on_probe(&request, 60).expect("must answer");
        let parsed = ProbeMessage::parse(&reply).expect("parse");
        assert_eq!(parsed.kind, ProbeKind::Response);
        assert_eq!(parsed.token, 0x1234);
        assert_eq!(parsed.echo_micros, 55);
        assert_eq!(probe.status(), PathStatus::Alive);
    }

    #[test]
    fn three_silent_intervals_mark_the_path_down() {
        let mut probe = PathProbe::new(3, 1_000_000);
        assert!(probe.tick(0));
        probe.create_request(0);

        for index in 1..=3 {
            let now = index * 1_000_001;
            probe.tick(now);
            assert_eq!(
                probe.status(),
                if index < 3 {
                    PathStatus::Probing
                } else {
                    PathStatus::Down
                },
                "silence {index}"
            );
        }

        assert!(probe.consecutive_misses() >= 3);
    }

    #[test]
    fn a_late_response_revives_a_down_path() {
        let mut probe = PathProbe::new(3, 1_000_000);
        probe.tick(0);
        let request = probe.create_request(0);
        for index in 1..=3 {
            probe.tick(index * 1_000_001);
        }
        assert_eq!(probe.status(), PathStatus::Down);

        let token = token_of(&request);
        let response = ProbeMessage::encode_response(token, 0, 0);
        probe.on_probe(&response, 3_000_100);
        assert_eq!(probe.status(), PathStatus::Alive);
        assert_eq!(probe.consecutive_misses(), 0);
    }

    #[test]
    fn outstanding_tokens_are_bounded() {
        let mut probe = PathProbe::new(3, 1_000_000);
        for index in 0..(MAX_OUTSTANDING as i64 * 2) {
            probe.create_request(index);
        }
        assert_eq!(probe.lookup.len(), MAX_OUTSTANDING);
        assert_eq!(probe.probes_sent(), MAX_OUTSTANDING as u64 * 2);
    }

    #[test]
    fn keepalive_interval_is_clamped() {
        let mut probe = PathProbe::new(3, 1_000_000);
        probe.set_keepalive_interval_micros(1);
        assert_eq!(probe.keepalive_interval_micros(), MIN_KEEPALIVE_MICROS);
        probe.set_keepalive_interval_micros(5_000_000);
        assert_eq!(probe.keepalive_interval_micros(), 5_000_000);
    }

    #[test]
    fn tick_only_sends_when_the_interval_elapsed() {
        let mut probe = PathProbe::new(3, 1_000_000);
        assert!(probe.tick(0));
        assert!(!probe.tick(1));
        assert!(!probe.tick(999_999));
        assert!(probe.tick(1_000_000));
    }
}
