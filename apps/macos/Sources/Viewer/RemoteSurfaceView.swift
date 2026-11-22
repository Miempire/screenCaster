//
//  RemoteSurfaceView.swift
//  The Viewer's picture surface plus the input capture that goes with it.
//
//  One view does both jobs on purpose: the letterbox geometry used to place the picture is the
//  same geometry used to invert the pointer position, so keeping them in one place means they
//  cannot disagree. The view is flipped (top-left origin) to match the Windows desktop coordinate
//  convention the normalized coordinates are defined in.
//
//  Mouse position is normalized 0..65535 against the *picture*, not the view: a click in the black
//  letterbox bar would otherwise land somewhere arbitrary on the remote desktop.
//

import AppKit
import AVFoundation

final class RemoteSurfaceView: NSView {
    /// `AVSampleBufferDisplayLayer` is the simplest reliable renderer: it takes a decoded
    /// `CMSampleBuffer` directly and handles scaling, colour space and vsync itself.
    let displayLayer = AVSampleBufferDisplayLayer()

    /// Size of the last decoded picture, in pixels. Zero until the first frame.
    var videoSize = CGSize.zero

    /// (scanCode, extended, isDown)
    var onKey: ((UInt16, Bool, Bool) -> Void)?
    /// (normalizedX, normalizedY)
    var onMouseMove: ((UInt16, UInt16) -> Void)?
    /// (button, isDown)
    var onMouseButton: ((MouseButtonID, Bool) -> Void)?
    /// (deltaX, deltaY) in Windows wheel units
    var onWheel: ((Int16, Int16) -> Void)?
    var onReleaseAll: (() -> Void)?

