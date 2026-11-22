//
//  MacInputInjector.swift
//  CGEvent synthesis for the Host.
//
//  Two hard requirements:
//
//   1. Accessibility permission. `CGEvent.post` silently does nothing for a process that is not
//      trusted, so the UI has to check `AXIsProcessTrusted` and say so. The injector refuses to
//      pretend it worked: `isTrusted` is a precondition of every call.
//
//   2. No feedback loop. Every event this file posts carries `kCGEventSourceUserData` set to
//      `eventMarker`, so an event tap (or a future capture filter) can tell an injected event from
//      a physical one and drop it.
//
//  The wire carries PC Set 1 scan codes; `CGEvent` wants macOS virtual key codes, so every key
//  goes through `KeyMap.binding(forScanCode:extended:)`. A scan code with no Mac equivalent is
//  dropped rather than guessed.
//

import AppKit
import ApplicationServices
import CoreGraphics
import Foundation

final class MacInputInjector {
    /// Stamped into `kCGEventSourceUserData` on every injected event. ASCII "SCMC".
    static let eventMarker: Int64 = 0x5343_4D43

    private let source: CGEventSource?
    private let lock = NSLock()
    private var heldKeys: Set<UInt16> = []      // macOS virtual key codes
    private var heldButtons: Set<Int> = []
    private var leaseOwner: String?
    private var cursorPosition = CGPoint.zero

    /// Set by the host session from the accessibility check. Injection is a no-op while false.
    var isTrusted = false

    init() {
        source = CGEventSource(stateID: .hidSystemState)
    }

    // MARK: - Permission

    /// `kAXTrustedCheckOptionPrompt` is the documented key for `AXIsProcessTrustedWithOptions`.
    /// The literal is used so the call does not depend on how the constant imports into Swift.
    static func isAccessibilityTrusted(prompt: Bool = false) -> Bool {
        let options: [String: Any] = ["AXTrustedCheckOptionPrompt": prompt]
        return AXIsProcessTrustedWithOptions(options as CFDictionary)
    }

    static func openAccessibilitySettings() {
        let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")
        if let url {
            NSWorkspace.shared.open(url)
        }
    }

    // MARK: - Lease

    var currentLeaseOwner: String? {
        lock.lock()
        defer { lock.unlock() }
        return leaseOwner
    }

