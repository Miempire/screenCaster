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
    @Published private(set) var inviteCode = ""
    @Published private(set) var peerSummary = ""

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

    private var signaling: SignalingClient?
    private var signalingView: SessionView?
    private var signalingAccepted = false
    private var signalingMediaStarted = false
    private var signalingEpoch = 0
    private var pendingDisplay: CaptureDisplay?
    private var pendingTarget: (width: Int, height: Int) = (0, 0)
    private var pendingFPS = CoreDefaults.defaultFPS
    private var pendingBitrate = CoreDefaults.defaultBitrate

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
        guard !running, signaling == nil else { return }

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
            peerSummary = "\(literal):\(peerVideo)/\(peerControl)"
            try await beginCapture(display: display, target: target, fps: fps, bitrate: bitrate)
            statusText = "共享中：\(display.name) \(target.width)x\(target.height)@\(fps) · 本机端口 \(bound.video)/\(bound.control) · 对端 \(literal):\(peerVideo)/\(peerControl)"
        } catch {
            lastError = error.localizedDescription
            statusText = "启动失败"
            await cleanup()
        }
    }

    @MainActor
    func startSignaling() async {
        guard !running, signaling == nil else { return }

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

        pendingDisplay = display
        pendingTarget = ScreenCaptureSource.targetSize(for: display, maxWidth: maxWidth, maxHeight: maxHeight)
        pendingFPS = fps
        pendingBitrate = bitrate
        lastError = ""
        await openSignaling()
    }

    @MainActor
    func refreshCode() async {
        guard !running, !inviteCode.isEmpty else { return }
        resetSignaling()
        await openSignaling()
    }

    @MainActor
    private func openSignaling() async {
        guard signaling == nil else { return }
        signalingEpoch += 1
        let epoch = signalingEpoch
        let client = SignalingClient()
        signaling = client
        client.onJoinRequested = { [weak self] view in
            Task { @MainActor in
                guard let self, self.signalingEpoch == epoch else { return }
                await self.acceptViewer(view)
            }
        }
        client.onPeerKey = { [weak self] message in
            Task { @MainActor in
                guard let self, self.signalingEpoch == epoch else { return }
                self.handlePeerKey(message)
            }
        }
        client.onSessionClosed = { [weak self] view in
            Task { @MainActor in
                guard let self, self.signalingEpoch == epoch else { return }
                await self.handleSignalingClosed(view)
            }
        }
        client.onError = { [weak self] error in
            Task { @MainActor in
                guard let self, self.signalingEpoch == epoch else { return }
                self.lastError = error.localizedDescription
            }
        }
        client.onFault = { [weak self] error in
            Task { @MainActor in
                guard let self, self.signalingEpoch == epoch, !self.running else { return }
                self.lastError = error.localizedDescription
            }
        }

        do {
            try await client.start()
            guard signalingEpoch == epoch else {
                client.stop()
                return
            }
            let created = try await client.createSession()
            guard signalingEpoch == epoch else {
                client.stop()
                return
            }
            signalingView = created
            signalingAccepted = false
            signalingMediaStarted = false
            inviteCode = created.inviteCode
            statusText = "等待控制端输入连接码 \(created.inviteCode)…"
        } catch {
            client.stop()
            guard signalingEpoch == epoch else { return }
            signaling = nil
            inviteCode = ""
            lastError = error.localizedDescription
            statusText = "信令连接失败"
        }
    }

    @MainActor
    private func acceptViewer(_ view: SessionView) async {
        guard let client = signaling, !signalingAccepted else { return }
        if let current = signalingView, view.sessionId != current.sessionId {
            guard !signalingMediaStarted else { return }
            signalingView = view
        }
        signalingAccepted = true
        let epoch = signalingEpoch
        statusText = "控制端已加入，正在接受…"
        do {
            let accepted = try await client.acceptSession(id: view.sessionId)
            guard signalingEpoch == epoch else { return }
            signalingView = accepted
            let secret = try MediaKeys.deriveMediaSecret(code: inviteCode, sessionId: accepted.sessionId.bytes)
            let core = try CoreSession(secret: secret, sessionID: accepted.sessionId.bytes, isHost: true)
            let bound = try core.bind(videoPort: UInt16(CoreDefaults.hostVideoPort),
                                      controlPort: UInt16(CoreDefaults.hostControlPort))
            self.core = core
            boundPorts = BoundPorts(video: bound.video, control: bound.control)

            let info = PeerMediaInfo(role: PeerMediaInfo.hostRole,
                                     videoPort: bound.video,
                                     controlPort: bound.control,
                                     localAddresses: PeerMediaInfo.localIPv4Addresses())
            try await client.sendPeerKey(type: .peerKeyOffer,
                                         sessionId: accepted.sessionId,
                                         payload: Data(info.encode()))
            guard signalingEpoch == epoch else { return }
            statusText = "控制端已加入，正在交换媒体地址…"
        } catch {
            guard signalingEpoch == epoch else { return }
            signalingAccepted = false
            lastError = error.localizedDescription
            resetSignaling()
            statusText = "配对失败"
        }
    }

    @MainActor
    private func handlePeerKey(_ message: PeerKeyMessage) {
        guard message.type == .peerKeyAnswer,
              let current = signalingView,
              message.sessionId == current.sessionId,
              !signalingMediaStarted,
              let peer = PeerMediaInfo.decode(Data(message.payload)) else { return }
        guard let address = PeerMediaInfo.choosePeerAddress(peer: peer,
                                                            peerObserved: current.viewerEndpoint,
                                                            ownObserved: current.hostEndpoint) else {
            lastError = "无法确定对端地址。"
            return
        }
        signalingMediaStarted = true
        let epoch = signalingEpoch
        Task { @MainActor in
            guard self.signalingEpoch == epoch else { return }
            do {
                try await self.startSignalingMedia(address: address, peer: peer)
                if self.signalingEpoch != epoch {
                    await self.cleanup()
                }
            } catch {
                guard self.signalingEpoch == epoch else { return }
                self.lastError = error.localizedDescription
                await self.cleanup()
                self.resetSignaling()
                self.statusText = "启动失败"
            }
        }
    }

    @MainActor
    private func startSignalingMedia(address: String, peer: PeerMediaInfo) async throws {
        guard let core, let display = pendingDisplay else { return }
        try core.setPeer(videoAddress: address, videoPort: peer.videoPort,
                         controlAddress: address, controlPort: peer.controlPort)
        try core.start()
        peerSummary = "\(address):\(peer.videoPort)/\(peer.controlPort)"
        try await beginCapture(display: display, target: pendingTarget, fps: pendingFPS, bitrate: pendingBitrate)
        let videoPort = boundPorts?.video ?? 0
        let controlPort = boundPorts?.control ?? 0
        statusText = "共享中：\(display.name) \(pendingTarget.width)x\(pendingTarget.height)@\(pendingFPS) · 本机端口 \(videoPort)/\(controlPort) · 对端 \(address):\(peer.videoPort)/\(peer.controlPort)"
    }

    @MainActor
    private func handleSignalingClosed(_ view: SessionView) async {
        if let current = signalingView, view.sessionId != current.sessionId { return }
        if signalingMediaStarted {
            signalingAccepted = false
            signalingMediaStarted = false
            await cleanup()
            statusText = "控制端已离开，等待下一次连接…"
            return
        }
        signalingEpoch += 1
        signaling?.stop()
        signaling = nil
        signalingView = nil
        signalingAccepted = false
        inviteCode = ""
        core?.close()
        core = nil
        boundPorts = nil
        statusText = "连接已结束，正在刷新…"
        await openSignaling()
    }

    @MainActor
    private func resetSignaling() {
        signalingEpoch += 1
        signaling?.stop()
        signaling = nil
        signalingView = nil
        signalingAccepted = false
        signalingMediaStarted = false
        inviteCode = ""
        peerSummary = ""
        core?.close()
        core = nil
        boundPorts = nil
    }

    @MainActor
    private func beginCapture(display: CaptureDisplay,
                              target: (width: Int, height: Int),
                              fps: Int,
                              bitrate: Int) async throws {
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
        startPolling()
    }

    @MainActor
    func stop() async {
        guard running || core != nil || signaling != nil else { return }
        signalingEpoch += 1
        // Releasing held keys is the last thing that must happen on this machine.
        injector?.releaseAll()
        signaling?.stop()
        signaling = nil
        signalingView = nil
        signalingAccepted = false
        signalingMediaStarted = false
        inviteCode = ""
        peerSummary = ""
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
        peerSummary = ""
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