    private var pressedModifiers: Set<UInt16> = []
    private var trackingAreaRef: NSTrackingArea?

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor
        displayLayer.videoGravity = .resizeAspect
        displayLayer.backgroundColor = NSColor.black.cgColor
        layer?.addSublayer(displayLayer)
    }

    required init?(coder: NSCoder) {
        fatalError("RemoteSurfaceView is created in code only")
    }

    // MARK: - Layout

    override func layout() {
        super.layout()
        attachDisplayLayerIfNeeded()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        displayLayer.frame = bounds
        CATransaction.commit()
    }

    private func attachDisplayLayerIfNeeded() {
        guard let host = layer else { return }
        if displayLayer.superlayer !== host {
            displayLayer.removeFromSuperlayer()
            host.addSublayer(displayLayer)
        }
    }

    /// The picture rect inside the view, after the aspect-fit letterbox is applied.
    var pictureRect: CGRect {
        let boundsSize = bounds.size
        guard videoSize.width > 1, videoSize.height > 1,
              boundsSize.width > 1, boundsSize.height > 1 else {
            return bounds
        }
        let scale = min(boundsSize.width / videoSize.width, boundsSize.height / videoSize.height)
        let width = videoSize.width * scale
        let height = videoSize.height * scale
        return CGRect(x: (boundsSize.width - width) / 2,
                      y: (boundsSize.height - height) / 2,
                      width: width,
                      height: height)
    }

    // MARK: - Rendering

    func enqueue(_ sampleBuffer: CMSampleBuffer) {
        if displayLayer.status == .failed || displayLayer.requiresFlushToResumeDecoding {
            displayLayer.flush()
        }
        displayLayer.enqueue(sampleBuffer)
    }

    func flush() {
        displayLayer.flush()
        videoSize = .zero
    }

    // MARK: - Tracking

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let trackingAreaRef {
            removeTrackingArea(trackingAreaRef)
        }
        let area = NSTrackingArea(
            rect: bounds,
            options: [.activeInKeyWindow, .inVisibleRect, .mouseMoved, .enabledDuringMouseDrag],
            owner: self,
            userInfo: nil)
        addTrackingArea(area)
        trackingAreaRef = area
    }

    // MARK: - Mouse

    private func emitMove(_ event: NSEvent) {
        guard let normalized = normalizedPoint(for: event) else { return }
        onMouseMove?(normalized.x, normalized.y)
    }

    private func normalizedPoint(for event: NSEvent) -> (x: UInt16, y: UInt16)? {
        guard videoSize.width > 1, videoSize.height > 1 else { return nil }
        let picture = pictureRect
        guard picture.width > 1, picture.height > 1 else { return nil }

        let location = convert(event.locationInWindow, from: nil)
        let pixelX = (location.x - picture.origin.x) / picture.width * videoSize.width
        // isFlipped is true, so location.y already grows downwards like the Windows desktop.
        let pixelY = (location.y - picture.origin.y) / picture.height * videoSize.height

        return (ControlWire.normalize(pixelX, extent: videoSize.width),
                ControlWire.normalize(pixelY, extent: videoSize.height))
    }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        emitMove(event)
        onMouseButton?(.left, true)
    }

    override func mouseUp(with event: NSEvent) {
        onMouseButton?(.left, false)
    }

    override func rightMouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        emitMove(event)
        onMouseButton?(.right, true)
    }

    override func rightMouseUp(with event: NSEvent) {
        onMouseButton?(.right, false)
    }

    override func otherMouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        emitMove(event)
        onMouseButton?(.middle, true)
    }

    override func otherMouseUp(with event: NSEvent) {
        onMouseButton?(.middle, false)
    }

    override func mouseMoved(with event: NSEvent) {
        emitMove(event)
    }

    override func mouseDragged(with event: NSEvent) {
        emitMove(event)
    }

    override func rightMouseDragged(with event: NSEvent) {
        emitMove(event)
    }

    override func otherMouseDragged(with event: NSEvent) {
        emitMove(event)
    }

    override func scrollWheel(with event: NSEvent) {
        // The wire uses Windows wheel units: 120 per notch. A trackpad reports points, a wheel
        // reports lines, so the two need different scale factors.
        let unit: Double = event.hasPreciseScrollingDeltas ? 8.0 : 120.0
        let deltaY = clampToInt16(event.scrollingDeltaY * unit)
        let deltaX = clampToInt16(event.scrollingDeltaX * unit)
        guard deltaX != 0 || deltaY != 0 else { return }
        onWheel?(deltaX, deltaY)
    }

    private func clampToInt16(_ value: Double) -> Int16 {
        let rounded = value.rounded()
        if rounded > Double(Int16.max) { return Int16.max }
        if rounded < Double(Int16.min) { return Int16.min }
        return Int16(rounded)
    }

    // MARK: - Keyboard

    override func keyDown(with event: NSEvent) {
        // Command combinations belong to the local Mac (Cmd+Q, Cmd+W, ...). Forwarding them would
        // press a key on the remote machine that the user meant for this one.
        if event.modifierFlags.contains(.command) {
            super.keyDown(with: event)
            return
        }
        // The remote host generates its own auto-repeat from the held key, so a repeated event
        // would double the rate.
        if event.isARepeat { return }
        guard let binding = KeyMap.binding(forMacKeyCode: event.keyCode),
              !KeyMap.isModifier(macKeyCode: event.keyCode) else {
            return
        }
        onKey?(binding.scanCode, binding.extended, true)
    }

    override func keyUp(with event: NSEvent) {
        if event.modifierFlags.contains(.command) {
            super.keyUp(with: event)
            return
        }
        guard let binding = KeyMap.binding(forMacKeyCode: event.keyCode),
              !KeyMap.isModifier(macKeyCode: event.keyCode) else {
            return
        }
        onKey?(binding.scanCode, binding.extended, false)
    }

    override func flagsChanged(with event: NSEvent) {
        let keyCode = event.keyCode
        guard KeyMap.isModifier(macKeyCode: keyCode),
              let binding = KeyMap.binding(forMacKeyCode: keyCode) else {
            super.flagsChanged(with: event)
            return
        }

        // Modifier state arrives here, never through keyDown/keyUp. Tracking the set ourselves
        // instead of trusting `modifierFlags` keeps left and right Shift (and Control, Option)
        // independent: with both held, `modifierFlags` only says "shift is down".
        if pressedModifiers.contains(keyCode) {
            pressedModifiers.remove(keyCode)
            onKey?(binding.scanCode, binding.extended, false)
        } else {
            pressedModifiers.insert(keyCode)
            onKey?(binding.scanCode, binding.extended, true)
        }
    }

    override func resignFirstResponder() -> Bool {
        releaseModifiers()
        return super.resignFirstResponder()
    }

    private func releaseModifiers() {
        guard !pressedModifiers.isEmpty else { return }
        let codes = pressedModifiers
        pressedModifiers.removeAll()
        for keyCode in codes {
            guard let binding = KeyMap.binding(forMacKeyCode: keyCode) else { continue }
            onKey?(binding.scanCode, binding.extended, false)
        }
    }

    /// Called when the session stops so a held modifier cannot stay stuck on the remote machine.
    func releaseEverything() {
        releaseModifiers()
        onReleaseAll?()
    }
}
