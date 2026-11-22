//! Monotonic microsecond clock.
//!
//! Every timeout, RTT sample and fragment lifetime in this crate is expressed in
//! microseconds measured from one process-wide monotonic origin. No wall-clock time is
//! ever used for protocol decisions: the peers never need synchronised clocks.

use std::sync::OnceLock;
use std::time::Instant;

static ORIGIN: OnceLock<Instant> = OnceLock::new();

/// Microseconds elapsed since the first call in this process.
pub fn now_micros() -> i64 {
    let origin = ORIGIN.get_or_init(Instant::now);
    origin.elapsed().as_micros() as i64
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn time_moves_forward_and_starts_near_zero() {
        let first = now_micros();
        assert!(first >= 0);
        std::thread::sleep(std::time::Duration::from_millis(2));
        let second = now_micros();
        assert!(second > first, "{second} must be after {first}");
    }
}
