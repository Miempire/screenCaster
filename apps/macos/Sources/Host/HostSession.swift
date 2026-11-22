//
//  HostSession.swift
//  The controlled side.
//
//  Mirrors `src/ScreenCast.Windows/Session/HostSession.cs`: capture the display, encode, send, and
//  inject the remote input that comes back. Differences forced by the platform:
//
//   * Capture is ScreenCaptureKit instead of Desktop Duplication; unchanged frames are already
//     filtered by ScreenCaptureKit's `.idle` frame status, so there is no dirty-rect check here.
//   * The Rust core owns the sockets, so there is no transport object and no `Tick()`.
//   * The peer endpoint has to be typed in. The C ABI can only be told where to send
//     (`sc_session_set_peer`); it cannot report the source address of the first datagram the way
//     the Windows build's `WaitForPeerSources` does, so this build asks for the viewer's address
//     and ports instead of guessing them.
//

import AppKit
import CoreGraphics
import Foundation

enum HostError: LocalizedError {
    case encoderUnavailable

    var errorDescription: String? {
        switch self {
        case .encoderUnavailable:
            return "无法创建 H.264 编码器（VTCompressionSessionCreate / PrepareToEncodeFrames 失败）。"
        }
    }
}

struct HostStats: Equatable {    var capturedFrames = 0
    var encodedFrames = 0
    var keyFrames = 0
    var sentBytes = 0
    var rejectedDatagrams = 0
    var pressedKeys = 0
    var leaseOwner = ""
    var encodeMilliseconds = 0.0
    var videoRTTMilliseconds = -1.0
    var controlRTTMilliseconds = -1.0
    var videoPath = "—"
    var controlPath = "—"
    var width = 0
    var height = 0
    var fps = 0.0
}

final class HostSession: ObservableObject {
    // MARK: - Published state

    @Published private(set) var stats = HostStats()
    @Published private(set) var running = false
    @Published private(set) var statusText = "未开始"
    @Published private(set) var boundPorts: BoundPorts?
    @Published private(set) var lastError = ""
    @Published private(set) var accessibilityTrusted = MacInputInjector.isAccessibilityTrusted()
    @Published private(set) var screenRecordingTrusted = ScreenCaptureSource.hasScreenRecordingPermission
    @Published private(set) var displays: [CaptureDisplay] = []

    @Published var selectedDisplayID: UInt32 = 0
    @Published var peerAddress = "127.0.0.1"
    @Published var peerVideoPort = String(CoreDefaults.viewerVideoPort)
    @Published var peerControlPort = String(CoreDefaults.viewerControlPort)
    @Published var localVideoPort = String(CoreDefaults.hostVideoPort)
    @Published var localControlPort = String(CoreDefaults.hostControlPort)
    @Published var maxWidthText = String(CoreDefaults.defaultWidth)
    @Published var maxHeightText = String(CoreDefaults.defaultHeight)
    @Published var fpsText = String(CoreDefaults.defaultFPS)
    @Published var bitrateText = String(CoreDefaults.defaultBitrate / 1_000_000)

    // MARK: - Session state

    private var core: CoreSession?
    private var encoder: H264Encoder?
    private var capture: ScreenCaptureSource?
    private var injector: MacInputInjector?

    private let stopFlag = LockedFlag(true)
    private let forceKeyFrame = LockedFlag(false)
    private let counters = CounterBox()
    private var exitSemaphore: DispatchSemaphore?
    private var statsTimer: Timer?

    private var displayBounds = CGRect.zero
    private var nextFrameID: UInt64 = 0
    private var lastFPSStamp = CFAbsoluteTimeGetCurrent()
    private var fpsAtStamp = 0

    // MARK: - Displays / permissions

    /// Main-actor methods only touch `@Published` state; the stored properties themselves stay
    /// non-isolated so the capture and poll threads can reach the locked helpers without a hop.
    @MainActor
    func refreshPermissions() {
        accessibilityTrusted = MacInputInjector.isAccessibilityTrusted()
        screenRecordingTrusted = ScreenCaptureSource.hasScreenRecordingPermission
        injector?.isTrusted = accessibilityTrusted
    }

    @MainActor
    func loadDisplays() async {
        refreshPermissions()
        guard screenRecordingTrusted else { return }
        do {
            let found = try await ScreenCaptureSource.availableDisplays()
            displays = found
            if !found.contains(where: { $0.id == selectedDisplayID }) {
                selectedDisplayID = found.first?.id ?? 0
            }
        } catch {
            lastError = error.localizedDescription
        }
    }

    var selectedDisplay: CaptureDisplay? {
        displays.first { $0.id == selectedDisplayID } ?? displays.first
    }

    // MARK: - Start / stop

