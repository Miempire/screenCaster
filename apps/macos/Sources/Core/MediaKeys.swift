import CryptoKit
import Foundation

enum MediaKeyError: LocalizedError {
    case emptyCode
    case invalidSessionID

    var errorDescription: String? {
        switch self {
        case .emptyCode:
            return "连接码不能为空。"
        case .invalidSessionID:
            return "会话 ID 必须是 16 字节。"
        }
    }
}

enum MediaKeys {
    static func deriveMediaSecret(code: String, sessionId: [UInt8]) throws -> [UInt8] {
        guard !code.isEmpty else { throw MediaKeyError.emptyCode }
        guard sessionId.count == SignalingId.size else { throw MediaKeyError.invalidSessionID }
        let ikm = Array("ScreenCast/code/v1".utf8) + Array(code.utf8)
        let salt = Data(sessionId)
        let info = Data("ScreenCast/media-key/v1".utf8)
        let key = HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: Data(ikm)),
                                         salt: salt,
                                         info: info,
                                         outputByteCount: 32)
        return key.withUnsafeBytes { Array($0) }
    }
}
