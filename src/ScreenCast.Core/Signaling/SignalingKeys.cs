using System.Security.Cryptography;
using System.Text;

namespace ScreenCast.Core.Signaling;

public static class SignalingKeys
{
    private static readonly byte[] CodePrefix = Encoding.UTF8.GetBytes("ScreenCast/code/v1");
    private static readonly byte[] MediaInfo = Encoding.UTF8.GetBytes("ScreenCast/media-key/v1");

    public static byte[] DeriveMediaSecret(string code, ReadOnlySpan<byte> sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        if (sessionId.Length != SignalingId.Size)
        {
            throw new ArgumentException("sessionId must be 16 bytes.", nameof(sessionId));
        }

        byte[] codeBytes = Encoding.UTF8.GetBytes(code);
        byte[] ikm = new byte[CodePrefix.Length + codeBytes.Length];
        CodePrefix.CopyTo(ikm, 0);
        codeBytes.CopyTo(ikm, CodePrefix.Length);

        byte[] secret = new byte[32];
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, secret, sessionId, MediaInfo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ikm);
        }

        return secret;
    }
}
