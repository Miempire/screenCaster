//
//  DeviceBook.swift
//  Local list of machines this viewer has connected to, plus the nine-digit shortcut code.
//
//  There is no signalling service in this build, so the code is assigned on this Mac the first
//  time a direct address connects successfully. It is a local shortcut, not a server-issued code.
//

import Foundation

struct SavedDevice: Codable, Identifiable, Equatable {
    var code: String
    var name: String
    var host: String
    var videoPort: Int
    var controlPort: Int
    var lastUsed: Date

    var id: String { code }

    var addressText: String {
        "\(host):\(videoPort) / \(controlPort)"
    }

    var spacedCode: String {
        guard code.count == 9 else { return code }
        let start = code.startIndex
        let first = code[start..<code.index(start, offsetBy: 3)]
        let second = code[code.index(start, offsetBy: 3)..<code.index(start, offsetBy: 6)]
        let third = code[code.index(start, offsetBy: 6)...]
        return "\(first) \(second) \(third)"
    }
}

final class DeviceBook: ObservableObject {
    @Published private(set) var devices: [SavedDevice] = []

    private let defaults: UserDefaults
    private let storageKey = "screencast.macos.devicebook"

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        load()
    }

    func device(forCode code: String) -> SavedDevice? {
        devices.first { $0.code == code }
    }

    /// Saves or refreshes a device. Passing an existing code updates that entry; otherwise a fresh
    /// nine-digit code is generated so a direct-address connection still becomes a device card.
    @discardableResult
    func remember(name: String, host: String, videoPort: Int, controlPort: Int,
                  code: String? = nil) -> SavedDevice {
        let resolvedCode: String
        if let code, code.count == 9, let existing = device(forCode: code) {
            resolvedCode = existing.code
        } else if let code, code.count == 9 {
            resolvedCode = code
        } else {
            resolvedCode = freshCode()
        }

        let cleanedName = name.trimmingCharacters(in: .whitespacesAndNewlines)
        let device = SavedDevice(code: resolvedCode,
                                 name: cleanedName.isEmpty ? host : cleanedName,
                                 host: host,
                                 videoPort: videoPort,
                                 controlPort: controlPort,
                                 lastUsed: Date())
        devices.removeAll { $0.code == resolvedCode }
        devices.insert(device, at: 0)
        persist()
        return device
    }

    func touch(code: String) {
        guard let index = devices.firstIndex(where: { $0.code == code }) else { return }
        var device = devices.remove(at: index)
        device.lastUsed = Date()
        devices.insert(device, at: 0)
        persist()
    }

    func remove(code: String) {
        devices.removeAll { $0.code == code }
        persist()
    }

    func removeAll() {
        devices.removeAll()
        persist()
    }

    // MARK: - Storage

    private func load() {
        guard let data = defaults.data(forKey: storageKey) else { return }
        do {
            let decoded = try JSONDecoder().decode([SavedDevice].self, from: data)
            devices = decoded.sorted { $0.lastUsed > $1.lastUsed }
        } catch {
            devices = []
        }
    }

    private func persist() {
        guard let data = try? JSONEncoder().encode(devices) else { return }
        defaults.set(data, forKey: storageKey)
    }

    private func freshCode() -> String {
        for _ in 0 ..< 64 {
            var digits = ""
            for _ in 0 ..< 9 {
                digits.append(Character(String(Int.random(in: 0 ... 9))))
            }
            if !digits.hasPrefix("0"), device(forCode: digits) == nil {
                return digits
            }
        }
        return String(Int.random(in: 100_000_000 ... 999_999_999))
    }
}
