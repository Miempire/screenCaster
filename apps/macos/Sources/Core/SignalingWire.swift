import Foundation

enum SignalingMessageType: UInt8 {
    case register = 1
    case heartbeat = 2
    case createSession = 3
    case joinSession = 4
    case acceptSession = 5
    case rejectSession = 6
    case punchStart = 7
    case closeSession = 8
    case peerKeyOffer = 9
    case peerKeyAnswer = 10
    case pathCommit = 11
    case registered = 128
    case heartbeatAck = 129
    case sessionCreated = 130
    case sessionJoined = 131
    case sessionAccepted = 132
    case sessionRejected = 133
    case sessionClosed = 134
    case ack = 135
    case error = 136
    case joinRequested = 137
    case punchStartNotify = 138
    case sessionInvited = 139
    case sessionActive = 140
}

enum SignalingErrorCode: UInt16 {
    case malformed = 1
    case unsupported = 2
    case unregistered = 3
    case notFound = 4
    case limitExceeded = 5
    case invalidState = 6
    case notParticipant = 7
    case roleMismatch = 8
    case generationMismatch = 9
    case inviteInvalid = 10
    case busy = 11
}

enum SessionState: UInt8 {
    case created = 0
    case accepted = 1
    case negotiating = 2
    case active = 3
    case closed = 4
}

enum SessionCloseReason: UInt8 {
    case userClosed = 0
    case timeout = 1
    case peerOffline = 2
    case hostRejected = 3
}

enum DeviceRoles: UInt8 {
    case host = 1
    case viewer = 2
}

enum DeviceRoleRules {
    static let all: UInt8 = 3
    static let maxDeviceIdBytes = 64
    static let maxCapabilitiesBytes = 1024
    static let maxCandidates = 8
}

enum SignalingWireError: LocalizedError {
    case malformed(String)
    case oversized(String)

    var errorDescription: String? {
        switch self {
        case let .malformed(detail):
            return "信令报文无效：\(detail)"
        case let .oversized(field):
            return "信令字段超长：\(field)"
        }
    }
}

struct SignalingException: LocalizedError {
    let code: SignalingErrorCode
    let detail: String

    var errorDescription: String? {
        "信令错误 \(code.rawValue)：\(detail)"
    }
}

struct SignalingId: Equatable {
    static let size = 16

    let high: UInt64
    let low: UInt64

    static let zero = SignalingId(high: 0, low: 0)

    var isZero: Bool { high == 0 && low == 0 }

    var hex: String {
        String(format: "%016llx%016llx", high, low)
    }

    var bytes: [UInt8] {
        var output: [UInt8] = []
        output.reserveCapacity(SignalingId.size)
        for shift in stride(from: 56, through: 0, by: -8) {
            output.append(UInt8((high >> UInt64(shift)) & 0xff))
        }
        for shift in stride(from: 56, through: 0, by: -8) {
            output.append(UInt8((low >> UInt64(shift)) & 0xff))
        }
        return output
    }

    static func random() -> SignalingId {
        var generator = SystemRandomNumberGenerator()
        let high = UInt64.random(in: UInt64.min ... UInt64.max, using: &generator)
        let low = UInt64.random(in: UInt64.min ... UInt64.max, using: &generator)
        return SignalingId(high: high, low: low)
    }

    static func from(bytes: [UInt8], offset: Int) -> SignalingId? {
        guard offset >= 0, bytes.count >= offset + SignalingId.size else { return nil }
        var high: UInt64 = 0
        var low: UInt64 = 0
        for index in 0 ..< 8 {
            high = (high << 8) | UInt64(bytes[offset + index])
        }
        for index in 8 ..< 16 {
            low = (low << 8) | UInt64(bytes[offset + index])
        }
        return SignalingId(high: high, low: low)
    }
}

struct SignalingHeader {
    let type: SignalingMessageType
    let requestId: SignalingId
    let bodyLength: Int
}

struct SignalingEndpoint: Equatable {
    let address: String
    let port: UInt16
}

enum SignalingWire {
    static let magic: UInt32 = 0x5343_5347
    static let version: UInt8 = 1
    static let headerSize = 24
    static let maxMessageBytes = 8192
    static let maxBodyBytes = maxMessageBytes - headerSize
    static let maxTextBytes = 64
    static let maxBlobBytes = 4096