    @MainActor
    func start() async {
        guard !running else { return }

        guard let literal = HostResolver.literal(for: peerAddress) else {
            lastError = "对端地址 \(peerAddress) 无法解析为 IP 字面量（C ABI 不做 DNS）。"
            return
        }
        guard let peerVideo = UInt16(peerVideoPort.trimmingCharacters(in: .whitespaces)),
              let peerControl = UInt16(peerControlPort.trimmingCharacters(in: .whitespaces)) else {
            lastError = "对端端口不是 0..65535 的整数。"
            return
        }
        let localVideo = UInt16(localVideoPort.trimmingCharacters(in: .whitespaces)) ?? 0
        let localControl = UInt16(localControlPort.trimmingCharacters(in: .whitespaces)) ?? 0

        guard let display = selectedDisplay else {
            lastError = "没有选择显示器（先点“刷新显示器”并授予屏幕录制权限）。"
            return
        }

        refreshPermissions()
        guard screenRecordingTrusted else {
            ScreenCaptureSource.requestScreenRecordingPermission()
            lastError = "需要“屏幕录制”权限才能采集画面：系统设置 → 隐私与安全性 → 屏幕录制。"
            return
        }

        let maxWidth = Int(maxWidthText.trimmingCharacters(in: .whitespaces)) ?? CoreDefaults.defaultWidth
        let maxHeight = Int(maxHeightText.trimmingCharacters(in: .whitespaces)) ?? CoreDefaults.defaultHeight
        let fps = max(1, min(60, Int(fpsText.trimmingCharacters(in: .whitespaces)) ?? CoreDefaults.defaultFPS))
        let bitrate = max(1, Int(bitrateText.trimmingCharacters(in: .whitespaces)) ?? 8) * 1_000_000
        let target = ScreenCaptureSource.targetSize(for: display, maxWidth: maxWidth, maxHeight: maxHeight)

        do {
            let core = try CoreSession(secret: CoreDefaults.sharedSecret,
                                       sessionID: CoreDefaults.sessionID,
                                       isHost: true)
            let bound = try core.bind(videoPort: localVideo, controlPort: localControl)
            try core.setPeer(videoAddress: literal, videoPort: peerVideo,
                             controlAddress: literal, controlPort: peerControl)
            try core.start()

            self.core = core
            boundPorts = BoundPorts(video: bound.video, control: bound.control)
            displayBounds = CGDisplayBounds(CGDirectDisplayID(display.id))

            let injector = MacInputInjector()
            injector.isTrusted = accessibilityTrusted
            self.injector = injector

            let encoder = H264Encoder(width: target.width,
                                      height: target.height,
                                      fps: fps,
                                      bitrate: bitrate) { [weak self] output in
                self?.send(output)
            }
            guard encoder.prepare() else {
                throw HostError.encoderUnavailable
            }
            self.encoder = encoder

            counters.reset()
            nextFrameID = 0
            lastFPSStamp = CFAbsoluteTimeGetCurrent()
            fpsAtStamp = 0
            // The very first frame must be a key frame or the viewer has no reference to start from.
            forceKeyFrame.raise()

            let capture = ScreenCaptureSource()
            self.capture = capture
            try await capture.start(displayID: CGDirectDisplayID(display.id),
                                    width: target.width,
                                    height: target.height,
                                    fps: fps) { [weak self] pixelBuffer, timestampUS in
                guard let self else { return }
                self.counters.add("captured")
                _ = self.encoder?.encode(pixelBuffer: pixelBuffer,
                                         presentationTimeUS: timestampUS,
                                         forceKeyFrame: self.forceKeyFrame.take())
            }

            running = true
            lastError = ""
            statusText = "共享中：\(display.name) \(target.width)x\(target.height)@\(fps) · 本机端口 \(bound.video)/\(bound.control) · 对端 \(literal):\(peerVideo)/\(peerControl)"
            startPolling()
        } catch {
            lastError = error.localizedDescription
            statusText = "启动失败"
            await cleanup()
        }
    }

    @MainActor
    func stop() async {
        guard running || core != nil else { return }
        // Releasing held keys is the last thing that must happen on this machine.
        injector?.releaseAll()
        await cleanup()
        statusText = "已停止"
    }

    @MainActor
    private func cleanup() async {
        stopFlag.set(true)
        _ = exitSemaphore?.wait(timeout: .now() + 3)
        exitSemaphore = nil

        statsTimer?.invalidate()
        statsTimer = nil

        await capture?.stop()
        capture = nil
        encoder?.close()
        encoder = nil
        injector?.releaseAll()
        injector = nil
        core?.close()
        core = nil

        running = false
        boundPorts = nil
        forceKeyFrame.set(false)
    }

    // MARK: - Encode -> wire