    var pressedKeyCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return heldKeys.count
    }

    func acquireLease(owner: String) {
        lock.lock()
        leaseOwner = owner
        lock.unlock()
    }

    /// Dropping the lease also releases everything it was holding: a viewer that disappears must
    /// not leave a modifier stuck down on this machine.
    func releaseLease(owner: String) {
        lock.lock()
        let owned = leaseOwner == owner
        if owned {
            leaseOwner = nil
        }
        let keys = owned ? heldKeys : []
        let buttons = owned ? heldButtons : []
        if owned {
            heldKeys.removeAll()
            heldButtons.removeAll()
        }
        lock.unlock()

        for key in keys {
            postKey(macKeyCode: key, isDown: false)
        }
        for button in buttons {
            postMouseButton(MacInputInjector.cgButton(for: button), isDown: false)
        }
    }

    private var canInject: Bool {
        isTrusted && currentLeaseOwner != nil
    }

    /// `heldButtons` stores `CGMouseButton.rawValue` as an `Int`; this maps it back without relying
    /// on how the C enum imports its raw-value initialiser.
    private static func cgButton(for rawValue: Int) -> CGMouseButton {
        switch rawValue {
        case Int(CGMouseButton.right.rawValue): return .right
        case Int(CGMouseButton.center.rawValue): return .center
        default: return .left
        }
    }

    // MARK: - Mouse

    /// `displayBounds` is `CGDisplayBounds` of the shared display, in the global point space that
    /// `CGEvent` positions use (top-left origin, points, not pixels).
    func movePointer(normalizedX: UInt16, normalizedY: UInt16, displayBounds: CGRect) {
        guard canInject else { return }
        let point = CGPoint(
            x: ControlWire.denormalize(normalizedX, into: Double(displayBounds.origin.x), extent: Double(displayBounds.width)),
            y: ControlWire.denormalize(normalizedY, into: Double(displayBounds.origin.y), extent: Double(displayBounds.height)))
        cursorPosition = point

        guard let event = CGEvent(mouseEventSource: source,
                                  mouseType: .mouseMoved,
                                  mouseCursorPosition: point,
                                  mouseButton: .left) else { return }
        stamp(event)
        event.post(tap: .cghidEventTap)
    }

    func mouseButton(_ button: MouseButtonID, isDown: Bool) {
        guard canInject else { return }

        let cgButton: CGMouseButton
        let eventType: CGEventType
        switch button {
        case .left:
            cgButton = .left
            eventType = isDown ? .leftMouseDown : .leftMouseUp
        case .right:
            cgButton = .right
            eventType = isDown ? .rightMouseDown : .rightMouseUp
        case .middle:
            cgButton = .center
            eventType = isDown ? .otherMouseDown : .otherMouseUp
        }

        lock.lock()
        if isDown {
            heldButtons.insert(Int(cgButton.rawValue))
        } else {
            heldButtons.remove(Int(cgButton.rawValue))
        }
        lock.unlock()

        guard let event = CGEvent(mouseEventSource: source,
                                  mouseType: eventType,
                                  mouseCursorPosition: cursorPosition,
                                  mouseButton: cgButton) else { return }
        stamp(event)
        event.post(tap: .cghidEventTap)
    }

    func wheel(deltaX: Int16, deltaY: Int16) {
        guard canInject else { return }

        // Windows wheel units are 120 per notch; `CGEvent` line units are 1 per notch.
        let vertical = Int32((Double(deltaY) / 120.0).rounded())
        let horizontal = Int32((Double(deltaX) / 120.0).rounded())
        guard vertical != 0 || horizontal != 0 else { return }

        guard let event = CGEvent(scrollWheelEvent2Source: source,
                                  units: .line,
                                  wheelCount: 2,
                                  wheel1: vertical,
                                  wheel2: horizontal,
                                  wheel3: 0) else { return }
        stamp(event)
        event.post(tap: .cghidEventTap)
    }

    // MARK: - Keyboard

    func key(scanCode: UInt16, extended: Bool, isDown: Bool) {
        guard canInject else { return }
        guard let binding = KeyMap.binding(forScanCode: scanCode, extended: extended) else {
            // No faithful macOS key for this PC scan code. Sending something else would be a
            // different keystroke than the user made.
            return
        }

        lock.lock()
        if isDown {
            heldKeys.insert(binding.macKeyCode)
        } else {
            heldKeys.remove(binding.macKeyCode)
        }
        lock.unlock()

        postKey(macKeyCode: binding.macKeyCode, isDown: isDown)
    }

    private func postKey(macKeyCode: UInt16, isDown: Bool) {
        guard let event = CGEvent(keyboardEventSource: source,
                                  virtualKey: CGKeyCode(macKeyCode),
                                  keyDown: isDown) else { return }
        stamp(event)
        event.setIntegerValueField(.keyboardEventAutorepeat, value: 0)
        event.post(tap: .cghidEventTap)
    }

    private func postMouseButton(_ button: CGMouseButton, isDown: Bool) {
        let eventType: CGEventType
        switch button {
        case .left: eventType = isDown ? .leftMouseDown : .leftMouseUp
        case .right: eventType = isDown ? .rightMouseDown : .rightMouseUp
        default: eventType = isDown ? .otherMouseDown : .otherMouseUp
        }
        guard let event = CGEvent(mouseEventSource: source,
                                  mouseType: eventType,
                                  mouseCursorPosition: cursorPosition,
                                  mouseButton: button) else { return }
        stamp(event)
        event.post(tap: .cghidEventTap)
    }

    /// Everything held right now, released. Used on ReleaseAll, on lease loss and on shutdown.
    func releaseAll() {
        lock.lock()
        let keys = heldKeys
        let buttons = heldButtons
        heldKeys.removeAll()
        heldButtons.removeAll()
        lock.unlock()

        for key in keys {
            postKey(macKeyCode: key, isDown: false)
        }
        for button in buttons {
            postMouseButton(MacInputInjector.cgButton(for: button), isDown: false)
        }
    }

    private func stamp(_ event: CGEvent) {
        event.setIntegerValueField(.eventSourceUserData, value: Self.eventMarker)
    }
}
