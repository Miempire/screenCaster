import Foundation

struct PeerMediaInfo: Equatable {
    static let version: UInt8 = 1
    static let hostRole: UInt8 = 0
    static let viewerRole: UInt8 = 1
    static let maxAddresses = 8
    static let headerBytes = 7

    let role: UInt8
    let videoPort: UInt16
    let controlPort: UInt16
    let localAddresses: [String]

    var isHost: Bool { role == PeerMediaInfo.hostRole }

    func encode() -> [UInt8] {
        var output: [UInt8] = [PeerMediaInfo.version, role,
                               UInt8((videoPort >> 8) & 0xff), UInt8(videoPort & 0xff),
                               UInt8((controlPort >> 8) & 0xff), UInt8(controlPort & 0xff),
                               0]
        let valid = localAddresses.compactMap { PeerMediaInfo.ipv4Bytes($0) }
            .prefix(PeerMediaInfo.maxAddresses)
        output[6] = UInt8(valid.count)
        for address in valid {
            output.append(contentsOf: address)
        }
        return output
    }

    static func decode(_ payload: Data) -> PeerMediaInfo? {
        let bytes = [UInt8](payload)
        guard bytes.count >= headerBytes, bytes[0] == version else { return nil }
        let count = Int(bytes[6])
        guard count <= maxAddresses, bytes.count == headerBytes + count * 4 else { return nil }
        let videoPort = (UInt16(bytes[2]) << 8) | UInt16(bytes[3])
        let controlPort = (UInt16(bytes[4]) << 8) | UInt16(bytes[5])
        var addresses: [String] = []
        addresses.reserveCapacity(count)
        for index in 0 ..< count {
            let start = headerBytes + index * 4
            addresses.append("\(bytes[start]).\(bytes[start + 1]).\(bytes[start + 2]).\(bytes[start + 3])")
        }
        return PeerMediaInfo(role: bytes[1], videoPort: videoPort,
                             controlPort: controlPort, localAddresses: addresses)
    }

    static func localIPv4Addresses() -> [String] {
        var addresses: [String] = []
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0, let first = list else { return addresses }
        defer { freeifaddrs(list) }

        var cursor: UnsafeMutablePointer<ifaddrs>? = first
        while let entry = cursor {
            defer { cursor = entry.pointee.ifa_next }
            guard let address = entry.pointee.ifa_addr,
                  address.pointee.sa_family == UInt8(AF_INET) else { continue }
            let flags = entry.pointee.ifa_flags
            guard flags & UInt32(IFF_UP) != 0, flags & UInt32(IFF_LOOPBACK) == 0 else { continue }

            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            let status = getnameinfo(address, socklen_t(address.pointee.sa_len),
                                     &host, socklen_t(host.count),
                                     nil, 0, NI_NUMERICHOST)
            guard status == 0 else { continue }
            let text = String(cString: host)
            let parts = text.split(separator: ".").compactMap { Int($0) }
            guard parts.count == 4 else { continue }
            if parts[0] == 127 || (parts[0] == 169 && parts[1] == 254) { continue }
            if !addresses.contains(text) {
                addresses.append(text)
            }
        }
        return addresses
    }

    static func choosePeerAddress(peer: PeerMediaInfo,
                                  peerObserved: SignalingEndpoint?,
                                  ownObserved: SignalingEndpoint?) -> String? {
        let sameNAT: Bool
        if let peerObserved, let ownObserved {
            sameNAT = peerObserved.address == ownObserved.address
        } else {
            sameNAT = false
        }
        if sameNAT, let local = peer.localAddresses.first {
            return local
        }
        if let peerObserved {
            return peerObserved.address
        }
        return peer.localAddresses.first
    }

    private static func ipv4Bytes(_ text: String) -> [UInt8]? {
        let parts = text.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return nil }
        var output: [UInt8] = []
        output.reserveCapacity(4)
        for part in parts {
            guard let value = UInt8(part) else { return nil }
            output.append(value)
        }
        return output
    }
}
