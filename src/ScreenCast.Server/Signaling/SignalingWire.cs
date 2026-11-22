using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ScreenCast.Server.Signaling;

/// <summary>
/// Message kinds. 1..63 are client requests, 128..135 are direct replies, 136..191 are server
/// notifications, and 9/10 double as the forwarded form of the client PeerKey bodies.
/// </summary>
public enum SignalingMessageType : byte
{
    Register = 1,
    Heartbeat = 2,
    CreateSession = 3,
    JoinSession = 4,
    AcceptSession = 5,
    RejectSession = 6,
    PunchStart = 7,
    CloseSession = 8,
    PeerKeyOffer = 9,
    PeerKeyAnswer = 10,
    PathCommit = 11,

    Registered = 128,
    HeartbeatAck = 129,
    SessionCreated = 130,
    SessionJoined = 131,
    SessionAccepted = 132,
    SessionRejected = 133,
    SessionClosed = 134,
    Ack = 135,

    Error = 136,
    JoinRequested = 137,
    PunchStartNotify = 138,
    SessionInvited = 139,
    SessionActive = 140,
}

/// <summary>Stable numeric error codes; the detail string is advisory only.</summary>
public enum SignalingErrorCode : ushort
{
    Malformed = 1,
    Unsupported = 2,
    Unregistered = 3,
    NotFound = 4,
    LimitExceeded = 5,
    InvalidState = 6,
    NotParticipant = 7,
    RoleMismatch = 8,
    GenerationMismatch = 9,
    InviteInvalid = 10,
    Busy = 11,
}

/// <summary>Session lifecycle: Created -> Accepted -> Negotiating -> Active -> Closed.</summary>
public enum SessionState : byte
{
    Created = 0,
    Accepted = 1,
    Negotiating = 2,
    Active = 3,
    Closed = 4,
}

public enum SessionCloseReason : byte
{
    UserClosed = 0,
    Timeout = 1,
    PeerOffline = 2,
    HostRejected = 3,
}

public enum DeviceRoles : byte
{
    Host = 1,
    Viewer = 2,
}

public static class DeviceRoleRules
{
    public const byte All = (byte)(DeviceRoles.Host | DeviceRoles.Viewer);
    public const int MaxDeviceIdBytes = 64;
    public const int MaxCapabilitiesBytes = 1024;
    public const int MaxCandidates = 8;
}

/// <summary>128-bit wire identifier used for both requestId and sessionId.</summary>
public readonly record struct SignalingId(ulong High, ulong Low)
{
    public const int Size = 16;

    public static SignalingId Zero => default;

    public bool IsZero => High == 0 && Low == 0;

    public static SignalingId FromBytes(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size) throw new ArgumentException("16 bytes are required.", nameof(source));
        return new SignalingId(BinaryPrimitives.ReadUInt64BigEndian(source), BinaryPrimitives.ReadUInt64BigEndian(source[(Size / 2)..]));
    }

    public static SignalingId NewRandom() => FromBytes(RandomNumberGenerator.GetBytes(Size));

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size) throw new ArgumentException("16 bytes are required.", nameof(destination));
        BinaryPrimitives.WriteUInt64BigEndian(destination, High);
        BinaryPrimitives.WriteUInt64BigEndian(destination[(Size / 2)..], Low);
    }

    public string ToHex() => string.Create(CultureInfo.InvariantCulture, $"{High:x16}{Low:x16}");

    public override string ToString() => ToHex();
}

public readonly record struct SignalingHeader(SignalingMessageType Type, SignalingId RequestId, int BodyLength);

/// <summary>
/// Hand-written framing: magic SCSG, version, type, body length, then a 16 byte requestId.
/// Every field is explicit; no reflection serialization is involved anywhere in this server.
/// </summary>
public static class SignalingWire
{
    /// <summary>ASCII "SCSG".</summary>
    public const uint Magic = 0x53435347;
    public const byte Version = 1;
    public const int HeaderSize = 24;
    public const int MaxMessageBytes = 8192;
    public const int MaxBodyBytes = MaxMessageBytes - HeaderSize;
    public const int MaxTextBytes = 64;
    public const int MaxBlobBytes = 4096;

