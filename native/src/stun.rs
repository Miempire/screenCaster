//! RFC 5389 Binding wire primitives and a minimal Binding client.
//!
//! This module implements only the standard STUN message format and the Binding
//! transaction needed to discover the public mapping of a media socket. TURN allocations,
//! long-term credentials and authentication are out of scope and are *not* claimed.
//!
//! Message layout: 20-byte header (type u16, length u16, magic cookie `0x2112A442`,
//! transaction id 12) followed by 4-byte aligned TLV attributes.
//!
//! STUN headers are deliberately plaintext: the project's "all UDP is encrypted" statement
//! covers application media/control/session messages, and breaking RFC 5389 framing to
//! encrypt a Binding request would destroy interoperability with ordinary STUN servers.

use std::io;
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr};
use std::time::{Duration, Instant};

use crate::crypto::rand;
use crate::transport::{is_transient, UdpTransport, MAX_DATAGRAM_SIZE};

/// Magic cookie.
pub const MAGIC_COOKIE: u32 = 0x2112_A442;
/// Binding request.
pub const BINDING_REQUEST: u16 = 0x0001;
/// Binding success response.
pub const BINDING_SUCCESS: u16 = 0x0101;
/// Binding error response.
pub const BINDING_ERROR: u16 = 0x0111;
/// XOR-MAPPED-ADDRESS attribute.
pub const XOR_MAPPED_ADDRESS: u16 = 0x0020;
/// MAPPED-ADDRESS attribute (legacy, accepted on parse only).
pub const MAPPED_ADDRESS: u16 = 0x0001;
/// ERROR-CODE attribute.
pub const ERROR_CODE: u16 = 0x0009;
/// UNKNOWN-ATTRIBUTES attribute.
pub const UNKNOWN_ATTRIBUTES: u16 = 0x000A;
/// FINGERPRINT attribute.
pub const FINGERPRINT: u16 = 0x8028;
/// XOR value applied to the FINGERPRINT CRC.
pub const FINGERPRINT_XOR: u32 = 0x5354_554E;
/// Header length in bytes.
pub const HEADER_SIZE: usize = 20;
/// Transaction id length in bytes.
pub const TRANSACTION_ID_SIZE: usize = 12;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StunError {
    /// Message shorter than the 20-byte header.
    TruncatedHeader,
    /// The two most significant type bits are not zero.
    InvalidType,
    /// Magic cookie mismatch.
    BadCookie,
    /// Length field is not a multiple of four or does not match the buffer.
    BadLength,
    /// Attribute header or value runs past the end of the message.
    TruncatedAttribute,
    /// FINGERPRINT is malformed or does not verify.
    BadFingerprint,
    /// FINGERPRINT is not the final attribute.
    FingerprintNotLast,
    /// Transaction id is not 12 bytes.
    BadTransactionId,
    /// XOR-MAPPED-ADDRESS is missing, duplicated or malformed.
    BadXorAddress,
    /// Response came from an address that is not the configured server.
    UnexpectedSource,
    /// Transaction id or message type does not match the outstanding request.
    UnexpectedTransaction,
    /// A mandatory comprehension-required attribute is not understood.
    UnsupportedRequiredAttribute,
    /// Message too long for the length field.
    TooLong,
}

/// One TLV attribute.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StunAttribute {
    pub attr_type: u16,
    pub value: Vec<u8>,
}

/// A parsed STUN message.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StunMessage {
    pub message_type: u16,
    pub transaction_id: [u8; TRANSACTION_ID_SIZE],
    pub attributes: Vec<StunAttribute>,
}

impl StunMessage {
    pub fn attribute(&self, attr_type: u16) -> Option<&StunAttribute> {
        self.attributes
            .iter()
            .find(|attribute| attribute.attr_type == attr_type)
    }

    pub fn attributes_of(&self, attr_type: u16) -> Vec<&StunAttribute> {
        self.attributes
            .iter()
            .filter(|attribute| attribute.attr_type == attr_type)
            .collect()
    }
}

/// Standard IEEE CRC-32 (the zlib polynomial), used only by FINGERPRINT.
pub fn crc32_ieee(data: &[u8]) -> u32 {
    let mut crc: u32 = 0xFFFF_FFFF;
    for byte in data {
        crc ^= u32::from(*byte);
        for _ in 0..8 {
            crc = if crc & 1 != 0 {
                (crc >> 1) ^ 0xEDB8_8320
            } else {
                crc >> 1
            };
        }
    }
    !crc
}

