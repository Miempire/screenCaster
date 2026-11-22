//
//  KeyMap.swift
//  macOS virtual key code  <->  PC Set 1 scan code.
//
//  The wire carries a PC Set 1 scan code, because that is what `SendInput` injects on Windows and
//  what a physical PC keyboard produces. A Mac keyboard reports `NSEvent.keyCode`, a macOS
//  virtual key code — a completely different numbering. There is no formula between them, so the
//  translation is a fixed table.
//
//  Only keys that exist on both sides are in the table. A key with no faithful PC equivalent
//  (Command, Fn, F13-F20, volume, brightness, ...) is deliberately absent: `binding(forMacKeyCode:)`
//  returns nil and the viewer sends nothing rather than a wrong scancode. The Windows injector has
//  no way to tell a wrong key from a right one, so a guess would be a real keystroke on someone
//  else's machine.
//
//  `extended` is the E0 prefix flag. PC Set 1 reuses the same byte for two keys and only the
//  prefix distinguishes them (Left vs Right Control, Numpad7 vs Home, ...), so it is part of the
//  identity on both directions.
//

import Foundation

/// One row of the translation table.
struct KeyBinding {
    /// `NSEvent.keyCode` / `CGKeyCode`.
    let macKeyCode: UInt16
    /// PC Set 1 make code, without the 0x80 break bit.
    let scanCode: UInt16
    /// E0 prefix.
    let extended: Bool
}