    static func encode(type: SignalingMessageType, requestId: SignalingId, body: [UInt8]) -> Data {
        var output: [UInt8] = []
        output.reserveCapacity(headerSize + body.count)
        appendU32(&output, magic)
        output.append(version)
        output.append(type.rawValue)
        appendU16(&output, UInt16(body.count))
        output.append(contentsOf: requestId.bytes)
        output.append(contentsOf: body)
        return Data(output)
    }

    static func parseHeader(_ data: Data) -> SignalingHeader? {
        let bytes = [UInt8](data)
        guard bytes.count >= headerSize, bytes.count <= maxMessageBytes else { return nil }
        guard readU32(bytes, 0) == magic, bytes[4] == version else { return nil }
        let bodyLength = Int(readU16(bytes, 6))
        guard bytes.count == headerSize + bodyLength else { return nil }
        guard let type = SignalingMessageType(rawValue: bytes[5]),
              let requestId = SignalingId.from(bytes: bytes, offset: 8) else { return nil }
        return SignalingHeader(type: type, requestId: requestId, bodyLength: bodyLength)
    }

    static func appendU16(_ output: inout [UInt8], _ value: UInt16) {
        output.append(UInt8((value >> 8) & 0xff))
        output.append(UInt8(value & 0xff))
    }

    static func appendU32(_ output: inout [UInt8], _ value: UInt32) {
        output.append(UInt8((value >> 24) & 0xff))
        output.append(UInt8((value >> 16) & 0xff))
        output.append(UInt8((value >> 8) & 0xff))
        output.append(UInt8(value & 0xff))
    }

    static func appendU64(_ output: inout [UInt8], _ value: UInt64) {
        for shift in stride(from: 56, through: 0, by: -8) {
            output.append(UInt8((value >> UInt64(shift)) & 0xff))
        }
    }

    static func readU16(_ bytes: [UInt8], _ offset: Int) -> UInt16 {
        (UInt16(bytes[offset]) << 8) | UInt16(bytes[offset + 1])
    }

    static func readU32(_ bytes: [UInt8], _ offset: Int) -> UInt32 {
        (UInt32(bytes[offset]) << 24) | (UInt32(bytes[offset + 1]) << 16) |
            (UInt32(bytes[offset + 2]) << 8) | UInt32(bytes[offset + 3])
    }

    static func readU64(_ bytes: [UInt8], _ offset: Int) -> UInt64 {
        var value: UInt64 = 0
        for index in 0 ..< 8 {
            value = (value << 8) | UInt64(bytes[offset + index])
        }
        return value
    }
}

struct SignalingReader {
    private let bytes: [UInt8]
    private var offset: Int
    private let end: Int

    init(bytes: [UInt8], header: SignalingHeader) throws {
        guard header.bodyLength >= 0, header.bodyLength <= SignalingWire.maxBodyBytes else {
            throw SignalingWireError.malformed("body length")
        }
        guard bytes.count >= SignalingWire.headerSize + header.bodyLength else {
            throw SignalingWireError.malformed("truncated datagram")
        }
        self.bytes = bytes
        self.offset = SignalingWire.headerSize
        self.end = SignalingWire.headerSize + header.bodyLength
    }

    var remaining: Int { end - offset }

    mutating func u8() throws -> UInt8 {
        try require(1)
        let value = bytes[offset]
        offset += 1
        return value
    }

    mutating func u16() throws -> UInt16 {
        try require(2)
        let value = SignalingWire.readU16(bytes, offset)
        offset += 2
        return value
    }

    mutating func u32() throws -> UInt32 {
        try require(4)
        let value = SignalingWire.readU32(bytes, offset)
        offset += 4
        return value
    }

    mutating func u64() throws -> UInt64 {
        try require(8)
        let value = SignalingWire.readU64(bytes, offset)
        offset += 8
        return value
    }

    mutating func id() throws -> SignalingId {
        try require(SignalingId.size)
        guard let value = SignalingId.from(bytes: bytes, offset: offset) else {
            throw SignalingWireError.malformed("identifier")
        }
        offset += SignalingId.size
        return value
    }

