//! AES-256 block encryption (FIPS-197), written as a dependency-free byte-oriented
//! reference implementation.
//!
//! The project deliberately keeps the Rust core free of third-party crates so the same
//! source compiles for Windows, macOS, Android and iOS without a C toolchain, a build
//! script or a network fetch. The block cipher is the only part that needs to be fast
//! enough for real traffic, and the media path only ever encrypts 1104-byte fragments,
//! so a compact implementation is sufficient.
//!
//! S-box and multiplication tables are generated at compile time from their algebraic
//! definitions (GF(2^8) inverse + affine transform) instead of being transcribed, which
//! removes the single most common source of silent AES table typos.

/// AES block size in bytes.
pub const BLOCK: usize = 16;
/// AES-256 key size in bytes.
pub const KEY: usize = 32;
/// AES-256 round count.
const ROUNDS: usize = 14;

/// Multiplication in GF(2^8) modulo x^8 + x^4 + x^3 + x + 1.
const fn gf_mul(mut a: u8, mut b: u8) -> u8 {
    let mut product: u8 = 0;
    let mut bit = 0;
    while bit < 8 {
        if b & 1 != 0 {
            product ^= a;
        }
        let high = a & 0x80;
        a <<= 1;
        if high != 0 {
            a ^= 0x1b;
        }
        b >>= 1;
        bit += 1;
    }
    product
}

/// Multiplicative inverse in GF(2^8); zero maps to zero, as the S-box definition requires.
const fn gf_inv(x: u8) -> u8 {
    if x == 0 {
        return 0;
    }
    let mut candidate: u8 = 1;
    loop {
        if gf_mul(x, candidate) == 1 {
            return candidate;
        }
        candidate = candidate.wrapping_add(1);
    }
}

/// S(a) = b ^ rotl(b,1) ^ rotl(b,2) ^ rotl(b,3) ^ rotl(b,4) ^ 0x63, b = a^-1.
const fn build_sbox() -> [u8; 256] {
    let mut table = [0u8; 256];
    let mut index = 0;
    while index < 256 {
        let b = gf_inv(index as u8);
        table[index] =
            b ^ b.rotate_left(1) ^ b.rotate_left(2) ^ b.rotate_left(3) ^ b.rotate_left(4) ^ 0x63;
        index += 1;
    }
    table
}

const SBOX: [u8; 256] = build_sbox();

/// Multiply by x (i.e. by 2) in GF(2^8).
const fn build_mul2() -> [u8; 256] {
    let mut table = [0u8; 256];
    let mut index = 0;
    while index < 256 {
        let value = index as u8;
        let high = value & 0x80;
        let doubled = value << 1;
        table[index] = if high != 0 { doubled ^ 0x1b } else { doubled };
        index += 1;
    }
    table
}

const MUL2: [u8; 256] = build_mul2();

/// Round constants for the AES-256 key schedule; only i % 8 == 0 uses them.
const RCON: [u8; 8] = [0x00, 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40];

fn sub_word(word: [u8; 4]) -> [u8; 4] {
    [
        SBOX[word[0] as usize],
        SBOX[word[1] as usize],
        SBOX[word[2] as usize],
        SBOX[word[3] as usize],
    ]
}

/// AES-256 with an expanded key schedule. The key material is zeroed on drop.
pub struct Aes256 {
    /// 15 round keys of 16 bytes; round r occupies `round_keys[r]`.
    round_keys: [[u8; BLOCK]; ROUNDS + 1],
}

impl Aes256 {
    /// Expand a 32-byte key into the 15 AES-256 round keys.
    pub fn new(key: &[u8; KEY]) -> Self {
        // Nk = 8 words, Nr = 14 rounds, so 60 schedule words.
        let mut words = [[0u8; 4]; 60];
        let mut index = 0;
        while index < 8 {
            words[index] = [
                key[4 * index],
                key[4 * index + 1],
                key[4 * index + 2],
                key[4 * index + 3],
            ];
            index += 1;
        }
        while index < 60 {
            let mut temp = words[index - 1];
            if index % 8 == 0 {
                temp = sub_word([temp[1], temp[2], temp[3], temp[0]]);
                temp[0] ^= RCON[index / 8];
            } else if index % 8 == 4 {
                temp = sub_word(temp);
            }
            let previous = words[index - 8];
            words[index] = [
                previous[0] ^ temp[0],
                previous[1] ^ temp[1],
                previous[2] ^ temp[2],
                previous[3] ^ temp[3],
            ];
            index += 1;
        }

        let mut round_keys = [[0u8; BLOCK]; ROUNDS + 1];
        for (round, round_key) in round_keys.iter_mut().enumerate() {
            for (word_index, word) in round_key.chunks_mut(4).enumerate() {
                word.copy_from_slice(&words[4 * round + word_index]);
            }
        }
        Self { round_keys }
    }