enum KeyMap {
    /// The table. Kept in one place so the two directions cannot drift apart.
    static let bindings: [KeyBinding] = [
        // Letters ---------------------------------------------------------------
        KeyBinding(macKeyCode: 0x00, scanCode: 0x1E, extended: false), // A
        KeyBinding(macKeyCode: 0x0B, scanCode: 0x30, extended: false), // B
        KeyBinding(macKeyCode: 0x08, scanCode: 0x2E, extended: false), // C
        KeyBinding(macKeyCode: 0x02, scanCode: 0x20, extended: false), // D
        KeyBinding(macKeyCode: 0x0E, scanCode: 0x12, extended: false), // E
        KeyBinding(macKeyCode: 0x03, scanCode: 0x21, extended: false), // F
        KeyBinding(macKeyCode: 0x05, scanCode: 0x22, extended: false), // G
        KeyBinding(macKeyCode: 0x04, scanCode: 0x23, extended: false), // H
        KeyBinding(macKeyCode: 0x22, scanCode: 0x17, extended: false), // I
        KeyBinding(macKeyCode: 0x26, scanCode: 0x24, extended: false), // J
        KeyBinding(macKeyCode: 0x28, scanCode: 0x25, extended: false), // K
        KeyBinding(macKeyCode: 0x25, scanCode: 0x26, extended: false), // L
        KeyBinding(macKeyCode: 0x2E, scanCode: 0x32, extended: false), // M
        KeyBinding(macKeyCode: 0x2D, scanCode: 0x31, extended: false), // N
        KeyBinding(macKeyCode: 0x1F, scanCode: 0x18, extended: false), // O
        KeyBinding(macKeyCode: 0x23, scanCode: 0x19, extended: false), // P
        KeyBinding(macKeyCode: 0x0C, scanCode: 0x10, extended: false), // Q
        KeyBinding(macKeyCode: 0x0F, scanCode: 0x13, extended: false), // R
        KeyBinding(macKeyCode: 0x01, scanCode: 0x1F, extended: false), // S
        KeyBinding(macKeyCode: 0x11, scanCode: 0x14, extended: false), // T
        KeyBinding(macKeyCode: 0x20, scanCode: 0x16, extended: false), // U
        KeyBinding(macKeyCode: 0x09, scanCode: 0x2F, extended: false), // V
        KeyBinding(macKeyCode: 0x0D, scanCode: 0x11, extended: false), // W
        KeyBinding(macKeyCode: 0x07, scanCode: 0x2D, extended: false), // X
        KeyBinding(macKeyCode: 0x10, scanCode: 0x15, extended: false), // Y
        KeyBinding(macKeyCode: 0x06, scanCode: 0x2C, extended: false), // Z

        // Digits (top row) ------------------------------------------------------
        KeyBinding(macKeyCode: 0x1D, scanCode: 0x0B, extended: false), // 0
        KeyBinding(macKeyCode: 0x12, scanCode: 0x02, extended: false), // 1
        KeyBinding(macKeyCode: 0x13, scanCode: 0x03, extended: false), // 2
        KeyBinding(macKeyCode: 0x14, scanCode: 0x04, extended: false), // 3
        KeyBinding(macKeyCode: 0x15, scanCode: 0x05, extended: false), // 4
        KeyBinding(macKeyCode: 0x17, scanCode: 0x06, extended: false), // 5
        KeyBinding(macKeyCode: 0x16, scanCode: 0x07, extended: false), // 6
        KeyBinding(macKeyCode: 0x1A, scanCode: 0x08, extended: false), // 7
        KeyBinding(macKeyCode: 0x1C, scanCode: 0x09, extended: false), // 8
        KeyBinding(macKeyCode: 0x19, scanCode: 0x0A, extended: false), // 9

        // Punctuation -----------------------------------------------------------
        KeyBinding(macKeyCode: 0x32, scanCode: 0x29, extended: false), // `
        KeyBinding(macKeyCode: 0x1B, scanCode: 0x0C, extended: false), // -
        KeyBinding(macKeyCode: 0x18, scanCode: 0x0D, extended: false), // =
        KeyBinding(macKeyCode: 0x21, scanCode: 0x1A, extended: false), // [
        KeyBinding(macKeyCode: 0x1E, scanCode: 0x1B, extended: false), // ]
        KeyBinding(macKeyCode: 0x2A, scanCode: 0x2B, extended: false), // backslash
        KeyBinding(macKeyCode: 0x29, scanCode: 0x27, extended: false), // ;
        KeyBinding(macKeyCode: 0x27, scanCode: 0x28, extended: false), // '
        KeyBinding(macKeyCode: 0x2B, scanCode: 0x33, extended: false), // ,
        KeyBinding(macKeyCode: 0x2F, scanCode: 0x34, extended: false), // .
        KeyBinding(macKeyCode: 0x2C, scanCode: 0x35, extended: false), // /

        // Editing / navigation --------------------------------------------------
        KeyBinding(macKeyCode: 0x24, scanCode: 0x1C, extended: false), // Return
        KeyBinding(macKeyCode: 0x30, scanCode: 0x0F, extended: false), // Tab
        KeyBinding(macKeyCode: 0x31, scanCode: 0x39, extended: false), // Space
        KeyBinding(macKeyCode: 0x33, scanCode: 0x0E, extended: false), // Backspace
        KeyBinding(macKeyCode: 0x35, scanCode: 0x01, extended: false), // Escape
        KeyBinding(macKeyCode: 0x75, scanCode: 0x53, extended: true),  // Forward Delete
        KeyBinding(macKeyCode: 0x73, scanCode: 0x47, extended: true),  // Home
        KeyBinding(macKeyCode: 0x77, scanCode: 0x4F, extended: true),  // End
        KeyBinding(macKeyCode: 0x74, scanCode: 0x49, extended: true),  // Page Up
        KeyBinding(macKeyCode: 0x79, scanCode: 0x51, extended: true),  // Page Down
        KeyBinding(macKeyCode: 0x72, scanCode: 0x52, extended: true),  // Help -> Insert
        KeyBinding(macKeyCode: 0x7B, scanCode: 0x4B, extended: true),  // Left
        KeyBinding(macKeyCode: 0x7C, scanCode: 0x4D, extended: true),  // Right
        KeyBinding(macKeyCode: 0x7D, scanCode: 0x50, extended: true),  // Down
        KeyBinding(macKeyCode: 0x7E, scanCode: 0x48, extended: true),  // Up

        // Modifiers -------------------------------------------------------------
        KeyBinding(macKeyCode: 0x38, scanCode: 0x2A, extended: false), // Left Shift
        KeyBinding(macKeyCode: 0x3C, scanCode: 0x36, extended: false), // Right Shift
        KeyBinding(macKeyCode: 0x3B, scanCode: 0x1D, extended: false), // Left Control
        KeyBinding(macKeyCode: 0x3E, scanCode: 0x1D, extended: true),  // Right Control
        KeyBinding(macKeyCode: 0x3A, scanCode: 0x38, extended: false), // Left Option -> Left Alt
        KeyBinding(macKeyCode: 0x3D, scanCode: 0x38, extended: true),  // Right Option -> Right Alt
        KeyBinding(macKeyCode: 0x39, scanCode: 0x3A, extended: false), // Caps Lock

        // Function keys ---------------------------------------------------------
        KeyBinding(macKeyCode: 0x7A, scanCode: 0x3B, extended: false), // F1
        KeyBinding(macKeyCode: 0x78, scanCode: 0x3C, extended: false), // F2
        KeyBinding(macKeyCode: 0x63, scanCode: 0x3D, extended: false), // F3
        KeyBinding(macKeyCode: 0x76, scanCode: 0x3E, extended: false), // F4
        KeyBinding(macKeyCode: 0x60, scanCode: 0x3F, extended: false), // F5
        KeyBinding(macKeyCode: 0x61, scanCode: 0x40, extended: false), // F6
        KeyBinding(macKeyCode: 0x62, scanCode: 0x41, extended: false), // F7
        KeyBinding(macKeyCode: 0x64, scanCode: 0x42, extended: false), // F8
        KeyBinding(macKeyCode: 0x65, scanCode: 0x43, extended: false), // F9
        KeyBinding(macKeyCode: 0x6D, scanCode: 0x44, extended: false), // F10
        KeyBinding(macKeyCode: 0x67, scanCode: 0x57, extended: false), // F11
        KeyBinding(macKeyCode: 0x6F, scanCode: 0x58, extended: false), // F12

        // Numeric keypad --------------------------------------------------------
        KeyBinding(macKeyCode: 0x52, scanCode: 0x52, extended: false), // KP 0
        KeyBinding(macKeyCode: 0x53, scanCode: 0x4F, extended: false), // KP 1
        KeyBinding(macKeyCode: 0x54, scanCode: 0x50, extended: false), // KP 2
        KeyBinding(macKeyCode: 0x55, scanCode: 0x51, extended: false), // KP 3
        KeyBinding(macKeyCode: 0x56, scanCode: 0x4B, extended: false), // KP 4
        KeyBinding(macKeyCode: 0x57, scanCode: 0x4C, extended: false), // KP 5
        KeyBinding(macKeyCode: 0x58, scanCode: 0x4D, extended: false), // KP 6
        KeyBinding(macKeyCode: 0x59, scanCode: 0x47, extended: false), // KP 7
        KeyBinding(macKeyCode: 0x5B, scanCode: 0x48, extended: false), // KP 8
        KeyBinding(macKeyCode: 0x5C, scanCode: 0x49, extended: false), // KP 9
        KeyBinding(macKeyCode: 0x41, scanCode: 0x53, extended: false), // KP .
        KeyBinding(macKeyCode: 0x43, scanCode: 0x37, extended: false), // KP *
        KeyBinding(macKeyCode: 0x45, scanCode: 0x4E, extended: false), // KP +
        KeyBinding(macKeyCode: 0x4E, scanCode: 0x4A, extended: false), // KP -
        KeyBinding(macKeyCode: 0x4B, scanCode: 0x35, extended: true),  // KP /
        KeyBinding(macKeyCode: 0x4C, scanCode: 0x1C, extended: true),  // KP Enter
    ]