    mutating func text(maxBytes: Int) throws -> String {
        let length = Int(try u16())
        guard length <= maxBytes else { throw SignalingWireError.oversized("text") }
        try require(length)
        let slice = Array(bytes[offset ..< offset + length])
        offset += length
        guard let value = String(bytes: slice, encoding: .utf8) else {
            throw SignalingWireError.malformed("text encoding")
        }
        return value
    }

    mutating func blob(maxBytes: Int) throws -> [UInt8] {
        let length = Int(try u32())
        guard length <= maxBytes else { throw SignalingWireError.oversized("blob") }
        try require(length)
        let value = Array(bytes[offset ..< offset + length])
        offset += length
        return value
    }

    mutating func endpoint() throws -> SignalingEndpoint? {
        let family = try u8()
        switch family {
        case 0:
            return nil
        case 4:
            try require(4)
            let text = "\(bytes[offset]).\(bytes[offset + 1]).\(bytes[offset + 2]).\(bytes[offset + 3])"
            offset += 4
            let port = try u16()
            return SignalingEndpoint(address: text, port: port)
        case 6:
            try require(16)
            let raw = Array(bytes[offset ..< offset + 16])
            offset += 16
            let port = try u16()
            return SignalingEndpoint(address: SignalingReader.ipv6Text(raw), port: port)
        default:
            throw SignalingWireError.malformed("endpoint family")
        }
    }

    mutating func endpoints(maxCount: Int) throws -> [SignalingEndpoint] {
        let count = Int(try u16())
        guard count <= maxCount else { throw SignalingWireError.oversized("endpoints") }
        var result: [SignalingEndpoint] = []
        result.reserveCapacity(count)
        for _ in 0 ..< count {
            if let endpoint = try endpoint() {
                result.append(endpoint)
            }
        }
        return result
    }

    func requireEnd() throws {
        guard offset == end else { throw SignalingWireError.malformed("trailing bytes") }
    }

    private func require(_ count: Int) throws {
        guard count >= 0, end - offset >= count else {
            throw SignalingWireError.malformed("truncated body")
        }
    }

    private static func ipv6Text(_ raw: [UInt8]) -> String {
        if raw.count == 16, raw[0 ..< 10].allSatisfy({ $0 == 0 }), raw[10] == 0xff, raw[11] == 0xff {
            return "\(raw[12]).\(raw[13]).\(raw[14]).\(raw[15])"
        }
        var storage = in6_addr()
        withUnsafeMutableBytes(of: &storage) { destination in
            raw.withUnsafeBytes { source in
                destination.copyMemory(from: source)
            }
        }
        var buffer = [CChar](repeating: 0, count: Int(INET6_ADDRSTRLEN))
        let result = withUnsafePointer(to: &storage) { pointer in
            pointer.withMemoryRebound(to: CChar.self, capacity: MemoryLayout<in6_addr>.size) { rawPointer in
                inet_ntop(AF_INET6, rawPointer, &buffer, socklen_t(INET6_ADDRSTRLEN))
            }
        }
        return result == nil ? "" : String(cString: buffer)
    }
}

final class SignalingWriter {
    private var body: [UInt8] = []

    func u8(_ value: UInt8) {
        body.append(value)
    }

    func u16(_ value: UInt16) {
        SignalingWire.appendU16(&body, value)
    }

    func u32(_ value: UInt32) {
        SignalingWire.appendU32(&body, value)
    }

    func u64(_ value: UInt64) {
        SignalingWire.appendU64(&body, value)
    }

    func id(_ value: SignalingId) {
        body.append(contentsOf: value.bytes)
    }

    func text(_ value: String, maxBytes: Int = SignalingWire.maxTextBytes) throws {
        let utf8 = Array(value.utf8)
        guard utf8.count <= maxBytes, utf8.count <= Int(UInt16.max) else {
            throw SignalingWireError.oversized("text")
        }
        u16(UInt16(utf8.count))
        body.append(contentsOf: utf8)
    }

    func blob(_ value: [UInt8], maxBytes: Int = SignalingWire.maxBlobBytes) throws {
        guard value.count <= maxBytes else { throw SignalingWireError.oversized("blob") }
        u32(UInt32(value.count))
        body.append(contentsOf: value)
    }

