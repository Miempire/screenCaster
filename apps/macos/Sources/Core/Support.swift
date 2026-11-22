//
//  Support.swift
//  Small thread-safe helpers shared by both roles.
//

import Foundation

/// The ports `sc_session_local_ports` reported after `sc_session_bind`.
struct BoundPorts: Equatable {
    let video: UInt16
    let control: UInt16
}

/// Locked boolean, used for the "stop the poll loop" flag and the pending key-frame request.
final class LockedFlag {
    private let lock = NSLock()
    private var value: Bool

    init(_ initial: Bool = false) {
        value = initial
    }

    var isSet: Bool {
        lock.lock()
        defer { lock.unlock() }
        return value
    }

    func set(_ newValue: Bool) {
        lock.lock()
        value = newValue
        lock.unlock()
    }

    func raise() {
        set(true)
    }

    /// Read and clear in one step.
    func take() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        let current = value
        value = false
        return current
    }
}

/// Named counters written from capture/decode threads and read from the UI timer.
final class CounterBox {
    private let lock = NSLock()
    private var values: [String: Int] = [:]

    func add(_ key: String, _ delta: Int = 1) {
        lock.lock()
        values[key, default: 0] += delta
        lock.unlock()
    }

    func value(_ key: String) -> Int {
        lock.lock()
        defer { lock.unlock() }
        return values[key] ?? 0
    }

    func reset() {
        lock.lock()
        values.removeAll()
        lock.unlock()
    }
}

/// Resolves a host name to the literal IP text the C ABI requires.
///
/// `sc_session_set_peer` parses with `IpAddr::from_str` and returns SC_ERR_INVALID_ARGUMENT for
/// anything that is not a literal, so "nas.local" or "example.com" must be resolved before the
/// call. IPv6 literals are returned without brackets, which is what the Rust side expects.
enum HostResolver {
    static func literal(for host: String) -> String? {
        let trimmed = host.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }

        // Already a literal? Hand it back unchanged so a v6 address with a scope id keeps working.
        var isLiteral = false
        trimmed.withCString { pointer in
            var storage = in6_addr()
            isLiteral = inet_pton(AF_INET, pointer, &storage) == 1 || inet_pton(AF_INET6, pointer, &storage) == 1
        }
        if isLiteral {
            return trimmed
        }

        var hints = addrinfo()
        hints.ai_family = AF_UNSPEC
        hints.ai_socktype = SOCK_DGRAM
        hints.ai_protocol = IPPROTO_UDP

        var result: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(trimmed, nil, &hints, &result) == 0, let first = result else {
            return nil
        }
        defer { freeaddrinfo(result) }

        var cursor: UnsafeMutablePointer<addrinfo>? = first
        while let current = cursor {
            if let address = current.pointee.ai_addr {
                var text = [CChar](repeating: 0, count: 1025)
                let status = getnameinfo(address, current.pointee.ai_addrlen,
                                         &text, socklen_t(text.count),
                                         nil, 0,
                                         NI_NUMERICHOST)
                if status == 0 {
                    return String(cString: text)
                }
            }
            cursor = current.pointee.ai_next
        }
        return nil
    }
}

/// Formats a byte count for the stats panels.
func formatBytes(_ bytes: Int) -> String {
    let value = Double(bytes)
    if value < 1024 { return "\(bytes) B" }
    if value < 1024 * 1024 { return String(format: "%.1f KiB", value / 1024) }
    return String(format: "%.2f MiB", value / (1024 * 1024))
}

/// Formats an RTT that may be "no sample yet" (-1).
func formatRTT(_ milliseconds: Double) -> String {
    guard milliseconds >= 0 else { return "—" }
    return String(format: "%.1f ms", milliseconds)
}
