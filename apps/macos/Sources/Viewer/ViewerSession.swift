//
//  ViewerSession.swift
//  The controlling side.
//
//  Mirrors `src/ScreenCast.Windows/Session/ViewerSession.cs`: receive encrypted fragments through
//  the Rust core, decode each assembled frame, show it, and turn local input into control events.
//  Two deliberate differences:
//
//   * The Rust core owns the sockets, so there is no transport class here and no `Tick()`: the
//     native session runs its own receive loops and ticker.
//   * A dropped frame means the reference chain is broken, so the viewer asks for a new key frame
//     at most every 500 ms (same rate limit as the Windows build).
//

import AVFoundation
import CoreMedia
import Foundation

struct ViewerStats: Equatable {
    var width = 0
    var height = 0
    var decodedFrames = 0
    var renderedFrames = 0
    var droppedFrames = 0
    var rejectedDatagrams = 0
    var assembledFrames = 0
    var receivedBytes = 0
    var fps = 0.0
    var decodeMilliseconds = 0.0
    var videoRTTMilliseconds = -1.0
    var controlRTTMilliseconds = -1.0
    var videoPath = "—"
    var controlPath = "—"
}

final class ViewerSession: ObservableObject {
    // MARK: - Published state (main thread only)

    @Published private(set) var stats = ViewerStats()
    @Published private(set) var statusText = "未连接"
    @Published private(set) var connected = false
    @Published private(set) var boundPorts: BoundPorts?
    @Published private(set) var lastError = ""

    @Published var hostAddress = "127.0.0.1"
    @Published var hostVideoPort = String(CoreDefaults.hostVideoPort)
    @Published var hostControlPort = String(CoreDefaults.hostControlPort)
    @Published var localVideoPort = String(CoreDefaults.viewerVideoPort)
    @Published var localControlPort = String(CoreDefaults.viewerControlPort)

    /// The picture surface. Created once so SwiftUI's representable can hold on to it.
    let surface = RemoteSurfaceView()

    // MARK: - Session state

    private var core: CoreSession?
    private var decoder: H264Decoder?
    private var exitSemaphore: DispatchSemaphore?
    private let stopFlag = LockedFlag(true)
    private let counters = CounterBox()
    private var statsTimer: Timer?

    private let keyFrameLock = NSLock()
    private var lastKeyFrameRequest: CFAbsoluteTime = 0
    private var lastDroppedObserved: UInt64 = 0

    private var lastFPSStamp: CFAbsoluteTime = 0
    private var fpsAtStamp = 0

    // MARK: - Lifecycle

    func connect() {
        guard !connected else { return }

        guard let hostVideo = UInt16(hostVideoPort.trimmingCharacters(in: .whitespaces)),
              let hostControl = UInt16(hostControlPort.trimmingCharacters(in: .whitespaces)) else {
            lastError = "主机端口不是 0..65535 的整数。"
            return
        }
        let localVideo = UInt16(localVideoPort.trimmingCharacters(in: .whitespaces)) ?? 0
        let localControl = UInt16(localControlPort.trimmingCharacters(in: .whitespaces)) ?? 0

        guard let literal = HostResolver.literal(for: hostAddress) else {
            lastError = "地址 \(hostAddress) 无法解析为 IP 字面量（C ABI 不做 DNS）。"
            return
        }

        do {
            let core = try CoreSession(secret: CoreDefaults.sharedSecret,
                                       sessionID: CoreDefaults.sessionID,
                                       isHost: false)
            let bound = try core.bind(videoPort: localVideo, controlPort: localControl)
            try core.setPeer(videoAddress: literal, videoPort: hostVideo,
                             controlAddress: literal, controlPort: hostControl)
            try core.start()

            self.core = core
            boundPorts = BoundPorts(video: bound.video, control: bound.control)

            let decoder = H264Decoder()
            decoder.onFrame = { [weak self] sampleBuffer, size in
                self?.present(sampleBuffer, size: size)
            }
            decoder.onError = { [weak self] message in
                DispatchQueue.main.async { self?.lastError = message }
            }
            self.decoder = decoder

            wireSurface()
            counters.reset()
            lastDroppedObserved = 0
            lastFPSStamp = CFAbsoluteTimeGetCurrent()
            fpsAtStamp = 0
            stats = ViewerStats()

            connected = true
            lastError = ""
            statusText = "已连接 \(literal):\(hostVideo)/\(hostControl)，本机端口 \(bound.video)/\(bound.control)"

            // Take the input lease and force a key frame so the first picture is complete.
            core.sendControl(.controlLease, body: ControlWire.lease(acquire: true))
            requestKeyFrame(force: true)

            startPolling()
        } catch {
            lastError = error.localizedDescription
            statusText = "连接失败"
            self.core?.close()
            self.core = nil
            decoder?.close()
            decoder = nil
            connected = false
        }
    }