/// Fresh 12-byte transaction id.
pub fn new_transaction_id() -> [u8; TRANSACTION_ID_SIZE] {
    let mut id = [0u8; TRANSACTION_ID_SIZE];
    rand::fill(&mut id);
    id
}

/// Parse a complete STUN message, validating the header, every attribute length and any
/// FINGERPRINT attribute.
pub fn parse(bytes: &[u8]) -> Result<StunMessage, StunError> {
    if bytes.len() < HEADER_SIZE {
        return Err(StunError::TruncatedHeader);
    }
    if bytes[0] & 0xC0 != 0 {
        return Err(StunError::InvalidType);
    }
    if u32::from_be_bytes([bytes[4], bytes[5], bytes[6], bytes[7]]) != MAGIC_COOKIE {
        return Err(StunError::BadCookie);
    }

    let length = u16::from_be_bytes([bytes[2], bytes[3]]) as usize;
    if length & 3 != 0 || bytes.len() != HEADER_SIZE + length {
        return Err(StunError::BadLength);
    }

    let mut transaction_id = [0u8; TRANSACTION_ID_SIZE];
    transaction_id.copy_from_slice(&bytes[8..20]);

    let mut attributes = Vec::new();
    let mut offset = HEADER_SIZE;
    while offset < bytes.len() {
        if bytes.len() - offset < 4 {
            return Err(StunError::TruncatedAttribute);
        }

        let attr_type = u16::from_be_bytes([bytes[offset], bytes[offset + 1]]);
        let size = u16::from_be_bytes([bytes[offset + 2], bytes[offset + 3]]) as usize;
        let padded = (size + 3) & !3;
        if padded > bytes.len() - offset - 4 {
            return Err(StunError::TruncatedAttribute);
        }

        if attr_type == FINGERPRINT {
            if size != 4 {
                return Err(StunError::BadFingerprint);
            }
            if offset + 8 != bytes.len() {
                return Err(StunError::FingerprintNotLast);
            }
            let expected = crc32_ieee(&bytes[..offset]) ^ FINGERPRINT_XOR;
            let actual = u32::from_be_bytes([
                bytes[offset + 4],
                bytes[offset + 5],
                bytes[offset + 6],
                bytes[offset + 7],
            ]);
            if actual != expected {
                return Err(StunError::BadFingerprint);
            }
        }

        attributes.push(StunAttribute {
            attr_type,
            value: bytes[offset + 4..offset + 4 + size].to_vec(),
        });
        offset += 4 + padded;
    }

    Ok(StunMessage {
        message_type: u16::from_be_bytes([bytes[0], bytes[1]]),
        transaction_id,
        attributes,
    })
}

/// Encode a STUN message with 4-byte aligned attributes.
pub fn encode(
    message_type: u16,
    transaction_id: &[u8; TRANSACTION_ID_SIZE],
    attributes: &[StunAttribute],
) -> Result<Vec<u8>, StunError> {
    if message_type & 0xC000 != 0 {
        return Err(StunError::InvalidType);
    }

    let mut length = 0usize;
    for attribute in attributes {
        if attribute.value.len() > u16::MAX as usize {
            return Err(StunError::TooLong);
        }
        length += 4 + ((attribute.value.len() + 3) & !3);
    }
    if length > u16::MAX as usize {
        return Err(StunError::TooLong);
    }

    let mut message = vec![0u8; HEADER_SIZE + length];
    message[0..2].copy_from_slice(&message_type.to_be_bytes());
    message[2..4].copy_from_slice(&(length as u16).to_be_bytes());
    message[4..8].copy_from_slice(&MAGIC_COOKIE.to_be_bytes());
    message[8..20].copy_from_slice(transaction_id);

    let mut offset = HEADER_SIZE;
    for attribute in attributes {
        message[offset..offset + 2].copy_from_slice(&attribute.attr_type.to_be_bytes());
        message[offset + 2..offset + 4]
            .copy_from_slice(&(attribute.value.len() as u16).to_be_bytes());
        message[offset + 4..offset + 4 + attribute.value.len()].copy_from_slice(&attribute.value);
        offset += 4 + ((attribute.value.len() + 3) & !3);
    }

    Ok(message)
}

/// Encode a Binding request.
pub fn encode_request(transaction_id: &[u8; TRANSACTION_ID_SIZE]) -> Vec<u8> {
    encode(BINDING_REQUEST, transaction_id, &[]).expect("an empty Binding request always fits")
}

