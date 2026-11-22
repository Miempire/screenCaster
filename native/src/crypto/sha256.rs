//! SHA-256 (FIPS 180-4) plus HMAC-SHA256 and HKDF-SHA256 (RFC 2104 / RFC 5869).
//!
//! ScreenCast derives every direction-scoped media key with HKDF-SHA256, so these
//! primitives must match the .NET `HKDF.DeriveKey(HashAlgorithmName.SHA256, ...)` and
//! `HMACSHA256` implementations bit for bit. The tests pin the published vectors.

/// SHA-256 digest size in bytes.
pub const DIGEST: usize = 32;
/// SHA-256 internal block size in bytes.
pub const BLOCK: usize = 64;

const K: [u32; 64] = [
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
];

const INITIAL: [u32; 8] = [
    0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
];

/// Incremental SHA-256 state.
#[derive(Clone)]
pub struct Sha256 {
    state: [u32; 8],
    buffer: [u8; BLOCK],
    buffered: usize,
    total_bytes: u64,
}

impl Default for Sha256 {
    fn default() -> Self {
        Self::new()
    }
}

impl Sha256 {
    pub fn new() -> Self {
        Self {
            state: INITIAL,
            buffer: [0u8; BLOCK],
            buffered: 0,
            total_bytes: 0,
        }
    }

    pub fn update(&mut self, mut data: &[u8]) {
        self.total_bytes = self.total_bytes.wrapping_add(data.len() as u64);

        if self.buffered > 0 {
            let needed = BLOCK - self.buffered;
            let take = needed.min(data.len());
            self.buffer[self.buffered..self.buffered + take].copy_from_slice(&data[..take]);
            self.buffered += take;
            data = &data[take..];
            if self.buffered == BLOCK {
                let block = self.buffer;
                self.compress(&block);
                self.buffered = 0;
            }
        }

        while data.len() >= BLOCK {
            let mut block = [0u8; BLOCK];
            block.copy_from_slice(&data[..BLOCK]);
            self.compress(&block);
            data = &data[BLOCK..];
        }

        if !data.is_empty() {
            self.buffer[..data.len()].copy_from_slice(data);
            self.buffered = data.len();
        }
    }

    pub fn finalize(mut self) -> [u8; DIGEST] {
        let bit_length = self.total_bytes.wrapping_mul(8);
        let mut padding = [0u8; BLOCK * 2];
        padding[0] = 0x80;
        // Message length must occupy the last 8 bytes of a block.
        let pad_len = if self.buffered < 56 {
            56 - self.buffered
        } else {
            BLOCK + 56 - self.buffered
        };
        self.update(&padding[..pad_len]);
        self.total_bytes = self.total_bytes.wrapping_sub(pad_len as u64);
        self.update(&bit_length.to_be_bytes());
        self.total_bytes = self.total_bytes.wrapping_sub(8);

        let mut digest = [0u8; DIGEST];
        for (chunk, word) in digest.chunks_mut(4).zip(self.state.iter()) {
            chunk.copy_from_slice(&word.to_be_bytes());
        }
        digest
    }

    fn compress(&mut self, block: &[u8; BLOCK]) {
        let mut schedule = [0u32; 64];
        for (index, chunk) in block.chunks(4).enumerate() {
            schedule[index] = u32::from_be_bytes([chunk[0], chunk[1], chunk[2], chunk[3]]);
        }
        for index in 16..64 {
            let s0 = schedule[index - 15].rotate_right(7)
                ^ schedule[index - 15].rotate_right(18)
                ^ (schedule[index - 15] >> 3);
            let s1 = schedule[index - 2].rotate_right(17)
                ^ schedule[index - 2].rotate_right(19)
                ^ (schedule[index - 2] >> 10);
            schedule[index] = schedule[index - 16]
                .wrapping_add(s0)
                .wrapping_add(schedule[index - 7])
                .wrapping_add(s1);
        }

        let mut a = self.state[0];
        let mut b = self.state[1];
        let mut c = self.state[2];
        let mut d = self.state[3];
        let mut e = self.state[4];
        let mut f = self.state[5];
        let mut g = self.state[6];
        let mut h = self.state[7];

        for index in 0..64 {
            let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
            let choose = (e & f) ^ (!e & g);
            let temp1 = h
                .wrapping_add(s1)
                .wrapping_add(choose)
                .wrapping_add(K[index])
                .wrapping_add(schedule[index]);
            let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
            let majority = (a & b) ^ (a & c) ^ (b & c);
            let temp2 = s0.wrapping_add(majority);

            h = g;
            g = f;
            f = e;
            e = d.wrapping_add(temp1);
            d = c;
            c = b;
            b = a;
            a = temp1.wrapping_add(temp2);
        }

        self.state[0] = self.state[0].wrapping_add(a);
        self.state[1] = self.state[1].wrapping_add(b);
        self.state[2] = self.state[2].wrapping_add(c);
        self.state[3] = self.state[3].wrapping_add(d);
        self.state[4] = self.state[4].wrapping_add(e);
        self.state[5] = self.state[5].wrapping_add(f);
        self.state[6] = self.state[6].wrapping_add(g);
        self.state[7] = self.state[7].wrapping_add(h);
    }
}

