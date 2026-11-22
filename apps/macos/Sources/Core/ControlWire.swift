//
//  ControlWire.swift
//  Byte-exact control event bodies.
//
//  This mirrors `src/ScreenCast.Windows/Input/InputCodec.cs` field for field. Every body is
//  fixed-length and big-endian so the peer can validate it without parsing an untrusted
//  variable structure. Getting a byte wrong here means the Windows host silently ignores the
//  event (the decoder validates the length and returns false), so the sizes below are the
//  contract:
//
//      Key         scanCode(2) flags(1)        flags bit0 = extended, bit1 = down
//      MouseMove   x(2) y(2) monitorId(2)      all normalized 0..65535
//      MouseButton button(1) down(1)
//      Wheel       deltaX(2 signed) deltaY(2 signed), 120 units per notch
//      Touch       contactId(2) phase(1) x(2) y(2)
//

import Foundation

/// Control event types, identical to `ControlCodec.ControlEventType`.
enum ControlEventType: UInt16 {
    case key = 1
    case mouseMove = 2
    case mouseButton = 3
    case wheel = 4
    case touch = 5
    case releaseAll = 6
    case requestKeyFrame = 7
    case setQuality = 8
    case pathSwitch = 9
    case controlLease = 10
}

/// Mouse button identifiers used on the wire.
enum MouseButtonID: UInt8 {
    case left = 0
    case right = 1
    case middle = 2
}

/// Touch phases used on the wire.
enum TouchPhase: UInt8 {
    case down = 0
    case move = 1
    case up = 2
    case cancel = 3
}

enum ControlWire {
    static let keyBodySize = 3
    static let mouseMoveBodySize = 6
    static let mouseButtonBodySize = 2
    static let wheelBodySize = 4
    static let touchBodySize = 7

    // MARK: - Encoding (viewer -> host)

    static func key(scanCode: UInt16, extended: Bool, isDown: Bool) -> [UInt8] {
        [
            UInt8((scanCode >> 8) & 0xFF),
            UInt8(scanCode & 0xFF),
            (extended ? 1 : 0) | (isDown ? 2 : 0),
        ]
    }

    static func mouseMove(x: UInt16, y: UInt16, monitorID: UInt16 = 0) -> [UInt8] {
        [
            UInt8((x >> 8) & 0xFF), UInt8(x & 0xFF),
            UInt8((y >> 8) & 0xFF), UInt8(y & 0xFF),
            UInt8((monitorID >> 8) & 0xFF), UInt8(monitorID & 0xFF),
        ]
    }

    static func mouseButton(_ button: MouseButtonID, isDown: Bool) -> [UInt8] {
        [button.rawValue, isDown ? 1 : 0]
    }

    static func wheel(deltaX: Int16, deltaY: Int16) -> [UInt8] {
        let ux = UInt16(bitPattern: deltaX)
        let uy = UInt16(bitPattern: deltaY)
        return [
            UInt8((ux >> 8) & 0xFF), UInt8(ux & 0xFF),
            UInt8((uy >> 8) & 0xFF), UInt8(uy & 0xFF),
        ]
    }

    static func touch(contactID: UInt16, phase: TouchPhase, x: UInt16, y: UInt16) -> [UInt8] {
        [
            UInt8((contactID >> 8) & 0xFF), UInt8(contactID & 0xFF),
            phase.rawValue,
            UInt8((x >> 8) & 0xFF), UInt8(x & 0xFF),
            UInt8((y >> 8) & 0xFF), UInt8(y & 0xFF),
        ]
    }

    /// One byte: 1 = acquire, 0 = release.
    static func lease(acquire: Bool) -> [UInt8] {
        [acquire ? 1 : 0]
    }

    // MARK: - Decoding (host side, with the same validation as TryDecode*)

    struct KeyEvent {
        let scanCode: UInt16
        let extended: Bool
        let isDown: Bool
    }

    static func decodeKey(_ body: [UInt8]) -> KeyEvent? {
        guard body.count == keyBodySize else { return nil }
        let scanCode = UInt16(body[0]) << 8 | UInt16(body[1])
        return KeyEvent(
            scanCode: scanCode,
            extended: (body[2] & 1) != 0,
            isDown: (body[2] & 2) != 0)
    }

    static func decodeMouseMove(_ body: [UInt8]) -> (x: UInt16, y: UInt16, monitorID: UInt16)? {
        guard body.count == mouseMoveBodySize else { return nil }
        let x = UInt16(body[0]) << 8 | UInt16(body[1])
        let y = UInt16(body[2]) << 8 | UInt16(body[3])
        let monitor = UInt16(body[4]) << 8 | UInt16(body[5])
        return (x, y, monitor)
    }

    static func decodeMouseButton(_ body: [UInt8]) -> (button: MouseButtonID, isDown: Bool)? {
        guard body.count == mouseButtonBodySize, body[1] <= 1 else { return nil }
        guard let button = MouseButtonID(rawValue: body[0]) else { return nil }
        return (button, body[1] != 0)
    }

    static func decodeWheel(_ body: [UInt8]) -> (deltaX: Int16, deltaY: Int16)? {
        guard body.count == wheelBodySize else { return nil }
        let ux = UInt16(body[0]) << 8 | UInt16(body[1])
        let uy = UInt16(body[2]) << 8 | UInt16(body[3])
        return (Int16(bitPattern: ux), Int16(bitPattern: uy))
    }

    // MARK: - Normalized coordinates

    /// Map a pixel position inside a surface to the 0..65535 normalized range.
    /// Identical rounding to `InputCodec.Normalize`.
    static func normalize(_ position: Double, extent: Double) -> UInt16 {
        guard extent > 1 else { return 0 }
        let ratio = min(max(position / (extent - 1), 0), 1)
        return UInt16((ratio * Double(UInt16.max)).rounded())
    }

    /// Map a normalized 0..65535 coordinate back to a pixel position inside a surface.
    static func denormalize(_ normalized: UInt16, extent: Double) -> Double {
        guard extent > 1 else { return 0 }
        return Double(normalized) / Double(UInt16.max) * (extent - 1)
    }

    /// Map a normalized coordinate to a point offset inside a rect (host side, in points).
    static func denormalize(_ normalized: UInt16, into origin: Double, extent: Double) -> Double {
        guard extent > 0 else { return origin }
        return origin + Double(normalized) / Double(UInt16.max) * extent
    }
}
