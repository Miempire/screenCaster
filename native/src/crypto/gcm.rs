//! AES-256-GCM (NIST SP 800-38D) with a 96-bit nonce and a 128-bit tag.
//!
//! The ScreenCast wire format is fixed: a 12-byte nonce (epoch || packet sequence), the
//! first 48 header bytes as additional authenticated data, and a 16-byte tag stored at
//! offset 48 of the datagram. This module implements exactly that shape so the caller
//! never has to assemble a nonce by hand.

use super::aes::{Aes256, BLOCK, KEY};

/// GCM authentication tag length in bytes.
pub const TAG: usize = 16;
/// GCM nonce length in bytes.
pub const NONCE: usize = 12;

/// Reduction constant for GF(2^128) multiplication in the GCM bit order.
const R: u128 = 0xe100_0000_0000_0000_0000_0000_0000_0000;

/// Multiply two blocks in GF(2^128) using the GCM convention (SP 800-38D, Algorithm 1).
fn gf_mul(x: u128, y: u128) -> u128 {
    let mut z: u128 = 0;
    let mut v = y;
    for bit in 0..128 {
        if (x >> (127 - bit)) & 1 == 1 {
            z ^= v;
        }
        v = if v & 1 == 0 { v >> 1 } else { (v >> 1) ^ R };
    }
    z
}

/// GHASH over AAD and ciphertext, including the trailing length block.
fn ghash(h: u128, aad: &[u8], ciphertext: &[u8]) -> u128 {
    let mut accumulator: u128 = 0;
    for chunk in aad.chunks(BLOCK) {
        let mut block = [0u8; BLOCK];
        block[..chunk.len()].copy_from_slice(chunk);
        accumulator = gf_mul(accumulator ^ u128::from_be_bytes(block), h);
    }
    for chunk in ciphertext.chunks(BLOCK) {
        let mut block = [0u8; BLOCK];
        block[..chunk.len()].copy_from_slice(chunk);
        accumulator = gf_mul(accumulator ^ u128::from_be_bytes(block), h);
    }
    let length_block = (((aad.len() as u64).wrapping_mul(8) as u128) << 64)
        | (ciphertext.len() as u64).wrapping_mul(8) as u128;
    gf_mul(accumulator ^ length_block, h)
}

/// Constant-time tag comparison; never branch on a tag byte.
fn tags_equal(left: &[u8; TAG], right: &[u8; TAG]) -> bool {
    let mut difference = 0u8;
    for index in 0..TAG {
        difference |= left[index] ^ right[index];
    }
    difference == 0
}

/// AES-256-GCM with a precomputed hash subkey.
pub struct AesGcm256 {
    cipher: Aes256,
    hash_subkey: u128,
}

impl AesGcm256 {
    pub fn new(key: &[u8; KEY]) -> Self {
        let cipher = Aes256::new(key);
        let mut h = [0u8; BLOCK];
        cipher.encrypt_block(&mut h);
        Self {
            cipher,
            hash_subkey: u128::from_be_bytes(h),
        }
    }

    /// Encrypt `plaintext` into `ciphertext_out` and write the tag.
    ///
    /// `ciphertext_out` must be exactly as long as the plaintext.
    pub fn seal(
        &self,
        nonce: &[u8; NONCE],
        aad: &[u8],
        plaintext: &[u8],
        ciphertext_out: &mut [u8],
        tag_out: &mut [u8; TAG],
    ) {
        debug_assert_eq!(plaintext.len(), ciphertext_out.len());
        ciphertext_out.copy_from_slice(plaintext);
        self.counter_mode(nonce, 2, ciphertext_out);

        let s = ghash(self.hash_subkey, aad, ciphertext_out);
        let mut mask = [0u8; BLOCK];
        mask[..NONCE].copy_from_slice(nonce);
        mask[12..].copy_from_slice(&1u32.to_be_bytes());
        self.cipher.encrypt_block(&mut mask);
        let encrypted = u128::from_be_bytes(mask);
        tag_out.copy_from_slice(&(encrypted ^ s).to_be_bytes());
    }

