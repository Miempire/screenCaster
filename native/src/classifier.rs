//! Structural datagram classification, mirroring `DatagramClassifier.cs`.
//!
//! One receive loop must dispatch four wire formats that share a UDP socket. The order is
//! fixed and matters: the full `SCT1` application magic wins over the TURN ChannelData
//! range `0x4000..=0x7fff`, because `0x5343` (the first two bytes of `SCT1`/`SCSH`) is
//! itself a legal channel number. A positive [`DatagramKind::TurnChannelData`] result is
//! *not* permission to relay anything: the caller still has to prove the source is a
//! registered TURN server, that an allocation owns the channel and that the length is
//! exact.

/// RFC 5389 magic cookie.
pub const STUN_COOKIE: u32 = 0x2112_A442;
/// ASCII "SCSH", the handshake magic.
pub const HANDSHAKE_MAGIC: [u8; 4] = *b"SCSH";
/// Application magic `SCT1` as a big-endian u32.
pub const APPLICATION_MAGIC: u32 = 0x5343_5431;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DatagramKind {
    /// ScreenCast application packet (SCT1), protected by AES-256-GCM.
    Application,
    /// ScreenCast handshake packet (SCSH); not implemented in this crate.
    Handshake,
    /// RFC 5389 STUN message.
    Stun,
    /// TURN ChannelData; only meaningful after allocation validation.
    TurnChannelData,
    /// Unrecognized bytes: discard without allocating or replying.
    Unknown,
}

/// Classify one datagram by structural magic only.
pub fn classify(datagram: &[u8]) -> DatagramKind {
    if datagram.len() >= 4 && datagram[0..4] == crate::packet::MAGIC {
        return DatagramKind::Application;
    }

    if datagram.len() >= 4 && datagram[0..4] == HANDSHAKE_MAGIC {
        return DatagramKind::Handshake;
    }

    // RFC 5389: the two most significant bits of the type are zero and the magic cookie
    // occupies bytes 4..8. Length alignment is validated later by the STUN codec.
    if datagram.len() >= 20
        && datagram[0] & 0xC0 == 0
        && u32::from_be_bytes([datagram[4], datagram[5], datagram[6], datagram[7]]) == STUN_COOKIE
    {
        return DatagramKind::Stun;
    }

    if datagram.len() >= 4 {
        let channel = u16::from_be_bytes([datagram[0], datagram[1]]);
        if (0x4000..=0x7fff).contains(&channel) {
            return DatagramKind::TurnChannelData;
        }
    }

    DatagramKind::Unknown
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

    #[test]
    fn application_magic_wins_over_the_turn_channel_overlap() {
        let mut application = b"SCT1".to_vec();
        application.resize(24, 0);
        assert_eq!(classify(&application), DatagramKind::Application);

        // The first two bytes 0x5343 are a legal TURN channel number.
        assert_eq!(u16::from_be_bytes([0x53, 0x43]), 0x5343);
        assert_eq!(classify(&application), DatagramKind::Application);
    }

    #[test]
    fn handshake_magic_is_recognised() {
        let mut handshake = b"SCSH".to_vec();
        handshake.resize(24, 0);
        assert_eq!(classify(&handshake), DatagramKind::Handshake);
    }

    #[test]
    fn stun_requires_the_cookie_and_zero_leading_bits() {
        let stun = hex("000100002112A442000000000000000000000000");
        assert_eq!(classify(&stun), DatagramKind::Stun);

        let mut bad_bits = stun.clone();
        bad_bits[0] = 0xC0;
        assert_ne!(classify(&bad_bits), DatagramKind::Stun);

        let mut bad_cookie = stun.clone();
        bad_cookie[4] = 0x00;
        assert_ne!(classify(&bad_cookie), DatagramKind::Stun);

        assert_eq!(classify(&stun[..19]), DatagramKind::Unknown);
    }

    #[test]
    fn turn_channel_data_range_is_recognised() {
        let channel_data = [0x40, 0x07, 0x00, 0x04, 0xca, 0xfe, 0xba, 0xbe];
        assert_eq!(classify(&channel_data), DatagramKind::TurnChannelData);

        let high = [0x7f, 0xff, 0x00, 0x00];
        assert_eq!(classify(&high), DatagramKind::TurnChannelData);

        let below = [0x3f, 0xff, 0x00, 0x00];
        assert_eq!(classify(&below), DatagramKind::Unknown);

        let above = [0x80, 0x00, 0x00, 0x00];
        assert_eq!(classify(&above), DatagramKind::Unknown);
    }

    #[test]
    fn unrelated_bytes_are_unknown() {
        assert_eq!(classify(&[0x00, 0x09, 0x00, 0x00]), DatagramKind::Unknown);
        assert_eq!(classify(&[]), DatagramKind::Unknown);
        assert_eq!(classify(&[0x53, 0x43]), DatagramKind::Unknown);
    }
}