/// One-shot SHA-256.
pub fn sha256(data: &[u8]) -> [u8; DIGEST] {
    let mut hasher = Sha256::new();
    hasher.update(data);
    hasher.finalize()
}

/// HMAC-SHA256 keyed hash (RFC 2104).
pub fn hmac_sha256(key: &[u8], data: &[u8]) -> [u8; DIGEST] {
    let mut normalized = [0u8; BLOCK];
    if key.len() > BLOCK {
        normalized[..DIGEST].copy_from_slice(&sha256(key));
    } else {
        normalized[..key.len()].copy_from_slice(key);
    }

    let mut inner_key = [0x36u8; BLOCK];
    let mut outer_key = [0x5cu8; BLOCK];
    for index in 0..BLOCK {
        inner_key[index] ^= normalized[index];
        outer_key[index] ^= normalized[index];
    }

    let mut inner = Sha256::new();
    inner.update(&inner_key);
    inner.update(data);
    let inner_digest = inner.finalize();

    let mut outer = Sha256::new();
    outer.update(&outer_key);
    outer.update(&inner_digest);
    outer.finalize()
}

/// HKDF-Extract: PRK = HMAC(salt, IKM).
pub fn hkdf_extract(salt: &[u8], ikm: &[u8]) -> [u8; DIGEST] {
    if salt.is_empty() {
        hmac_sha256(&[0u8; DIGEST], ikm)
    } else {
        hmac_sha256(salt, ikm)
    }
}

/// HKDF-Expand into `output`; panics only if the caller asks for more than 255 blocks.
pub fn hkdf_expand(prk: &[u8], info: &[u8], output: &mut [u8]) {
    assert!(
        output.len() <= 255 * DIGEST,
        "HKDF output exceeds the RFC 5869 limit"
    );

    // T(0) = empty, T(n) = HMAC(PRK, T(n-1) || info || n).
    let mut previous: [u8; DIGEST] = [0u8; DIGEST];
    let mut previous_length = 0usize;
    let mut written = 0;
    let mut counter: u8 = 1;
    while written < output.len() {
        let mut message = Vec::with_capacity(previous_length + info.len() + 1);
        message.extend_from_slice(&previous[..previous_length]);
        message.extend_from_slice(info);
        message.push(counter);
        previous = hmac_sha256(prk, &message);
        previous_length = DIGEST;

        let take = DIGEST.min(output.len() - written);
        output[written..written + take].copy_from_slice(&previous[..take]);
        written += take;
        counter += 1;
    }
}

/// HKDF-SHA256 extract-then-expand, matching .NET `HKDF.DeriveKey`.
pub fn hkdf(salt: &[u8], ikm: &[u8], info: &[u8], output: &mut [u8]) {
    let prk = hkdf_extract(salt, ikm);
    hkdf_expand(&prk, info, output);
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

    #[test]
    fn sha256_published_vectors() {
        assert_eq!(
            to_hex(&sha256(b"")),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
        assert_eq!(
            to_hex(&sha256(b"abc")),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        assert_eq!(
            to_hex(&sha256(
                b"abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"
            )),
            "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1"
        );
    }

    #[test]
    fn sha256_incremental_matches_one_shot_across_block_boundaries() {
        let data: Vec<u8> = (0..1000u32).map(|value| (value % 251) as u8).collect();
        let expected = sha256(&data);
        for chunk in 1..70 {
            let mut hasher = Sha256::new();
            for piece in data.chunks(chunk) {
                hasher.update(piece);
            }
            assert_eq!(hasher.finalize(), expected, "chunk size {chunk}");
        }
    }

    #[test]
    fn hmac_sha256_rfc4231_vectors() {
        // Test case 1.
        assert_eq!(
            to_hex(&hmac_sha256(&[0x0b; 20], b"Hi There")),
            "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7"
        );
        // Test case 2.
        assert_eq!(
            to_hex(&hmac_sha256(b"Jefe", b"what do ya want for nothing?")),
            "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843"
        );
        // Test case 6: key longer than the block size.
        let long_key = [0xaa; 131];
        assert_eq!(
            to_hex(&hmac_sha256(
                &long_key,
                b"Test Using Larger Than Block-Size Key - Hash Key First"
            )),
            "60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54"
        );
    }

    #[test]
    fn hkdf_rfc5869_test_case_one() {
        let ikm = hex("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b");
        let salt = hex("000102030405060708090a0b0c");
        let info = hex("f0f1f2f3f4f5f6f7f8f9");
        let mut output = [0u8; 42];
        hkdf(&salt, &ikm, &info, &mut output);
        assert_eq!(
            to_hex(&output),
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865"
        );
    }

    #[test]
    fn hkdf_rfc5869_test_case_three_empty_salt_and_info() {
        let ikm = hex("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b");
        let mut output = [0u8; 42];
        hkdf(&[], &ikm, &[], &mut output);
        assert_eq!(
            to_hex(&output),
            "8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8"
        );
    }
}
