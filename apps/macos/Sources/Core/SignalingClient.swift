import Foundation
import Network

enum DeviceIdentity {
    private static let storageKey = "screencast.macos.device-id"

    static func current(defaults: UserDefaults = .standard) -> String {
        if let existing = defaults.string(forKey: storageKey), isValid(existing) {
            return existing
        }
        var generator = SystemRandomNumberGenerator()
        let bytes = (0 ..< 8).map { _ in UInt8.random(in: UInt8.min ... UInt8.max, using: &generator) }
        let text = bytes.map { String(format: "%02x", $0) }.joined()
        defaults.set(text, forKey: storageKey)
        return text
    }

    static func isValid(_ text: String) -> Bool {
        guard text.count == 16 else { return false }
        return text.allSatisfy { character in
            guard let scalar = character.unicodeScalars.first else { return false }
            return (scalar.value >= 48 && scalar.value <= 57) ||
                (scalar.value >= 97 && scalar.value <= 102)
        }
    }

    private static let lastCodeKey = "screencast.macos.last-code"

    static var lastCode: String {
        guard let text = UserDefaults.standard.string(forKey: lastCodeKey),
              text.count == 9, text.first != "0",
              text.allSatisfy({ $0.unicodeScalars.first.map { $0.value >= 48 && $0.value <= 57 } == true })
        else {
            return ""
        }
        return text
    }

    static func rememberCode(_ code: String) {
        guard code.count == 9, code.first != "0" else { return }
        UserDefaults.standard.set(code, forKey: lastCodeKey)
    }
}

enum SignalingClientError: LocalizedError {
    case connectionFailed(String)
    case timeout(String)
    case closed

    var errorDescription: String? {
        switch self {
        case let .connectionFailed(detail):
            return "连接信令服务器失败：\(detail)"
        case let .timeout(operation):
            return "信令请求超时：\(operation)"
        case .closed:
            return "信令连接已关闭。"
        }
    }
}

final class PeerKeyWaiter {
    let messageType: SignalingMessageType

    private let lock = NSLock()
    private var result: Result<PeerKeyMessage, Error>?
    private var continuation: CheckedContinuation<PeerKeyMessage, Error>?
    private var timer: DispatchWorkItem?

    init(messageType: SignalingMessageType) {
        self.messageType = messageType
    }

    func resolve(_ message: PeerKeyMessage) {
        lock.lock()
        guard result == nil else {
            lock.unlock()
            return
        }
        result = .success(message)
        let pending = continuation
        continuation = nil
        let work = timer
        timer = nil
        lock.unlock()
        work?.cancel()
        pending?.resume(returning: message)
    }

    func fail(_ error: Error) {
        lock.lock()
        guard result == nil else {
            lock.unlock()
            return
        }
        result = .failure(error)
        let pending = continuation
        continuation = nil
        let work = timer
        timer = nil
        lock.unlock()
        work?.cancel()
        pending?.resume(throwing: error)
    }

    func wait(timeout: TimeInterval) async throws -> PeerKeyMessage {
        try await withCheckedThrowingContinuation { continuation in
            lock.lock()
            if let result {
                lock.unlock()
                continuation.resume(with: result)
                return
            }
            self.continuation = continuation
            let work = DispatchWorkItem { [weak self] in
                self?.fail(SignalingClientError.timeout("PeerKey"))
            }
            timer = work
            lock.unlock()
            DispatchQueue.global().asyncAfter(deadline: .now() + timeout, execute: work)
        }
    }
}

final class SignalingClient {
    static let serverHost = "jsspace.xyz"
    static let serverPort: UInt16 = 3479
    static let rolesAll: UInt8 = DeviceRoleRules.all
    static let maxAttempts = 5
    static let retryInterval: TimeInterval = 0.5
    static let requestTimeout: TimeInterval = 15

    let deviceId: String
    let roles: UInt8

    var onRegistered: (() -> Void)?
    var onJoinRequested: ((SessionView) -> Void)?
    var onSessionAccepted: ((SessionView) -> Void)?
    var onSessionClosed: ((SessionView) -> Void)?
    var onPeerKey: ((PeerKeyMessage) -> Void)?
    var onError: ((SignalingException) -> Void)?
    var onFault: ((Error) -> Void)?