    /// macOS key codes whose state arrives through `flagsChanged`, not `keyDown`.
    static let modifierKeyCodes: Set<UInt16> = [
        0x38, // Left Shift
        0x3C, // Right Shift
        0x3B, // Left Control
        0x3E, // Right Control
        0x3A, // Left Option
        0x3D, // Right Option
        0x39, // Caps Lock
    ]

    private static let byMacKeyCode: [UInt16: KeyBinding] = {
        var map: [UInt16: KeyBinding] = [:]
        for binding in bindings {
            map[binding.macKeyCode] = binding
        }
        return map
    }()

    private static let byScanCode: [UInt32: KeyBinding] = {
        var map: [UInt32: KeyBinding] = [:]
        for binding in bindings {
            map[scanKey(scanCode: binding.scanCode, extended: binding.extended)] = binding
        }
        return map
    }()

    /// Composite key so that the E0 flag participates in identity.
    static func scanKey(scanCode: UInt16, extended: Bool) -> UInt32 {
        UInt32(scanCode) | (extended ? 0x0001_0000 : 0)
    }

    static func isModifier(macKeyCode: UInt16) -> Bool {
        modifierKeyCodes.contains(macKeyCode)
    }

    /// Viewer direction: Mac key event -> PC scan code.
    static func binding(forMacKeyCode keyCode: UInt16) -> KeyBinding? {
        byMacKeyCode[keyCode]
    }

    /// Host direction: PC scan code from the wire -> Mac key code for `CGEvent`.
    static func binding(forScanCode scanCode: UInt16, extended: Bool) -> KeyBinding? {
        byScanCode[scanKey(scanCode: scanCode, extended: extended)]
    }

    /// Human readable table for the in-app help sheet and for the README.
    static var tableDescription: String {
        bindings
            .sorted { ($0.scanCode, $0.extended ? 1 : 0) < ($1.scanCode, $1.extended ? 1 : 0) }
            .map { binding in
                let scan = String(format: "0x%02X", Int(binding.scanCode))
                let prefix = binding.extended ? "E0" : "  "
                let mac = String(format: "0x%02X", Int(binding.macKeyCode))
                return "\(scan) \(prefix) -> \(mac)"
            }
            .joined(separator: "\n")
    }
}