    private func send(_ output: H264Encoder.Output) {
        guard let core else { return }
        let frameID = nextFrameID
        nextFrameID &+= 1
        let flags = output.isKeyFrame ? CoreDefaults.frameFlagKeyFrame : 0
        core.sendFrame(output.annexB,
                       frameID: frameID,
                       timestampUS: output.presentationTimeUS,
                       configID: CoreDefaults.configID,
                       flags: flags,
                       codec: CoreDefaults.codecH264)
    }

    // MARK: - Receive loop

    private func startPolling() {
        stopFlag.set(false)
        let semaphore = DispatchSemaphore(value: 0)
        exitSemaphore = semaphore

        Thread.detachNewThread { [weak self] in
            self?.pollLoop()
            semaphore.signal()
        }

        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.statsTimer?.invalidate()
            self.statsTimer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
                self?.refreshStats()
            }
        }
    }

    private func pollLoop() {
        while !stopFlag.isSet {
            var handledAny = false
            for _ in 0 ..< 16 {
                guard let core, let event = core.poll() else { break }
                handledAny = true
                handle(event)
            }
            if !handledAny {
                usleep(1000)
            }
        }
    }

    private func handle(_ event: CoreEvent) {
        switch event.kind {
        case .control:
            handleControl(event.eventType, event.payload)
        case .controlExpired:
            // The reliable channel gave up on an event, so the platform can no longer know whether
            // it was applied. Releasing everything is the only safe state.
            injector?.releaseAll()
        case .videoFrame, .path, .none:
            break
        }
    }

    private func handleControl(_ type: UInt16, _ body: [UInt8]) {
        guard let injector, let kind = ControlEventType(rawValue: type) else { return }

        switch kind {
        case .controlLease:
            if body.count >= 1, body[0] != 0 {
                injector.acquireLease(owner: "viewer")
            } else {
                injector.releaseLease(owner: "viewer")
            }

        case .key:
            if let key = ControlWire.decodeKey(body) {
                injector.key(scanCode: key.scanCode, extended: key.extended, isDown: key.isDown)
            }

        case .mouseMove:
            if let move = ControlWire.decodeMouseMove(body) {
                injector.movePointer(normalizedX: move.x,
                                     normalizedY: move.y,
                                     displayBounds: displayBounds)
            }

        case .mouseButton:
            if let button = ControlWire.decodeMouseButton(body) {
                injector.mouseButton(button.button, isDown: button.isDown)
            }

        case .wheel:
            if let wheel = ControlWire.decodeWheel(body) {
                injector.wheel(deltaX: wheel.deltaX, deltaY: wheel.deltaY)
            }

        case .releaseAll:
            injector.releaseAll()

        case .requestKeyFrame:
            forceKeyFrame.raise()

        case .touch, .setQuality, .pathSwitch:
            break
        }
    }

    // MARK: - Stats

    private func refreshStats() {
        guard let core, let encoder else { return }
        let native = core.stats()
        let now = CFAbsoluteTimeGetCurrent()

        var updated = HostStats()
        updated.capturedFrames = counters.value("captured")
        updated.encodedFrames = encoder.encodedFrames
        updated.keyFrames = encoder.keyFrames
        updated.sentBytes = Int(native.videoBytesSent)
        updated.rejectedDatagrams = Int(native.videoDatagramsRejected)
        updated.pressedKeys = injector?.pressedKeyCount ?? 0
        updated.leaseOwner = injector?.currentLeaseOwner ?? ""
        updated.encodeMilliseconds = encoder.averageEncodeMilliseconds
        updated.videoRTTMilliseconds = native.videoRTTMilliseconds
        updated.controlRTTMilliseconds = native.controlRTTMilliseconds
        updated.videoPath = CoreStats.pathName(native.videoPathStatus)
        updated.controlPath = CoreStats.pathName(native.controlPathStatus)
        updated.width = encoder.width
        updated.height = encoder.height

        let elapsed = now - lastFPSStamp
        if elapsed >= 0.5 {
            updated.fps = Double(updated.encodedFrames - fpsAtStamp) / elapsed
            fpsAtStamp = updated.encodedFrames
            lastFPSStamp = now
        } else {
            updated.fps = stats.fps
        }

        stats = updated

        // Permissions can change while the session runs; pick that up without a restart.
        let trusted = MacInputInjector.isAccessibilityTrusted()
        if trusted != accessibilityTrusted {
            accessibilityTrusted = trusted
            injector?.isTrusted = trusted
        }
        let screenTrusted = ScreenCaptureSource.hasScreenRecordingPermission
        if screenTrusted != screenRecordingTrusted {
            screenRecordingTrusted = screenTrusted
        }
        if updated.leaseOwner.isEmpty, injector != nil {
            statusText = "共享中（无输入租约，只观看）"
        }
    }
}