/// XOR mask for an address: cookie (4 bytes) followed by the transaction id (12 bytes).
fn xor_mask(transaction_id: &[u8; TRANSACTION_ID_SIZE]) -> [u8; 16] {
    let mut mask = [0u8; 16];
    mask[0..4].copy_from_slice(&MAGIC_COOKIE.to_be_bytes());
    mask[4..16].copy_from_slice(transaction_id);
    mask
}

/// Build the XOR-MAPPED-ADDRESS attribute value for an endpoint.
fn encode_xor_address(transaction_id: &[u8; TRANSACTION_ID_SIZE], mapped: SocketAddr) -> Vec<u8> {
    let mask = xor_mask(transaction_id);
    let port = mapped.port() ^ ((MAGIC_COOKIE >> 16) as u16);

    let mut value = Vec::with_capacity(20);
    value.push(0);
    match mapped.ip() {
        IpAddr::V4(address) => {
            value.push(1);
            value.extend_from_slice(&port.to_be_bytes());
            for (index, byte) in address.octets().iter().enumerate() {
                value.push(byte ^ mask[index]);
            }
        }
        IpAddr::V6(address) => {
            value.push(2);
            value.extend_from_slice(&port.to_be_bytes());
            for (index, byte) in address.octets().iter().enumerate() {
                value.push(byte ^ mask[index]);
            }
        }
    }
    value
}

/// Decode an XOR-MAPPED-ADDRESS attribute value.
fn decode_xor_address(
    transaction_id: &[u8; TRANSACTION_ID_SIZE],
    value: &[u8],
) -> Result<SocketAddr, StunError> {
    if value.len() < 4 || value[0] != 0 {
        return Err(StunError::BadXorAddress);
    }

    let family = value[1];
    let expected_length = match family {
        1 => 8,
        2 => 20,
        _ => return Err(StunError::BadXorAddress),
    };
    if value.len() != expected_length {
        return Err(StunError::BadXorAddress);
    }

    let mask = xor_mask(transaction_id);
    let port = u16::from_be_bytes([value[2], value[3]]) ^ ((MAGIC_COOKIE >> 16) as u16);

    let mut address = [0u8; 16];
    let length = value.len() - 4;
    for index in 0..length {
        address[index] = value[4 + index] ^ mask[index];
    }

    let ip = if family == 1 {
        IpAddr::V4(Ipv4Addr::new(
            address[0], address[1], address[2], address[3],
        ))
    } else {
        IpAddr::V6(Ipv6Addr::from(address))
    };

    Ok(SocketAddr::new(ip, port))
}

/// Encode a Binding success response for the observed source address.
pub fn encode_success(
    transaction_id: &[u8; TRANSACTION_ID_SIZE],
    mapped: SocketAddr,
) -> Result<Vec<u8>, StunError> {
    // An IPv4-mapped IPv6 source must be reported as plain IPv4, exactly as the C# server does.
    let mapped = match mapped.ip() {
        IpAddr::V6(address) => match address.to_ipv4_mapped() {
            Some(v4) => SocketAddr::new(IpAddr::V4(v4), mapped.port()),
            None => mapped,
        },
        IpAddr::V4(_) => mapped,
    };

    let attribute = StunAttribute {
        attr_type: XOR_MAPPED_ADDRESS,
        value: encode_xor_address(transaction_id, mapped),
    };
    encode(BINDING_SUCCESS, transaction_id, &[attribute])
}

/// Validate a Binding success response and return the reflexive address.
pub fn decode_success(
    response: &[u8],
    expected_transaction_id: &[u8; TRANSACTION_ID_SIZE],
    source: SocketAddr,
    expected_server: SocketAddr,
) -> Result<SocketAddr, StunError> {
    if source != expected_server {
        return Err(StunError::UnexpectedSource);
    }

    let message = parse(response)?;
    if message.transaction_id != *expected_transaction_id || message.message_type != BINDING_SUCCESS
    {
        return Err(StunError::UnexpectedTransaction);
    }

    let addresses = message.attributes_of(XOR_MAPPED_ADDRESS);
    if addresses.len() != 1 {
        return Err(StunError::BadXorAddress);
    }

    if message.attributes.iter().any(|attribute| {
        attribute.attr_type < 0x8000
            && attribute.attr_type != XOR_MAPPED_ADDRESS
            && attribute.attr_type != MAPPED_ADDRESS
    }) {
        return Err(StunError::UnsupportedRequiredAttribute);
    }

    for mapped in message.attributes_of(MAPPED_ADDRESS) {
        let value = &mapped.value;
        let expected = if value.get(1) == Some(&2) { 20 } else { 8 };
        if value.len() < 4
            || value[0] != 0
            || (value[1] != 1 && value[1] != 2)
            || value.len() != expected
        {
            return Err(StunError::BadXorAddress);
        }
    }

    decode_xor_address(&message.transaction_id, &addresses[0].value)
}