    /// Verify and decrypt. Returns `false` on any authentication failure; `plaintext_out`
    /// is left zeroed in that case.
    pub fn open(
        &self,
        nonce: &[u8; NONCE],
        aad: &[u8],
        ciphertext: &[u8],
        tag: &[u8; TAG],
        plaintext_out: &mut [u8],
    ) -> bool {
        if plaintext_out.len() != ciphertext.len() {
            return false;
        }

        let s = ghash(self.hash_subkey, aad, ciphertext);
        let mut mask = [0u8; BLOCK];
        mask[..NONCE].copy_from_slice(nonce);
        mask[12..].copy_from_slice(&1u32.to_be_bytes());
        self.cipher.encrypt_block(&mut mask);
        let expected = (u128::from_be_bytes(mask) ^ s).to_be_bytes();

        if !tags_equal(&expected, tag) {
            plaintext_out.fill(0);
            return false;
        }

        plaintext_out.copy_from_slice(ciphertext);
        self.counter_mode(nonce, 2, plaintext_out);
        true
    }

    /// GCTR with the 32-bit counter starting at `initial_counter`.
    fn counter_mode(&self, nonce: &[u8; NONCE], initial_counter: u32, data: &mut [u8]) {
        let mut counter = initial_counter;
        for chunk in data.chunks_mut(BLOCK) {
            let mut keystream = [0u8; BLOCK];
            keystream[..NONCE].copy_from_slice(nonce);
            keystream[12..].copy_from_slice(&counter.to_be_bytes());
            self.cipher.encrypt_block(&mut keystream);
            for (byte, key) in chunk.iter_mut().zip(keystream.iter()) {
                *byte ^= *key;
            }
            counter = counter.wrapping_add(1);
        }
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

    /// NIST GCM test vector for AES-256: all-zero key, all-zero 96-bit IV, no AAD,
    /// one all-zero block of plaintext.
    #[test]
    fn nist_zero_key_zero_iv_vector() {
        let gcm = AesGcm256::new(&[0u8; 32]);
        let nonce = [0u8; NONCE];
        let mut ciphertext = [0u8; 16];
        let mut tag = [0u8; TAG];
        gcm.seal(&nonce, &[], &[0u8; 16], &mut ciphertext, &mut tag);
        assert_eq!(
            to_hex(&ciphertext),
            "cea7403d4d606b6e074ec5d3baf39d18",
            "AES-256-GCM zero-block ciphertext"
        );
        assert_eq!(
            to_hex(&tag),
            "d0d1c8a799996bf0265b98b5d48ab919",
            "AES-256-GCM zero-block tag"
        );
    }

    #[test]
    fn roundtrip_with_aad_and_partial_blocks() {
        let gcm = AesGcm256::new(&[7u8; 32]);
        let nonce = [3u8; NONCE];
        let aad = hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f");
        for length in 0..70usize {
            let plaintext: Vec<u8> = (0..length).map(|value| (value * 13 % 256) as u8).collect();
            let mut ciphertext = vec![0u8; length];
            let mut tag = [0u8; TAG];
            gcm.seal(&nonce, &aad, &plaintext, &mut ciphertext, &mut tag);
            if length > 0 {
                assert_ne!(ciphertext, plaintext, "ciphertext must differ at {length}");
            }

            let mut recovered = vec![0u8; length];
            assert!(gcm.open(&nonce, &aad, &ciphertext, &tag, &mut recovered));
            assert_eq!(recovered, plaintext, "roundtrip at {length}");
        }
    }

    #[test]
    fn every_tag_bit_and_every_aad_byte_is_authenticated() {
        let gcm = AesGcm256::new(&[9u8; 32]);
        let nonce = [1u8; NONCE];
        let aad = [0x5au8; 48];
        let plaintext = b"authenticated payload";
        let mut ciphertext = vec![0u8; plaintext.len()];
        let mut tag = [0u8; TAG];
        gcm.seal(&nonce, &aad, plaintext, &mut ciphertext, &mut tag);

        for bit in 0..128 {
            let mut tampered = tag;
            tampered[bit / 8] ^= 1 << (bit % 8);
            let mut out = vec![0u8; plaintext.len()];
            assert!(!gcm.open(&nonce, &aad, &ciphertext, &tampered, &mut out));
        }

        for index in 0..aad.len() {
            let mut tampered = aad;
            tampered[index] ^= 0x01;
            let mut out = vec![0u8; plaintext.len()];
            assert!(!gcm.open(&nonce, &tampered, &ciphertext, &tag, &mut out));
        }

        let mut tampered_ciphertext = ciphertext.clone();
        tampered_ciphertext[0] ^= 0x80;
        let mut out = vec![0u8; plaintext.len()];
        assert!(!gcm.open(&nonce, &aad, &tampered_ciphertext, &tag, &mut out));
    }
}