    func endpoint(_ value: SignalingEndpoint?) throws {
        guard let value else {
            u8(0)
            return
        }
        var ipv4 = in_addr()
        let isIPv4 = value.address.withCString { inet_pton(AF_INET, $0, &ipv4) == 1 }
        if isIPv4 {
            u8(4)
            withUnsafeBytes(of: &ipv4) { body.append(contentsOf: $0) }
            u16(value.port)
            return
        }
        var ipv6 = in6_addr()
        let isIPv6 = value.address.withCString { inet_pton(AF_INET6, $0, &ipv6) == 1 }
        if isIPv6 {
            u8(6)
            withUnsafeBytes(of: &ipv6) { body.append(contentsOf: $0) }
            u16(value.port)
            return
        }
        throw SignalingWireError.malformed("endpoint address")
    }

    func endpoints(_ values: [SignalingEndpoint], maxCount: Int = DeviceRoleRules.maxCandidates) throws {
        guard values.count <= maxCount else { throw SignalingWireError.oversized("endpoints") }
        u16(UInt16(values.count))
        for value in values {
            try endpoint(value)
        }
    }

    func message(type: SignalingMessageType, requestId: SignalingId) -> Data {
        SignalingWire.encode(type: type, requestId: requestId, body: body)
    }
}

struct SessionView {
    let type: SignalingMessageType
    let sessionId: SignalingId
    let state: SessionState
    let generation: UInt32
    let role: DeviceRoles
    let inviteCode: String
    let hostId: String
    let viewerId: String
    let hostEndpoint: SignalingEndpoint?
    let viewerEndpoint: SignalingEndpoint?
    let hostNat: UInt8
    let viewerNat: UInt8
    let hostCandidates: [SignalingEndpoint]
    let viewerCandidates: [SignalingEndpoint]
    let punchStartDelayMs: UInt32
    let closeReason: SessionCloseReason?

    static func decode(bytes: [UInt8], header: SignalingHeader) throws -> SessionView {
        var reader = try SignalingReader(bytes: bytes, header: header)
        let sessionId = try reader.id()
        guard let state = SessionState(rawValue: try reader.u8()) else {
            throw SignalingWireError.malformed("session state")
        }
        let generation = try reader.u32()
        guard let role = DeviceRoles(rawValue: try reader.u8()) else {
            throw SignalingWireError.malformed("session role")
        }
        let inviteCode = try reader.text(maxBytes: 16)
        let hostId = try reader.text(maxBytes: DeviceRoleRules.maxDeviceIdBytes)
        let viewerId = try reader.text(maxBytes: DeviceRoleRules.maxDeviceIdBytes)
        let hostEndpoint = try reader.endpoint()
        let viewerEndpoint = try reader.endpoint()
        let hostNat = try reader.u8()
        let viewerNat = try reader.u8()
        let hostCandidates = try reader.endpoints(maxCount: DeviceRoleRules.maxCandidates)
        let viewerCandidates = try reader.endpoints(maxCount: DeviceRoleRules.maxCandidates)
        let punchStartDelayMs = try reader.u32()
        let closeReason: SessionCloseReason?
        if reader.remaining > 0 {
            closeReason = SessionCloseReason(rawValue: try reader.u8())
        } else {
            closeReason = nil
        }
        try reader.requireEnd()
        return SessionView(type: header.type, sessionId: sessionId, state: state,
                           generation: generation, role: role, inviteCode: inviteCode,
                           hostId: hostId, viewerId: viewerId,
                           hostEndpoint: hostEndpoint, viewerEndpoint: viewerEndpoint,
                           hostNat: hostNat, viewerNat: viewerNat,
                           hostCandidates: hostCandidates, viewerCandidates: viewerCandidates,
                           punchStartDelayMs: punchStartDelayMs, closeReason: closeReason)
    }
}

struct PeerKeyMessage {
    let type: SignalingMessageType
    let sessionId: SignalingId
    let originRequestId: SignalingId
    let payload: [UInt8]

    static func decode(bytes: [UInt8], header: SignalingHeader) throws -> PeerKeyMessage {
        var reader = try SignalingReader(bytes: bytes, header: header)
        let sessionId = try reader.id()
        let originRequestId = try reader.id()
        let payload = try reader.blob(maxBytes: SignalingWire.maxBlobBytes)
        try reader.requireEnd()
        return PeerKeyMessage(type: header.type, sessionId: sessionId,
                              originRequestId: originRequestId, payload: payload)
    }
}
