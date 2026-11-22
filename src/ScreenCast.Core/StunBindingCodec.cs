using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ScreenCast.Core;

public sealed record StunAttribute(ushort Type, byte[] Value);
public sealed record StunMessage(ushort Type, byte[] TransactionId, IReadOnlyList<StunAttribute> Attributes);

/// <summary>RFC5389 Binding wire primitives only; no TURN allocations or authenticated STUN service.</summary>
public static class StunBindingCodec
{
    public const uint MagicCookie = 0x2112A442;
    public const ushort BindingRequest = 0x0001, BindingSuccess = 0x0101, BindingError = 0x0111;
    public const ushort XorMappedAddress = 0x0020;
    public static byte[] NewTransactionId() => RandomNumberGenerator.GetBytes(12);
    public static byte[] EncodeRequest(ReadOnlySpan<byte> transactionId) => Encode(BindingRequest, transactionId, []);
    public static StunMessage Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20 || (bytes[0] & 0xC0) != 0 || BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]) != MagicCookie)
            throw new FormatException("Invalid STUN header.");
        int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);
        if ((length & 3) != 0 || bytes.Length != 20 + length) throw new FormatException("Invalid STUN message length.");
        var attributes = new List<StunAttribute>();
        for (int offset = 20; offset < bytes.Length;)
        {
            if (bytes.Length - offset < 4) throw new FormatException("Truncated attribute header.");
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            int size = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 2)..]);
            int padded = (size + 3) & ~3;
            if (padded > bytes.Length - offset - 4) throw new FormatException("Truncated attribute value.");
            if (type == 0x8028)
            {
                if (size != 4 || offset + 8 != bytes.Length) throw new FormatException("FINGERPRINT must be last and four bytes.");
                uint crc = uint.MaxValue;
                foreach (byte b in bytes[..offset])
                {
                    crc ^= b;
                    for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
                }
                if (BinaryPrimitives.ReadUInt32BigEndian(bytes[(offset + 4)..]) != (~crc ^ 0x5354554Eu)) throw new FormatException("Invalid STUN FINGERPRINT.");
            }
            attributes.Add(new(type, bytes.Slice(offset + 4, size).ToArray())); offset += 4 + padded;
        }
        return new(BinaryPrimitives.ReadUInt16BigEndian(bytes), bytes.Slice(8, 12).ToArray(), attributes);
    }
    public static byte[] Encode(ushort type, ReadOnlySpan<byte> transactionId, IReadOnlyList<StunAttribute> attributes)
    {
        if ((type & 0xC000) != 0 || transactionId.Length != 12) throw new ArgumentException("Invalid STUN type or transaction.");
        int length = 0;
        foreach (var a in attributes)
        {
            if (a.Value.Length > ushort.MaxValue) throw new ArgumentException("Attribute too long.");
            length = checked(length + 4 + ((a.Value.Length + 3) & ~3));
        }
        if (length > ushort.MaxValue) throw new ArgumentException("STUN message too long.");
        byte[] result = new byte[20 + length];
        BinaryPrimitives.WriteUInt16BigEndian(result, type); BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), (ushort)length);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), MagicCookie); transactionId.CopyTo(result.AsSpan(8, 12));
        int offset = 20;
        foreach (var a in attributes)
        {
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset), a.Type);
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset + 2), (ushort)a.Value.Length);
            a.Value.CopyTo(result, offset + 4); offset += 4 + ((a.Value.Length + 3) & ~3);
        }
        return result;
    }
    public static byte[] EncodeSuccess(ReadOnlySpan<byte> transactionId, IPEndPoint mapped)
    {
        if (mapped.Address.IsIPv4MappedToIPv6) mapped = new(mapped.Address.MapToIPv4(), mapped.Port);
        byte[] address = mapped.Address.GetAddressBytes();
        if (mapped.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) throw new ArgumentException("IP family required.");
        byte[] value = new byte[4 + address.Length]; value[1] = (byte)(address.Length == 4 ? 1 : 2);
        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(2), (ushort)(mapped.Port ^ (MagicCookie >> 16)));
        Span<byte> mask = stackalloc byte[16]; BinaryPrimitives.WriteUInt32BigEndian(mask, MagicCookie);
        if (transactionId.Length != 12) throw new ArgumentException("Transaction ID must be 12 bytes.");
        transactionId.CopyTo(mask[4..]);
        for (int i = 0; i < address.Length; i++) value[i + 4] = (byte)(address[i] ^ mask[i]);
        return Encode(BindingSuccess, transactionId, [new(XorMappedAddress, value)]);
    }
    public static IPEndPoint DecodeSuccess(ReadOnlySpan<byte> response, ReadOnlySpan<byte> expectedTransactionId, IPEndPoint source, IPEndPoint expectedServer)
    {
        if (!source.Equals(expectedServer)) throw new FormatException("Unexpected STUN response source.");
        var message = Parse(response);
        if (expectedTransactionId.Length != 12 || !message.TransactionId.AsSpan().SequenceEqual(expectedTransactionId) || message.Type != BindingSuccess)
            throw new FormatException("Unexpected STUN transaction or response type.");
        var addresses = message.Attributes.Where(a => a.Type == XorMappedAddress).ToArray();
        if (addresses.Length != 1) throw new FormatException("Expected exactly one XOR-MAPPED-ADDRESS.");
        if (message.Attributes.Any(a => a.Type < 0x8000 && a.Type != XorMappedAddress && a.Type != 0x0001)) throw new FormatException("Unsupported required response attribute.");
        foreach (var mapped in message.Attributes.Where(a => a.Type == 0x0001))
        {
            var m = mapped.Value;
            if (m.Length < 4 || m[0] != 0 || (m[1] != 1 && m[1] != 2) || m.Length != (m[1] == 1 ? 8 : 20))
                throw new FormatException("Invalid MAPPED-ADDRESS.");
        }
        ReadOnlySpan<byte> value = addresses[0].Value;
        if (value.Length < 4 || value[0] != 0 || (value[1] != 1 && value[1] != 2) || value.Length != (value[1] == 1 ? 8 : 20))
            throw new FormatException("Invalid XOR address.");
        int port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]) ^ (int)(MagicCookie >> 16);
        Span<byte> mask = stackalloc byte[16]; BinaryPrimitives.WriteUInt32BigEndian(mask, MagicCookie); message.TransactionId.CopyTo(mask[4..]);
        byte[] address = value[4..].ToArray(); for (int i = 0; i < address.Length; i++) address[i] ^= mask[i];
        return new(new IPAddress(address), port);
    }
    /// <summary>Returns null for non-Binding requests. Unsupported mandatory attributes receive 420.</summary>
    public static byte[]? CreateBindingResponse(ReadOnlySpan<byte> request, IPEndPoint source)
    {
        var message = Parse(request);
        if (message.Type != BindingRequest) return null;
        // Parse validates FINGERPRINT; other comprehension-optional attributes are ignored.
        ushort[] unknown = message.Attributes.Where(a => a.Type < 0x8000).Select(a => a.Type).Distinct().Order().ToArray();
        if (unknown.Length == 0) return EncodeSuccess(message.TransactionId, source);
        byte[] error = new byte[4 + Encoding.UTF8.GetByteCount("Unknown Attribute")]; error[2] = 4; error[3] = 20;
        Encoding.UTF8.GetBytes("Unknown Attribute").CopyTo(error, 4);
        byte[] types = new byte[unknown.Length * 2];
        for (int i = 0; i < unknown.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(types.AsSpan(i * 2), unknown[i]);
        return Encode(BindingError, message.TransactionId, [new(0x0009, error), new(0x000A, types)]);
    }
}
