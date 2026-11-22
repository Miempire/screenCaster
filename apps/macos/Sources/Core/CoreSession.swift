//
//  CoreSession.swift
//  Swift wrapper around the 14-function C ABI in `native/include/screencast_core.h`.
//
//  Ownership rules taken straight from the header:
//   * `sc_event_t.data` points into memory owned by the session and is only valid until the next
//     poll that returns a body, or until `sc_frame_release`. `poll()` therefore copies the bytes
//     immediately and releases the native buffer before returning.
//   * `sc_session_destroy` stops the native threads. Nothing may touch the handle afterwards, so
//     callers must stop their poll loop before calling `close()`.
//

import Foundation

enum CoreStatus {
    static func describe(_ code: Int32) -> String {
        switch code {
        case 0: return "SC_OK"
        case -1: return "SC_ERR_NULL"
        case -2: return "SC_ERR_INVALID_ARGUMENT"
        case -3: return "SC_ERR_NOT_BOUND"
        case -4: return "SC_ERR_ALREADY_BOUND"
        case -5: return "SC_ERR_IO"
        case -6: return "SC_ERR_STATE"
        case -7: return "SC_ERR_TOO_LARGE"
        case -8: return "SC_ERR_PROTOCOL"
        default: return "未知状态 \(code)"
        }
    }
}

enum CoreError: LocalizedError {
    case invalidSecret
    case invalidSessionID
    case createFailed
    case callFailed(String, Int32)

    var errorDescription: String? {
        switch self {
        case .invalidSecret:
            return "共享密钥必须是 32 字节。"
        case .invalidSessionID:
            return "会话 ID 必须是 16 字节。"
        case .createFailed:
            return "sc_session_create 返回 NULL（参数非法或 epoch 为 0）。"
        case let .callFailed(name, code):
            return "\(name) 失败：\(CoreStatus.describe(code))"
        }
    }
}

/// One event drained from `sc_session_poll`.
struct CoreEvent {
    enum Kind: Int32 {
        case none = 0
        case videoFrame = 1
        case control = 2
        case path = 3
        case controlExpired = 4
    }

    let kind: Kind
    /// `SC_PATH_*` for `.path`, otherwise 0.
    let status: Int32
    /// 0 video, 1 control for `.path`.
    let component: Int32
    let eventType: UInt16
    let flags: UInt8
    let codec: UInt8
    let frameID: UInt64
    let captureTimestampUS: UInt64
    let rttUS: UInt64
    let configID: UInt32
    let payload: [UInt8]

    var isKeyFrame: Bool { (flags & CoreDefaults.frameFlagKeyFrame) != 0 }
}

/// Snapshot of `sc_stats_t`.
struct CoreStats {
    var videoDatagramsReceived: UInt64 = 0
    var videoDatagramsRejected: UInt64 = 0
    var controlDatagramsReceived: UInt64 = 0
    var controlDatagramsRejected: UInt64 = 0
    var controlFramesDropped: UInt64 = 0
    var assembledFrames: UInt64 = 0
    var droppedFrames: UInt64 = 0
    var droppedFragments: UInt64 = 0
    var videoBytesSent: UInt64 = 0
    var controlBytesSent: UInt64 = 0
    var replayRejected: UInt64 = 0
    var probesSent: UInt64 = 0
    var probesAnswered: UInt64 = 0
    var eventsDropped: UInt64 = 0
    var partialFrames: UInt64 = 0
    /// -1 when no sample exists yet.
    var videoRTTMicroseconds: Double = -1
    var controlRTTMicroseconds: Double = -1
    /// `SC_PATH_*`.
    var videoPathStatus: Int32 = 0
    var controlPathStatus: Int32 = 0
    var videoLocalPort: UInt16 = 0
    var controlLocalPort: UInt16 = 0

    var videoRTTMilliseconds: Double { videoRTTMicroseconds < 0 ? -1 : videoRTTMicroseconds / 1000 }
    var controlRTTMilliseconds: Double { controlRTTMicroseconds < 0 ? -1 : controlRTTMicroseconds / 1000 }

    static func pathName(_ status: Int32) -> String {
        switch status {
        case 0: return "探测中"
        case 1: return "通"
        case 2: return "断"
        default: return "未知"
        }
    }
}

/// Thin, synchronous owner of one `sc_session_t`.
final class CoreSession {
    private var handle: OpaquePointer?
    let isHost: Bool
    let videoPortHint: UInt16
    let controlPortHint: UInt16

    init(secret: [UInt8], sessionID: [UInt8], epoch: UInt32 = CoreDefaults.keyEpoch, isHost: Bool,
         videoPort: UInt16 = 0, controlPort: UInt16 = 0) throws {
        guard secret.count == 32 else { throw CoreError.invalidSecret }
        guard sessionID.count == 16 else { throw CoreError.invalidSessionID }

        self.isHost = isHost
        self.videoPortHint = videoPort
        self.controlPortHint = controlPort

        let created: OpaquePointer? = secret.withUnsafeBufferPointer { secretBuffer in
            sessionID.withUnsafeBufferPointer { idBuffer in
                sc_session_create(secretBuffer.baseAddress, idBuffer.baseAddress, epoch, isHost)
            }
        }
        guard let created else { throw CoreError.createFailed }
        handle = created
    }

    deinit {
        close()
    }

