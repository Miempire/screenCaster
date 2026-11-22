//! Operating-system randomness for probe tokens, transaction ids and session ids.
//!
//! The core cannot take a `rand` dependency (the crate is intentionally dependency-free so
//! it builds for every platform without a network fetch), so this reads the platform
//! entropy source directly and falls back to a SHA-256 counter only if that fails.

use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use super::sha256::Sha256;

static FALLBACK_COUNTER: AtomicU64 = AtomicU64::new(0);

#[cfg(windows)]
mod platform {
    #[link(name = "advapi32")]
    extern "system" {
        #[link_name = "SystemFunction036"]
        fn rtl_gen_random(buffer: *mut u8, length: u32) -> u8;
    }

    pub fn fill(buffer: &mut [u8]) -> bool {
        if buffer.is_empty() {
            return true;
        }
        if buffer.len() > u32::MAX as usize {
            return false;
        }
        // SAFETY: the pointer and length describe exactly the caller's writable slice.
        let ok = unsafe { rtl_gen_random(buffer.as_mut_ptr(), buffer.len() as u32) };
        ok != 0
    }
}

#[cfg(unix)]
mod platform {
    use std::io::Read;

    pub fn fill(buffer: &mut [u8]) -> bool {
        match std::fs::File::open("/dev/urandom") {
            Ok(mut file) => file.read_exact(buffer).is_ok(),
            Err(_) => false,
        }
    }
}

#[cfg(not(any(windows, unix)))]
mod platform {
    pub fn fill(_buffer: &mut [u8]) -> bool {
        false
    }
}

/// Fill `buffer` with operating-system randomness. Returns false when the platform
/// entropy source is unavailable; the fallback still produces distinct values, but
/// callers that need unpredictability should treat `false` as a hard failure.
pub fn fill(buffer: &mut [u8]) -> bool {
    if platform::fill(buffer) {
        return true;
    }

    let counter = FALLBACK_COUNTER.fetch_add(1, Ordering::Relaxed);
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_nanos() as u64)
        .unwrap_or(0);
    let seed = buffer.as_ptr() as usize as u64;
    let mut hasher = Sha256::new();
    hasher.update(&nanos.to_be_bytes());
    hasher.update(&counter.to_be_bytes());
    hasher.update(&seed.to_be_bytes());
    let digest = hasher.finalize();
    for (chunk, byte) in buffer.chunks_mut(1).zip(digest.iter().cycle()) {
        chunk[0] = *byte;
    }
    false
}

/// 8 random bytes interpreted as a big-endian token.
pub fn u64() -> u64 {
    let mut bytes = [0u8; 8];
    fill(&mut bytes);
    u64::from_be_bytes(bytes)
}

/// 16 random bytes, used as a raw session id (never a Guid byte order).
pub fn session_id() -> [u8; 16] {
    let mut bytes = [0u8; 16];
    fill(&mut bytes);
    bytes
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn produces_distinct_values() {
        let first = u64();
        let second = u64();
        assert_ne!(first, second, "two tokens must not collide");
        let mut buffer = [0u8; 64];
        assert!(fill(&mut buffer) || buffer.iter().any(|byte| *byte != 0));
    }

    #[test]
    fn zero_length_is_accepted() {
        assert!(fill(&mut []));
    }
}
