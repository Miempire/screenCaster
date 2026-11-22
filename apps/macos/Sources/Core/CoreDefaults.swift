//
//  CoreDefaults.swift
//  Pairing constants for the signalling-free MVP.
//
//  There is no signalling server in this build: both ends are handed the same 32-byte secret and
//  the same 16-byte session id by hand, exactly like the Windows app and the smoke tests do.
//  The Rust core never transmits the secret; it is only HKDF input.
//

import Foundation

enum CoreDefaults {
    /// 32 bytes `00 01 02 ... 1F`.
    static let sharedSecret: [UInt8] = (0...31).map { UInt8($0) }

    /// 16 bytes `A1 A2 ... B0`.
    static let sessionID: [UInt8] = (0...15).map { UInt8(0xA1 &+ $0) }

    /// Key epoch, must be >= 1.
    static let keyEpoch: UInt32 = 1

    /// Host-side media ports (the side that streams video).
    static let hostVideoPort = 42000
    static let hostControlPort = 42001

    /// Viewer-side media ports (the side that only sends control).
    static let viewerVideoPort = 42100
    static let viewerControlPort = 42101

    /// `sc_session_send_frame` flags.
    static let frameFlagKeyFrame: UInt8 = 1
    static let frameFlagConfiguration: UInt8 = 2
    static let frameFlagDiscontinuity: UInt8 = 4

    /// `sc_session_send_frame` codec ids.
    static let codecUnknown: UInt8 = 0
    static let codecH264: UInt8 = 1
    static let codecAV1: UInt8 = 2

    /// The Windows host uses config id 1 and never changes it in this build.
    static let configID: UInt32 = 1

    /// Encoder target used by the Host tab.
    static let defaultWidth = 1920
    static let defaultHeight = 1080
    static let defaultFPS = 30
    static let defaultBitrate = 8_000_000

    /// A viewer that drops a frame asks for a new key frame at most this often.
    static let keyFrameRequestInterval: CFAbsoluteTime = 0.5
}