/// Build a response for a Binding request, or `None` for any other message type.
/// Unsupported comprehension-required attributes produce a 420 error response.
pub fn create_binding_response(
    request: &[u8],
    source: SocketAddr,
) -> Result<Option<Vec<u8>>, StunError> {
    let message = parse(request)?;
    if message.message_type != BINDING_REQUEST {
        return Ok(None);
    }

    let mut unknown: Vec<u16> = message
        .attributes
        .iter()
        .filter(|attribute| attribute.attr_type < 0x8000)
        .map(|attribute| attribute.attr_type)
        .collect();
    unknown.sort_unstable();
    unknown.dedup();

    if unknown.is_empty() {
        return encode_success(&message.transaction_id, source).map(Some);
    }

    let reason = b"Unknown Attribute";
    let mut error = Vec::with_capacity(4 + reason.len());
    error.push(0);
    error.push(0);
    error.push(4); // class 4
    error.push(20); // number 20
    error.extend_from_slice(reason);

    let mut types = Vec::with_capacity(unknown.len() * 2);
    for attr_type in &unknown {
        types.extend_from_slice(&attr_type.to_be_bytes());
    }

    let attributes = [
        StunAttribute {
            attr_type: ERROR_CODE,
            value: error,
        },
        StunAttribute {
            attr_type: UNKNOWN_ATTRIBUTES,
            value: types,
        },
    ];
    encode(BINDING_ERROR, &message.transaction_id, &attributes).map(Some)
}

/// Result of one successful Binding transaction.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BindingOutcome {
    /// The server's view of this socket: the public mapping.
    pub mapped: SocketAddr,
    /// Round-trip time of the answered request.
    pub rtt_micros: i64,
}