    public static byte[] Encode(SignalingMessageType type, SignalingId requestId, ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxBodyBytes) throw new ArgumentOutOfRangeException(nameof(body));
        byte[] message = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(message, Magic);
        message[4] = Version;
        message[5] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(6), (ushort)body.Length);
        requestId.WriteTo(message.AsSpan(8, SignalingId.Size));
        body.CopyTo(message.AsSpan(HeaderSize));
        return message;
    }

    /// <summary>Rejects wrong magic/version, impossible lengths and length mismatches. Oversize is rejected by the caller.</summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> datagram, out SignalingHeader header)
    {
        header = default;
        if (datagram.Length < HeaderSize || datagram.Length > MaxMessageBytes) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(datagram) != Magic || datagram[4] != Version) return false;
        int bodyLength = BinaryPrimitives.ReadUInt16BigEndian(datagram[6..]);
        if (datagram.Length != HeaderSize + bodyLength) return false;
        header = new SignalingHeader((SignalingMessageType)datagram[5], SignalingId.FromBytes(datagram.Slice(8, SignalingId.Size)), bodyLength);
        return true;
    }
}

/// <summary>Cursor over one message body that never reads outside the declared length.</summary>
public sealed class SignalingReader
{
    private readonly byte[] buffer;
    private readonly int end;
    private int offset;

    public SignalingReader(byte[] datagram, SignalingHeader header)
    {
        ArgumentNullException.ThrowIfNull(datagram);
        if (header.BodyLength < 0 || header.BodyLength > SignalingWire.MaxBodyBytes) throw new ArgumentOutOfRangeException(nameof(header));
        if (datagram.Length < SignalingWire.HeaderSize + header.BodyLength) throw new ArgumentException("Datagram is shorter than its declared body.", nameof(datagram));
        buffer = datagram;
        offset = SignalingWire.HeaderSize;
        end = SignalingWire.HeaderSize + header.BodyLength;
    }

    public byte U8()
    {
        Require(1);
        return buffer[offset++];
    }

    public ushort U16()
    {
        Require(2);
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset));
        offset += 2;
        return value;
    }

    public uint U32()
    {
        Require(4);
        uint value = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset));
        offset += 4;
        return value;
    }

    public ulong U64()
    {
        Require(8);
        ulong value = BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(offset));
        offset += 8;
        return value;
    }

    public SignalingId Id() => SignalingId.FromBytes(Raw(SignalingId.Size));

    public string Text(int maxBytes)
    {
        int length = U16();
        if (length > maxBytes) throw new FormatException("Text field exceeds its limit.");
        return Decode(Raw(length));
    }

    public ReadOnlyMemory<byte> Blob(int maxBytes)
    {
        int length = checked((int)U32());
        if (length < 0 || length > maxBytes) throw new FormatException("Blob field exceeds its limit.");
        Require(length);
        var value = buffer.AsMemory(offset, length);
        offset += length;
        return value;
    }

    public IPEndPoint? Endpoint()
    {
        byte family = U8();
        switch (family)
        {
            case 0:
                return null;
            case 4:
                return new IPEndPoint(new IPAddress(Raw(4)), U16());
            case 6:
                return Normalize(new IPEndPoint(new IPAddress(Raw(16)), U16()));
            default:
                throw new FormatException("Unknown endpoint family.");
        }
    }

    public List<IPEndPoint> Endpoints(int maxCount)
    {
        int count = U16();
        if (count > maxCount) throw new FormatException("Endpoint list exceeds its limit.");
        var endpoints = new List<IPEndPoint>(count);
        for (int i = 0; i < count; i++)
        {
            if (Endpoint() is { } endpoint) endpoints.Add(endpoint);
        }
        return endpoints;
    }

    /// <summary>Rejects trailing bytes so an ambiguous message is never accepted.</summary>
    public void RequireEnd()
    {
        if (offset != end) throw new FormatException("Trailing bytes after the last field.");
    }

    public static IPEndPoint Normalize(IPEndPoint endpoint) =>
        endpoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port) : endpoint;

    private ReadOnlySpan<byte> Raw(int count)
    {
        Require(count);
        var value = buffer.AsSpan(offset, count);
        offset += count;
        return value;
    }

    private void Require(int count)
    {
        if (count < 0 || end - offset < count) throw new FormatException("Truncated body.");
    }

    private static string Decode(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(utf8);
        }
        catch (DecoderFallbackException ex)
        {
            throw new FormatException("Text field is not valid UTF-8.", ex);
        }
    }
}