    private let queue = DispatchQueue(label: "org.screencast.macos.signaling")
    private let waiterLock = NSLock()
    private var peerKeyWaiters: [PeerKeyWaiter] = []
    private var connection: NWConnection?
    private var pending: [String: (Result<Data, Error>) -> Void] = [:]
    private var readyHandler: ((Result<Void, Error>) -> Void)?
    private var heartbeatTimer: DispatchSourceTimer?
    private var heartbeatInterval: TimeInterval = 15
    private var started = false
    private var closed = false

    init(deviceId: String = DeviceIdentity.current(), roles: UInt8 = SignalingClient.rolesAll) {
        self.deviceId = deviceId
        self.roles = roles
    }

    deinit {
        heartbeatTimer?.cancel()
        connection?.cancel()
    }

    func makePeerKeyWaiter(for type: SignalingMessageType) -> PeerKeyWaiter {
        let waiter = PeerKeyWaiter(messageType: type)
        waiterLock.lock()
        peerKeyWaiters.append(waiter)
        waiterLock.unlock()
        return waiter
    }

    func start() async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            queue.async {
                self.openConnection { result in
                    continuation.resume(with: result)
                }
            }
        }

        let reply = try await request(.register, write: registerBody)
        guard let header = SignalingWire.parseHeader(reply), header.type == .registered else {
            throw SignalingException(code: .malformed, detail: "registration reply is invalid")
        }
        var reader = try SignalingReader(bytes: [UInt8](reply), header: header)
        _ = try reader.text(maxBytes: DeviceRoleRules.maxDeviceIdBytes)
        _ = try reader.u8()
        let interval = try reader.u32()
        _ = try reader.u32()
        try reader.requireEnd()

        queue.async {
            guard !self.closed else { return }
            self.heartbeatInterval = max(1, TimeInterval(interval) / 1000)
            self.started = true
            self.startHeartbeat()
        }
        DispatchQueue.main.async { [weak self] in
            self?.onRegistered?()
        }
    }

    func createSession() async throws -> SessionView {
        try await requestSession(.createSession) { writer in
            try writer.text(self.deviceId)
            try writer.text("")
        }
    }

    func joinSession(code: String) async throws -> SessionView {
        let trimmed = code.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else {
            throw SignalingException(code: .malformed, detail: "invite code is required")
        }
        return try await requestSession(.joinSession) { writer in
            try writer.text(trimmed, maxBytes: 16)
            writer.id(.zero)
        }
    }

    func acceptSession(id: SignalingId) async throws -> SessionView {
        try await requestSession(.acceptSession) { writer in
            writer.id(id)
        }
    }

    func closeSession(id: SignalingId) async throws -> SessionView {
        try await requestSession(.closeSession) { writer in
            writer.id(id)
            writer.u8(SessionCloseReason.userClosed.rawValue)
        }
    }

    func sendPeerKey(type: SignalingMessageType, sessionId: SignalingId, payload: Data) async throws {
        guard type == .peerKeyOffer || type == .peerKeyAnswer else {
            throw SignalingException(code: .unsupported, detail: "peer key type is invalid")
        }
        guard payload.count <= SignalingWire.maxBlobBytes else {
            throw SignalingWireError.oversized("peer key")
        }
        let reply = try await request(type) { writer in
            writer.id(sessionId)
            writer.id(.zero)
            try writer.blob([UInt8](payload))
        }
        guard let header = SignalingWire.parseHeader(reply), header.type == .ack else {
            throw SignalingException(code: .malformed, detail: "peer key reply is invalid")
        }
    }

    func stop() {
        queue.async {
            guard !self.closed else { return }
            self.closed = true
            self.heartbeatTimer?.cancel()
            self.heartbeatTimer = nil
            self.connection?.cancel()
            self.connection = nil
            self.completeReady(.failure(SignalingClientError.closed))
            self.failAll(SignalingClientError.closed)
        }
    }

    private func registerBody(_ writer: SignalingWriter) throws {
        try writer.text(deviceId)
        writer.u8(roles)
        writer.u8(0)
        try writer.blob([])
        try writer.endpoints([])
    }

    private func requestSession(_ type: SignalingMessageType,
                                write: @escaping (SignalingWriter) throws -> Void) async throws -> SessionView {
        let reply = try await request(type, write: write)
        guard let header = SignalingWire.parseHeader(reply) else {
            throw SignalingException(code: .malformed, detail: "session reply is invalid")
        }
        return try SessionView.decode(bytes: [UInt8](reply), header: header)
    }

    private func request(_ type: SignalingMessageType,
                         write: @escaping (SignalingWriter) throws -> Void) async throws -> Data {
        let writer = SignalingWriter()
        try write(writer)
        let requestId = SignalingId.random()
        let message = writer.message(type: type, requestId: requestId)
        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Data, Error>) in
            queue.async {
                self.beginRequest(type: type, message: message, requestId: requestId,
                                  timeout: SignalingClient.requestTimeout) { result in
                    continuation.resume(with: result)
                }
            }
        }
    }

    private func openConnection(_ completion: @escaping (Result<Void, Error>) -> Void) {
        if connection != nil {
            completion(.success(()))
            return
        }
        let parameters = NWParameters.udp
        parameters.allowLocalEndpointReuse = true
        guard let port = NWEndpoint.Port(rawValue: SignalingClient.serverPort) else {
            completion(.failure(SignalingClientError.connectionFailed("端口无效")))
            return
        }
        let connection = NWConnection(to: NWEndpoint.hostPort(host: NWEndpoint.Host(SignalingClient.serverHost),
                                                             port: port),
                                      using: parameters)
        self.connection = connection
        readyHandler = completion
        connection.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready:
                self.completeReady(.success(()))
            case let .failed(error):
                let failure = SignalingClientError.connectionFailed(error.localizedDescription)
                self.closed = true
                self.completeReady(.failure(failure))
                self.failAll(failure)
            case .cancelled:
                self.completeReady(.failure(SignalingClientError.closed))
                self.failAll(SignalingClientError.closed)
            default:
                break
            }
        }
        connection.start(queue: queue)
        startReceive()
        queue.asyncAfter(deadline: .now() + SignalingClient.requestTimeout) { [weak self] in
            self?.completeReady(.failure(SignalingClientError.timeout("连接服务器")))
        }
    }

    private func completeReady(_ result: Result<Void, Error>) {
        guard let handler = readyHandler else { return }
        readyHandler = nil
        handler(result)
    }

    private func startReceive() {
        guard let connection else { return }
        connection.receiveMessage { [weak self] data, _, _, error in
            guard let self else { return }
            if let data, !data.isEmpty {
                self.dispatch(data)
            }
            if let error {
                self.reportFault(error)
                self.failAll(error)
                return
            }
            if !self.closed {
                self.startReceive()
            }
        }
    }

    private func dispatch(_ data: Data) {
        guard let header = SignalingWire.parseHeader(data) else { return }
        let bytes = [UInt8](data)

        if header.type == .error {
            let exception: SignalingException
            do {
                var reader = try SignalingReader(bytes: bytes, header: header)
                let raw = try reader.u16()
                let detail = try reader.text(maxBytes: 100)
                try reader.requireEnd()
                exception = SignalingException(code: SignalingErrorCode(rawValue: raw) ?? .malformed,
                                               detail: detail)
            } catch {
                exception = SignalingException(code: .malformed, detail: "error body is invalid")
            }
            if let callback = pending.removeValue(forKey: header.requestId.hex) {
                callback(.failure(exception))
            } else {
                DispatchQueue.main.async { [weak self] in
                    self?.onError?(exception)
                }
            }
            return
        }

        if let callback = pending.removeValue(forKey: header.requestId.hex) {
            callback(.success(data))
            return
        }

        do {
            switch header.type {
            case .joinRequested:
                let view = try SessionView.decode(bytes: bytes, header: header)
                DispatchQueue.main.async { [weak self] in
                    self?.onJoinRequested?(view)
                }
            case .sessionAccepted:
                let view = try SessionView.decode(bytes: bytes, header: header)
                DispatchQueue.main.async { [weak self] in
                    self?.onSessionAccepted?(view)
                }
            case .sessionClosed:
                let view = try SessionView.decode(bytes: bytes, header: header)
                DispatchQueue.main.async { [weak self] in
                    self?.onSessionClosed?(view)
                }
            case .peerKeyOffer, .peerKeyAnswer:
                let message = try PeerKeyMessage.decode(bytes: bytes, header: header)
                if let waiter = takeWaiter(for: header.type) {
                    waiter.resolve(message)
                } else {
                    DispatchQueue.main.async { [weak self] in
                        self?.onPeerKey?(message)
                    }
                }
            default:
                break
            }
        } catch {
            reportFault(error)
        }
    }

    private func takeWaiter(for type: SignalingMessageType) -> PeerKeyWaiter? {
        waiterLock.lock()
        defer { waiterLock.unlock() }
        guard let index = peerKeyWaiters.firstIndex(where: { $0.messageType == type }) else {
            return nil
        }
        return peerKeyWaiters.remove(at: index)
    }

    private func beginRequest(type: SignalingMessageType, message: Data, requestId: SignalingId,
                              timeout: TimeInterval,
                              completion: @escaping (Result<Data, Error>) -> Void) {
        guard connection != nil, !closed else {
            completion(.failure(SignalingClientError.closed))
            return
        }
        pending[requestId.hex] = completion
        let deadline = Date().addingTimeInterval(timeout)
        sendAttempt(message, requestId: requestId, attempt: 0, deadline: deadline)
        queue.asyncAfter(deadline: .now() + timeout) { [weak self] in
            guard let self, let callback = self.pending.removeValue(forKey: requestId.hex) else {
                return
            }
            callback(.failure(SignalingClientError.timeout(SignalingClient.describe(type))))
        }
    }

    private func sendAttempt(_ message: Data, requestId: SignalingId, attempt: Int, deadline: Date) {
        guard pending[requestId.hex] != nil, let connection else { return }
        connection.send(content: message, completion: .contentProcessed { [weak self] error in
            guard let self, let error else { return }
            if let callback = self.pending.removeValue(forKey: requestId.hex) {
                callback(.failure(error))
            }
            self.reportFault(error)
        })
        guard attempt + 1 < SignalingClient.maxAttempts else { return }
        guard Date().addingTimeInterval(SignalingClient.retryInterval) < deadline else { return }
        queue.asyncAfter(deadline: .now() + SignalingClient.retryInterval) { [weak self] in
            guard let self, Date() < deadline else { return }
            self.sendAttempt(message, requestId: requestId, attempt: attempt + 1, deadline: deadline)
        }
    }

    private func startHeartbeat() {
        heartbeatTimer?.cancel()
        let interval = max(1, heartbeatInterval)
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + interval, repeating: interval)
        timer.setEventHandler { [weak self] in
            self?.heartbeat()
        }
        timer.resume()
        heartbeatTimer = timer
    }

    private func heartbeat() {
        guard started, !closed else { return }
        let writer = SignalingWriter()
        let requestId = SignalingId.random()
        let message = writer.message(type: .heartbeat, requestId: requestId)
        beginRequest(type: .heartbeat, message: message, requestId: requestId,
                     timeout: SignalingClient.requestTimeout) { [weak self] result in
            guard let self else { return }
            guard case let .failure(error) = result else { return }
            if let signaling = error as? SignalingException {
                if signaling.code == .unregistered {
                    self.reregister()
                } else {
                    self.reportFault(error)
                }
            } else if let clientError = error as? SignalingClientError {
                if case .closed = clientError { return }
            } else {
                self.reportFault(error)
            }
        }
    }

    private func reregister() {
        let writer = SignalingWriter()
        do {
            try registerBody(writer)
        } catch {
            reportFault(error)
            return
        }
        let requestId = SignalingId.random()
        let message = writer.message(type: .register, requestId: requestId)
        beginRequest(type: .register, message: message, requestId: requestId,
                     timeout: SignalingClient.requestTimeout) { [weak self] result in
            guard let self else { return }
            switch result {
            case .success:
                DispatchQueue.main.async { [weak self] in
                    self?.onRegistered?()
                }
            case let .failure(error):
                self.reportFault(error)
            }
        }
    }

    private func failAll(_ error: Error) {
        let callbacks = pending
        pending.removeAll()
        for callback in callbacks.values {
            callback(.failure(error))
        }
        waiterLock.lock()
        let waiters = peerKeyWaiters
        peerKeyWaiters.removeAll()
        waiterLock.unlock()
        for waiter in waiters {
            waiter.fail(error)
        }
    }

    private func reportFault(_ error: Error) {
        DispatchQueue.main.async { [weak self] in
            self?.onFault?(error)
        }
    }

    private static func describe(_ type: SignalingMessageType) -> String {
        switch type {
        case .register: return "注册"
        case .heartbeat: return "心跳"
        case .createSession: return "创建会话"
        case .joinSession: return "加入会话"
        case .acceptSession: return "接受会话"
        case .closeSession: return "关闭会话"
        case .peerKeyOffer: return "发送媒体信息"
        case .peerKeyAnswer: return "回复媒体信息"
        default: return "信令"
        }
    }
}
