//! Dependency-free cryptographic primitives used by the ScreenCast wire protocol.
//!
//! Nothing here is a general-purpose crypto library: the module exposes exactly the
//! algorithms the protocol needs (AES-256-GCM, SHA-256, HMAC-SHA256, HKDF-SHA256) and
//! pins each one against published known-answer vectors.

pub mod aes;
pub mod gcm;
pub mod rand;
pub mod sha256;

pub use aes::{Aes256, BLOCK as AES_BLOCK, KEY as AES_KEY};
pub use gcm::{AesGcm256, NONCE as GCM_NONCE, TAG as GCM_TAG};
pub use sha256::{hkdf, hmac_sha256, sha256, Sha256, DIGEST as SHA256_DIGEST};