/// <summary>Append-only body writer bounded by the 8 KiB message limit.</summary>
public sealed class SignalingWriter
{
    private readonly byte[] buffer = new byte[SignalingWire.MaxMessageBytes];
    private int offset = SignalingWire.HeaderSize;

    public void U8(byte value)
    {
        Require(1);
        buffer[offset++] = value;
    }

    public void U16(ushort value)
    {
        Require(2);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), value);
        offset += 2;
    }

    public void U32(uint value)
    {
        Require(4);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), value);
        offset += 4;
    }

    public void U64(ulong value)
    {
        Require(8);
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(offset), value);
        offset += 8;
    }

    public void Id(SignalingId value)
    {
        Require(SignalingId.Size);
        value.WriteTo(buffer.AsSpan(offset, SignalingId.Size));
        offset += SignalingId.Size;
    }

    public void Text(string value, int maxBytes = SignalingWire.MaxTextBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        int length = Encoding.UTF8.GetByteCount(value);
        if (length > maxBytes || length > ushort.MaxValue) throw new ArgumentException("Text field exceeds its limit.", nameof(value));
        U16((ushort)length);
        Require(length);
        Encoding.UTF8.GetBytes(value, buffer.AsSpan(offset));
        offset += length;
    }

    public void Blob(ReadOnlySpan<byte> value, int maxBytes = SignalingWire.MaxBlobBytes)
    {
        if (value.Length > maxBytes) throw new ArgumentException("Blob field exceeds its limit.", nameof(value));
        U32((uint)value.Length);
        Require(value.Length);
        value.CopyTo(buffer.AsSpan(offset));
        offset += value.Length;
    }

    public void Endpoint(IPEndPoint? value)
    {
        if (value is null)
        {
            U8(0);
            return;
        }
        IPEndPoint normalized = SignalingReader.Normalize(value);
        if (normalized.AddressFamily == AddressFamily.InterNetwork)
        {
            U8(4);
            normalized.Address.GetAddressBytes().CopyTo(buffer, offset);
            offset += 4;
        }
        else if (normalized.AddressFamily == AddressFamily.InterNetworkV6)
        {
            U8(6);
            normalized.Address.GetAddressBytes().CopyTo(buffer, offset);
            offset += 16;
        }
        else
        {
            throw new ArgumentException("Only IPv4 and IPv6 endpoints are supported.", nameof(value));
        }
        U16((ushort)normalized.Port);
    }

    public void Endpoints(IReadOnlyList<IPEndPoint> endpoints, int maxCount = DeviceRoleRules.MaxCandidates)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Count > maxCount || endpoints.Count > ushort.MaxValue) throw new ArgumentException("Endpoint list exceeds its limit.", nameof(endpoints));
        U16((ushort)endpoints.Count);
        foreach (var endpoint in endpoints) Endpoint(endpoint);
    }

    public byte[] ToMessage(SignalingMessageType type, SignalingId requestId) =>
        SignalingWire.Encode(type, requestId, buffer.AsSpan(SignalingWire.HeaderSize, offset - SignalingWire.HeaderSize));

    private void Require(int count)
    {
        if (buffer.Length - offset < count) throw new InvalidOperationException("Signaling body exceeded 8 KiB.");
    }
}