    /// Idempotent. Safe to call on a session that never bound.
    func close() {
        guard let handle else { return }
        self.handle = nil
        sc_session_destroy(handle)
    }

    var isOpen: Bool { handle != nil }

    /// Binds both media sockets on 0.0.0.0 and returns the ports that were actually assigned.
    func bind(videoPort: UInt16, controlPort: UInt16) throws -> (video: UInt16, control: UInt16) {
        guard let handle else { throw CoreError.callFailed("sc_session_bind", -1) }
        let result = sc_session_bind(handle, videoPort, controlPort)
        if result < 0 {
            throw CoreError.callFailed("sc_session_bind", result)
        }

        var video: UInt16 = 0
        var control: UInt16 = 0
        let portResult = sc_session_local_ports(handle, &video, &control)
        if portResult < 0 {
            throw CoreError.callFailed("sc_session_local_ports", portResult)
        }
        return (video, control)
    }

    /// Addresses must be literal IPv4/IPv6 text; the C ABI performs no DNS resolution.
    func setPeer(videoAddress: String, videoPort: UInt16,
                 controlAddress: String, controlPort: UInt16) throws {
        guard let handle else { throw CoreError.callFailed("sc_session_set_peer", -1) }
        let result = videoAddress.withCString { videoPointer in
            controlAddress.withCString { controlPointer in
                sc_session_set_peer(handle, videoPointer, videoPort, controlPointer, controlPort)
            }
        }
        if result < 0 {
            throw CoreError.callFailed("sc_session_set_peer", result)
        }
    }

    func start() throws {
        guard let handle else { throw CoreError.callFailed("sc_session_start", -1) }
        let result = sc_session_start(handle)
        if result < 0 {
            throw CoreError.callFailed("sc_session_start", result)
        }
    }

    @discardableResult
    func sendFrame(_ data: [UInt8], frameID: UInt64, timestampUS: UInt64,
                   configID: UInt32 = CoreDefaults.configID,
                   flags: UInt8 = 0, codec: UInt8 = CoreDefaults.codecH264) -> Int32 {
        guard let handle, !data.isEmpty else { return -2 }
        return data.withUnsafeBufferPointer { buffer in
            sc_session_send_frame(handle, buffer.baseAddress, UInt32(data.count),
                                  frameID, timestampUS, configID, flags, codec)
        }
    }

    @discardableResult
    func sendControl(_ type: ControlEventType, body: [UInt8] = []) -> Int32 {
        guard let handle else { return -2 }
        if body.isEmpty {
            return sc_session_send_control(handle, type.rawValue, nil, 0)
        }
        return body.withUnsafeBufferPointer { buffer in
            sc_session_send_control(handle, type.rawValue, buffer.baseAddress, UInt16(body.count))
        }
    }

    /// Non-blocking. Returns nil when the queue is empty or the session is closed.
    func poll() -> CoreEvent? {
        guard let handle else { return nil }
        var raw = sc_event_t()
        let result = sc_session_poll(handle, &raw)
        guard result == 1 else { return nil }

        var payload: [UInt8] = []
        if let pointer = raw.data, raw.data_len > 0 {
            payload = Array(UnsafeBufferPointer(start: pointer, count: Int(raw.data_len)))
        }
        // The native buffer behind `raw.data` dies on the next poll that returns a body.
        sc_frame_release(handle)

        return CoreEvent(
            kind: CoreEvent.Kind(rawValue: raw.kind) ?? .none,
            status: raw.status,
            component: raw.component,
            eventType: raw.event_type,
            flags: raw.flags,
            codec: raw.codec,
            frameID: raw.frame_id,
            captureTimestampUS: raw.capture_timestamp_us,
            rttUS: raw.rtt_us,
            configID: raw.config_id,
            payload: payload)
    }

    func stats() -> CoreStats {
        guard let handle else { return CoreStats() }
        var raw = sc_stats_t()
        sc_session_stats(handle, &raw)
        return CoreStats(
            videoDatagramsReceived: raw.video_datagrams_received,
            videoDatagramsRejected: raw.video_datagrams_rejected,
            controlDatagramsReceived: raw.control_datagrams_received,
            controlDatagramsRejected: raw.control_datagrams_rejected,
            controlFramesDropped: raw.control_frames_dropped,
            assembledFrames: raw.assembled_frames,
            droppedFrames: raw.dropped_frames,
            droppedFragments: raw.dropped_fragments,
            videoBytesSent: raw.video_bytes_sent,
            controlBytesSent: raw.control_bytes_sent,
            replayRejected: raw.replay_rejected,
            probesSent: raw.probes_sent,
            probesAnswered: raw.probes_answered,
            eventsDropped: raw.events_dropped,
            partialFrames: raw.partial_frames,
            videoRTTMicroseconds: raw.video_rtt_us,
            controlRTTMicroseconds: raw.control_rtt_us,
            videoPathStatus: raw.video_path_status,
            controlPathStatus: raw.control_path_status,
            videoLocalPort: raw.video_local_port,
            controlLocalPort: raw.control_local_port)
    }
}

/// `sc_core_version` formatted as major.minor.patch.
enum CoreVersion {
    static var text: String {
        let value = sc_core_version()
        return "\((value >> 16) & 0xFF).\((value >> 8) & 0xFF).\(value & 0xFF)"
    }
}