    /// Encrypt one block in place.
    pub fn encrypt_block(&self, block: &mut [u8; BLOCK]) {
        add_round_key(block, &self.round_keys[0]);
        for round in 1..ROUNDS {
            sub_bytes(block);
            shift_rows(block);
            mix_columns(block);
            add_round_key(block, &self.round_keys[round]);
        }
        sub_bytes(block);
        shift_rows(block);
        add_round_key(block, &self.round_keys[ROUNDS]);
    }
}

impl Drop for Aes256 {
    fn drop(&mut self) {
        for round_key in self.round_keys.iter_mut() {
            round_key.fill(0);
        }
    }
}

fn add_round_key(state: &mut [u8; BLOCK], round_key: &[u8; BLOCK]) {
    for (byte, key) in state.iter_mut().zip(round_key.iter()) {
        *byte ^= *key;
    }
}

fn sub_bytes(state: &mut [u8; BLOCK]) {
    for byte in state.iter_mut() {
        *byte = SBOX[*byte as usize];
    }
}

/// Row r rotates left by r. The state is column-major: byte index 4*c + r is row r, column c.
fn shift_rows(state: &mut [u8; BLOCK]) {
    let previous = *state;
    for row in 0..4 {
        for column in 0..4 {
            state[4 * column + row] = previous[4 * ((column + row) % 4) + row];
        }
    }
}

fn mul3(value: u8) -> u8 {
    MUL2[value as usize] ^ value
}

fn mix_columns(state: &mut [u8; BLOCK]) {
    for column in 0..4 {
        let offset = 4 * column;
        let a0 = state[offset];
        let a1 = state[offset + 1];
        let a2 = state[offset + 2];
        let a3 = state[offset + 3];
        state[offset] = MUL2[a0 as usize] ^ mul3(a1) ^ a2 ^ a3;
        state[offset + 1] = a0 ^ MUL2[a1 as usize] ^ mul3(a2) ^ a3;
        state[offset + 2] = a0 ^ a1 ^ MUL2[a2 as usize] ^ mul3(a3);
        state[offset + 3] = mul3(a0) ^ a1 ^ a2 ^ MUL2[a3 as usize];
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

    #[test]
    fn sbox_matches_fips_197_first_entries() {
        assert_eq!(SBOX[0x00], 0x63);
        assert_eq!(SBOX[0x01], 0x7c);
        assert_eq!(SBOX[0x02], 0x77);
        assert_eq!(SBOX[0x0f], 0x76);
        assert_eq!(SBOX[0x53], 0xed);
        assert_eq!(SBOX[0xff], 0x16);
        // The S-box is a permutation: every byte appears exactly once.
        let mut seen = [false; 256];
        for value in SBOX {
            assert!(!seen[value as usize], "duplicate S-box entry");
            seen[value as usize] = true;
        }
    }

    #[test]
    fn gf_inverse_round_trips() {
        for value in 1u16..=255 {
            let value = value as u8;
            assert_eq!(gf_mul(value, gf_inv(value)), 1, "inverse of {value}");
        }
    }

    #[test]
    fn aes256_fips_197_appendix_c3_vector() {
        let key = hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        let mut block = [0u8; BLOCK];
        block.copy_from_slice(&hex("00112233445566778899aabbccddeeff"));
        let cipher = Aes256::new(key.as_slice().try_into().expect("32-byte key"));
        cipher.encrypt_block(&mut block);
        assert_eq!(hex("8ea2b7ca516745bfeafc49904b496089"), block.to_vec());
    }

    #[test]
    fn aes256_fips_197_appendix_c1_round_zero_key() {
        // All-zero key/plaintext is a cheap structural check of the schedule.
        let cipher = Aes256::new(&[0u8; KEY]);
        let mut block = [0u8; BLOCK];
        cipher.encrypt_block(&mut block);
        assert_eq!(
            hex("dc95c078a2408989ad48a21492842087"),
            block.to_vec(),
            "AES-256 of an all-zero block under an all-zero key"
        );
    }
}