    func disconnect() {
        guard core != nil else { return }

        // Release every key on the remote machine before the session goes away.
        surface.releaseEverything()
        core?.sendControl(.releaseAll)
        core?.sendControl(.controlLease, body: ControlWire.lease(acquire: false))

        stopFlag.set(true)
        _ = exitSemaphore?.wait(timeout: .now() + 3)
        exitSemaphore = nil

        statsTimer?.invalidate()
        statsTimer = nil

        decoder?.close()
        decoder = nil
        core?.close()
        core = nil

        surface.flush()
        boundPorts = nil
        connected = false
        statusText = "已断开"
    }

    // MARK: - Input wiring

    private func wireSurface() {
        surface.onKey = { [weak self] scanCode, extended, isDown in
            self?.core?.sendControl(.key, body: ControlWire.key(scanCode: scanCode, extended: extended, isDown: isDown))
        }
        surface.onMouseMove = { [weak self] x, y in
            self?.core?.sendControl(.mouseMove, body: ControlWire.mouseMove(x: x, y: y))
        }
        surface.onMouseButton = { [weak self] button, isDown in
            self?.core?.sendControl(.mouseButton, body: ControlWire.mouseButton(button, isDown: isDown))
        }
        surface.onWheel = { [weak self] deltaX, deltaY in
            self?.core?.sendControl(.wheel, body: ControlWire.wheel(deltaX: deltaX, deltaY: deltaY))
        }
        surface.onReleaseAll = { [weak self] in
            self?.core?.sendControl(.releaseAll)
        }
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

            if let core {
                let native = core.stats()
                if native.droppedFrames > lastDroppedObserved {
                    lastDroppedObserved = native.droppedFrames
                    requestKeyFrame(force: false)
                }
            }

            if !handledAny {
                usleep(1000)
            }
        }
    }

    private func handle(_ event: CoreEvent) {
        switch event.kind {
        case .videoFrame:
            guard event.codec == CoreDefaults.codecH264 else { return }
            counters.add("bytes", event.payload.count)
            counters.add("assembled")
            if event.isKeyFrame {
                counters.add("keyFrames")
            }
            decoder?.decode(annexB: event.payload, presentationTimeUS: event.captureTimestampUS)

        case .control:
            // The viewer has nothing to inject; a control event here would come from another viewer.
            break

        case .controlExpired:
            // The reliable channel gave up on an event. The host releases its own held keys.
            break

        case .path:
            break

        case .none:
            break
        }
    }

    private func present(_ sampleBuffer: CMSampleBuffer, size: CGSize) {
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.surface.videoSize = size
            self.surface.enqueue(sampleBuffer)
            self.counters.add("rendered")
        }
    }

    // MARK: - Key frame recovery

    private func requestKeyFrame(force: Bool) {
        guard let core else { return }
        let now = CFAbsoluteTimeGetCurrent()

        keyFrameLock.lock()
        let tooSoon = !force && (now - lastKeyFrameRequest) < CoreDefaults.keyFrameRequestInterval
        if !tooSoon {
            lastKeyFrameRequest = now
        }
        keyFrameLock.unlock()

        if tooSoon { return }
        core.sendControl(.requestKeyFrame)
    }

    // MARK: - Stats

    private func refreshStats() {
        guard let core, let decoder else { return }
        let native = core.stats()
        let now = CFAbsoluteTimeGetCurrent()

        var updated = ViewerStats()
        updated.width = Int(surface.videoSize.width)
        updated.height = Int(surface.videoSize.height)
        updated.decodedFrames = decoder.decodedFrames
        updated.renderedFrames = counters.value("rendered")
        updated.droppedFrames = Int(native.droppedFrames) + decoder.droppedFrames
        updated.rejectedDatagrams = Int(native.videoDatagramsRejected)
        updated.assembledFrames = Int(native.assembledFrames)
        updated.receivedBytes = counters.value("bytes")
        updated.decodeMilliseconds = decoder.averageDecodeMilliseconds
        updated.videoRTTMilliseconds = native.videoRTTMilliseconds
        updated.controlRTTMilliseconds = native.controlRTTMilliseconds
        updated.videoPath = CoreStats.pathName(native.videoPathStatus)
        updated.controlPath = CoreStats.pathName(native.controlPathStatus)

        let elapsed = now - lastFPSStamp
        if elapsed >= 0.5 {
            updated.fps = Double(updated.renderedFrames - fpsAtStamp) / elapsed
            fpsAtStamp = updated.renderedFrames
            lastFPSStamp = now
        } else {
            updated.fps = stats.fps
        }

        stats = updated
    }
}