/// Perform a Binding transaction with RFC 5389 style retransmission.
///
/// The transport must not have a receive owner yet: this function reads the socket itself
/// and is meant to run before [`crate::session::MediaSession::start`].
pub fn query_binding(
    transport: &UdpTransport,
    server: SocketAddr,
    attempts: u32,
    initial_timeout: Duration,
) -> io::Result<BindingOutcome> {
    let attempts = attempts.max(1);
    let mut timeout = initial_timeout;

    for _ in 0..attempts {
        let transaction_id = new_transaction_id();
        let request = encode_request(&transaction_id);
        let started = Instant::now();
        transport.send_to(&request, server)?;

        let deadline = Instant::now() + timeout;
        let mut buffer = vec![0u8; MAX_DATAGRAM_SIZE];
        loop {
            match transport.recv_from(&mut buffer) {
                Ok((length, source)) => {
                    if let Ok(mapped) =
                        decode_success(&buffer[..length], &transaction_id, source, server)
                    {
                        return Ok(BindingOutcome {
                            mapped,
                            rtt_micros: started.elapsed().as_micros() as i64,
                        });
                    }
                    // Not our transaction: keep waiting until the deadline.
                    if Instant::now() >= deadline {
                        break;
                    }
                }
                Err(error) => {
                    if !is_transient(&error) {
                        return Err(error);
                    }
                    if Instant::now() >= deadline {
                        break;
                    }
                }
            }
        }

        timeout *= 2;
    }

    Err(io::Error::new(
        io::ErrorKind::TimedOut,
        "no STUN Binding response",
    ))
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

    fn transaction_id() -> [u8; TRANSACTION_ID_SIZE] {
        let bytes = hex("b7e7a701bc34d686fa87dfae");
        bytes.as_slice().try_into().expect("12 bytes")
    }

    #[test]
    fn crc32_matches_the_standard_check_value() {
        assert_eq!(crc32_ieee(b"123456789"), 0xCBF4_3926);
        assert_eq!(crc32_ieee(b""), 0);
    }

    #[test]
    fn binding_request_has_the_standard_header() {
        let id = transaction_id();
        let request = encode_request(&id);
        assert_eq!(request.len(), HEADER_SIZE);
        assert_eq!(
            u16::from_be_bytes([request[0], request[1]]),
            BINDING_REQUEST
        );
        assert_eq!(u16::from_be_bytes([request[2], request[3]]), 0);
        assert_eq!(
            u32::from_be_bytes([request[4], request[5], request[6], request[7]]),
            MAGIC_COOKIE
        );
        assert_eq!(request[8..20], id[..]);
    }

    #[test]
    fn xor_mapped_address_roundtrips_for_ipv4_and_ipv6() {
        let id = transaction_id();
        for text in ["203.0.113.7:54321", "[2001:db8::1]:1234"] {
            let address: SocketAddr = text.parse().expect("address");
            let response = encode_success(&id, address).expect("encode");
            let decoded = decode_success(&response, &id, address, address).expect("decode");
            assert_eq!(decoded, address, "{text}");
        }
    }

    #[test]
    fn xor_address_value_layout_is_exact() {
        let id = transaction_id();
        let address: SocketAddr = "192.0.2.1:32853".parse().expect("address");
        let value = encode_xor_address(&id, address);
        assert_eq!(value[0], 0);
        assert_eq!(value[1], 1);
        assert_eq!(
            u16::from_be_bytes([value[2], value[3]]),
            32853u16 ^ ((MAGIC_COOKIE >> 16) as u16)
        );
        let mask = xor_mask(&id);
        assert_eq!(value[4], 192 ^ mask[0]);
        assert_eq!(value[5], mask[1]);
        assert_eq!(value[6], 2 ^ mask[2]);
        assert_eq!(value[7], 1 ^ mask[3]);
    }

    #[test]
    fn fingerprint_must_be_last_and_verify() {
        let id = transaction_id();
        let mut message = encode(
            BINDING_REQUEST,
            &id,
            &[StunAttribute {
                attr_type: XOR_MAPPED_ADDRESS,
                value: vec![0, 1, 0x12, 0x34, 1, 2, 3, 4],
            }],
        )
        .expect("encode");

        let fingerprint_offset = message.len();
        message.extend_from_slice(&FINGERPRINT.to_be_bytes());
        message.extend_from_slice(&4u16.to_be_bytes());
        message.extend_from_slice(&0u32.to_be_bytes());

        // The length field is part of the hashed prefix, so patch it before the CRC.
        let length = (message.len() - HEADER_SIZE) as u16;
        message[2..4].copy_from_slice(&length.to_be_bytes());
        let crc = crc32_ieee(&message[..fingerprint_offset]) ^ FINGERPRINT_XOR;
        message[fingerprint_offset + 4..fingerprint_offset + 8].copy_from_slice(&crc.to_be_bytes());

        let parsed = parse(&message).expect("fingerprint must verify");
        assert_eq!(parsed.attributes.len(), 2);

        let mut corrupted = message.clone();
        let last = corrupted.len() - 1;
        corrupted[last] ^= 0xff;
        assert_eq!(parse(&corrupted), Err(StunError::BadFingerprint));
    }

    #[test]
    fn malformed_messages_are_rejected() {
        let id = transaction_id();
        let request = encode_request(&id);

        assert_eq!(parse(&request[..19]), Err(StunError::TruncatedHeader));

        let mut bad_cookie = request.clone();
        bad_cookie[4] = 0;
        assert_eq!(parse(&bad_cookie), Err(StunError::BadCookie));

        let mut bad_type = request.clone();
        bad_type[0] = 0xC0;
        assert_eq!(parse(&bad_type), Err(StunError::InvalidType));

        let mut bad_length = request.clone();
        bad_length[2..4].copy_from_slice(&4u16.to_be_bytes());
        assert_eq!(parse(&bad_length), Err(StunError::BadLength));

        let mut trailing = request;
        trailing.push(0);
        assert_eq!(parse(&trailing), Err(StunError::BadLength));
    }

    #[test]
    fn truncated_attribute_value_is_rejected() {
        let id = transaction_id();
        // Header declares a single 4-byte attribute header, but that header claims an
        // 8-byte value that is not present.
        let mut message = vec![0u8; HEADER_SIZE + 4];
        message[0..2].copy_from_slice(&BINDING_REQUEST.to_be_bytes());
        message[2..4].copy_from_slice(&4u16.to_be_bytes());
        message[4..8].copy_from_slice(&MAGIC_COOKIE.to_be_bytes());
        message[8..20].copy_from_slice(&id);
        message[20..22].copy_from_slice(&XOR_MAPPED_ADDRESS.to_be_bytes());
        message[22..24].copy_from_slice(&8u16.to_be_bytes());
        assert_eq!(parse(&message), Err(StunError::TruncatedAttribute));

        // A length that is not four-byte aligned is rejected before any attribute parsing.
        message[2..4].copy_from_slice(&5u16.to_be_bytes());
        assert_eq!(parse(&message), Err(StunError::BadLength));
    }

    #[test]
    fn response_from_the_wrong_source_is_rejected() {
        let id = transaction_id();
        let mapped: SocketAddr = "198.51.100.9:7000".parse().expect("address");
        let response = encode_success(&id, mapped).expect("encode");
        let server: SocketAddr = "192.0.2.1:3478".parse().expect("address");
        let attacker: SocketAddr = "192.0.2.2:3478".parse().expect("address");
        assert_eq!(
            decode_success(&response, &id, attacker, server),
            Err(StunError::UnexpectedSource)
        );
        assert_eq!(decode_success(&response, &id, server, server), Ok(mapped));
    }

    #[test]
    fn wrong_transaction_or_type_is_rejected() {
        let id = transaction_id();
        let mapped: SocketAddr = "198.51.100.9:7000".parse().expect("address");
        let response = encode_success(&id, mapped).expect("encode");
        let other = [0u8; TRANSACTION_ID_SIZE];
        assert_eq!(
            decode_success(&response, &other, mapped, mapped),
            Err(StunError::UnexpectedTransaction)
        );

        let mut wrong_type = response.clone();
        wrong_type[0] = 0x01;
        wrong_type[1] = 0x11;
        assert_eq!(
            decode_success(&wrong_type, &id, mapped, mapped),
            Err(StunError::UnexpectedTransaction)
        );
    }

    #[test]
    fn duplicate_xor_mapped_address_is_rejected() {
        let id = transaction_id();
        let mapped: SocketAddr = "198.51.100.9:7000".parse().expect("address");
        let value = encode_xor_address(&id, mapped);
        let response = encode(
            BINDING_SUCCESS,
            &id,
            &[
                StunAttribute {
                    attr_type: XOR_MAPPED_ADDRESS,
                    value: value.clone(),
                },
                StunAttribute {
                    attr_type: XOR_MAPPED_ADDRESS,
                    value,
                },
            ],
        )
        .expect("encode");
        assert_eq!(
            decode_success(&response, &id, mapped, mapped),
            Err(StunError::BadXorAddress)
        );
    }

    #[test]
    fn unsupported_required_attribute_produces_a_420_error() {
        let id = transaction_id();
        let request = encode(
            BINDING_REQUEST,
            &id,
            &[StunAttribute {
                attr_type: 0x0003, // CHANGE-REQUEST, unsupported here
                value: vec![0, 0, 0, 0],
            }],
        )
        .expect("encode");

        let source: SocketAddr = "192.0.2.55:1234".parse().expect("address");
        let response = create_binding_response(&request, source)
            .expect("encode")
            .expect("a Binding request always gets a response");

        let parsed = parse(&response).expect("parse");
        assert_eq!(parsed.message_type, BINDING_ERROR);
        let error = parsed.attribute(ERROR_CODE).expect("ERROR-CODE");
        assert_eq!(error.value[2], 4);
        assert_eq!(error.value[3], 20);
        assert_eq!(&error.value[4..], b"Unknown Attribute");
        let unknown = parsed
            .attribute(UNKNOWN_ATTRIBUTES)
            .expect("UNKNOWN-ATTRIBUTES");
        assert_eq!(unknown.value, vec![0x00, 0x03]);
    }

    #[test]
    fn non_binding_messages_produce_no_response() {
        let id = transaction_id();
        let response = encode(BINDING_SUCCESS, &id, &[]).expect("encode");
        let source: SocketAddr = "192.0.2.55:1234".parse().expect("address");
        assert_eq!(create_binding_response(&response, source), Ok(None));
    }

    #[test]
    fn ipv4_mapped_source_is_reported_as_ipv4() {
        let id = transaction_id();
        let mapped: SocketAddr = "[::ffff:192.0.2.1]:5000".parse().expect("address");
        let response = encode_success(&id, mapped).expect("encode");
        let decoded = decode_success(&response, &id, mapped, mapped).expect("decode");
        assert!(decoded.is_ipv4());
        assert_eq!(decoded.port(), 5000);
    }
}
