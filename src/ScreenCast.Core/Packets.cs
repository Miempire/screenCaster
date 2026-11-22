using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ScreenCast.Core;

public enum PacketChannel : byte { Signaling, Video, ReliableControl, Probe, Audio }
public enum PacketDirection : byte { HostToViewer, ViewerToHost }

/// <summary>Session identifiers are raw 16-byte values, never Guid byte order.</summary>
public sealed record PacketHeader(byte[] SessionId, uint KeyEpoch, uint StreamId, ulong PacketSequence,
    PacketChannel Channel, ushort Flags, ushort PayloadLength)
{
    public const int Size = 64;
    public const int MaxDatagramSize = 1200;
    public const uint Magic = 0x53435431;
    public void Write(Span<byte> destination)
    {
        if (destination.Length < Size) throw new ArgumentException("Header buffer too short.");
        Validate();
        destination[..Size].Clear();
        BinaryPrimitives.WriteUInt32BigEndian(destination, Magic);
        destination[4] = 1; destination[5] = (byte)Channel;
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], Flags);
        SessionId.CopyTo(destination[8..24]);
        BinaryPrimitives.WriteUInt32BigEndian(destination[24..], KeyEpoch);
        BinaryPrimitives.WriteUInt32BigEndian(destination[28..], StreamId);
        BinaryPrimitives.WriteUInt64BigEndian(destination[32..], PacketSequence);
        BinaryPrimitives.WriteUInt16BigEndian(destination[40..], PayloadLength);
        BinaryPrimitives.WriteUInt16BigEndian(destination[42..], Size);
    }
    private void Validate()
    {
        if (SessionId is not { Length: 16 } || KeyEpoch == 0 || (byte)Channel > 4 || (Flags & ~1) != 0 || PayloadLength > MaxDatagramSize - Size)
            throw new ArgumentException("Invalid packet header.");
    }
    public static PacketHeader Parse(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < Size || datagram.Length > MaxDatagramSize) throw new FormatException("Invalid datagram length.");
        if (BinaryPrimitives.ReadUInt32BigEndian(datagram) != Magic || datagram[4] != 1 ||
            BinaryPrimitives.ReadUInt16BigEndian(datagram[42..]) != Size || BinaryPrimitives.ReadUInt32BigEndian(datagram[44..]) != 0)
            throw new FormatException("Invalid fixed header fields.");
        var h = new PacketHeader(datagram[8..24].ToArray(), BinaryPrimitives.ReadUInt32BigEndian(datagram[24..]),
            BinaryPrimitives.ReadUInt32BigEndian(datagram[28..]), BinaryPrimitives.ReadUInt64BigEndian(datagram[32..]),
            (PacketChannel)datagram[5], BinaryPrimitives.ReadUInt16BigEndian(datagram[6..]), BinaryPrimitives.ReadUInt16BigEndian(datagram[40..]));
        try { h.Validate(); } catch (ArgumentException ex) { throw new FormatException("Invalid header.", ex); }
        if (datagram.Length != Size + h.PayloadLength) throw new FormatException("Payload length mismatch.");
        return h;
    }
}

/// <summary>One authenticated session/direction/channel/stream/epoch. Retain for the entire epoch.
/// Construct a new epoch with fresh key material after restart; never recreate a sender with an old key.</summary>
public sealed class PacketProtector : IDisposable
{
    private readonly byte[] session;
    private readonly uint epoch, stream;
    private readonly PacketChannel channel;
    private readonly AesGcm cipher;
    private readonly ReplayWindow replay = new();
    private readonly object gate = new();
    private ulong nextSequence;
    private bool exhausted;
    private bool disposed;

    public PacketProtector(ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> sessionId, uint keyEpoch,
        PacketDirection direction, PacketChannel channel, uint streamId)
    {
        if (sharedSecret.Length < 32 || sessionId.Length != 16 || keyEpoch == 0 || (byte)channel > 4 || (byte)direction > 1)
            throw new ArgumentException("Invalid protection context.");
        session = sessionId.ToArray(); epoch = keyEpoch; stream = streamId; this.channel = channel;
        // Explicit local HKDF info layout: ASCII label || version1 || session16 || epoch4 || direction1 || channel1 || stream4.
        byte[] label = Encoding.ASCII.GetBytes("ScreenCast/AES-256-GCM");
        byte[] info = new byte[label.Length + 27]; label.CopyTo(info, 0);
        Span<byte> fields = info.AsSpan(label.Length); fields[0] = 1; session.CopyTo(fields[1..17]);
        BinaryPrimitives.WriteUInt32BigEndian(fields[17..], epoch); fields[21] = (byte)direction; fields[22] = (byte)channel;
        BinaryPrimitives.WriteUInt32BigEndian(fields[23..], stream);
        byte[] key = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, key, session, info);
        try { cipher = new AesGcm(key, 16); } finally { CryptographicOperations.ZeroMemory(key); }
    }

    public byte[] Encode(ReadOnlySpan<byte> plaintext, bool ackOnly = false, int maxDatagramSize = PacketHeader.MaxDatagramSize)
    {
        if (maxDatagramSize < PacketHeader.Size || maxDatagramSize > PacketHeader.MaxDatagramSize || plaintext.Length > maxDatagramSize - PacketHeader.Size)
            throw new ArgumentOutOfRangeException(nameof(plaintext));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (exhausted) throw new InvalidOperationException("Sequence exhausted; rekey required.");
            ulong sequence = nextSequence;
            // Reserve before crypto so any failure cannot cause nonce reuse.
            if (nextSequence == ulong.MaxValue) exhausted = true; else nextSequence++;
            var header = new PacketHeader(session, epoch, stream, sequence, channel, (ushort)(ackOnly ? 1 : 0), (ushort)plaintext.Length);
            byte[] result = new byte[PacketHeader.Size + plaintext.Length]; header.Write(result);
            Span<byte> nonce = stackalloc byte[12]; MakeNonce(sequence, nonce);
            cipher.Encrypt(nonce, plaintext, result.AsSpan(64), result.AsSpan(48, 16), result.AsSpan(0, 48));
            return result;
        }
    }

    public byte[] Decode(ReadOnlySpan<byte> datagram)
    {
        var header = PacketHeader.Parse(datagram);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!header.SessionId.AsSpan().SequenceEqual(session) || header.KeyEpoch != epoch || header.StreamId != stream || header.Channel != channel)
                throw new CryptographicException("Wrong protection context.");
            if (!replay.CanAccept(header.PacketSequence)) throw new CryptographicException("Replay or stale sequence.");
            byte[] plaintext = new byte[header.PayloadLength];
            Span<byte> nonce = stackalloc byte[12]; MakeNonce(header.PacketSequence, nonce);
            try { cipher.Decrypt(nonce, datagram[64..], datagram.Slice(48, 16), plaintext, datagram[..48]); }
            catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
            replay.Commit(header.PacketSequence); // Only authenticated packets advance the window.
            return plaintext;
        }
    }
    private void MakeNonce(ulong sequence, Span<byte> nonce)
    {
        BinaryPrimitives.WriteUInt32BigEndian(nonce, epoch); BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], sequence);
    }
    public void Dispose() { lock (gate) { if (!disposed) { cipher.Dispose(); disposed = true; } } }
}

internal sealed class ReplayWindow
{
    private readonly bool[] received = new bool[1024];
    private bool initialized;
    private ulong highest;
    public bool CanAccept(ulong sequence) => !initialized || sequence > highest || (highest - sequence < 1024 && !received[(int)(sequence % 1024)]);
    public void Commit(ulong sequence)
    {
        if (!initialized) { initialized = true; highest = sequence; }
        else if (sequence > highest)
        {
            ulong distance = sequence - highest;
            if (distance >= 1024) Array.Clear(received);
            else for (ulong i = 1; i <= distance; i++) received[(int)((highest + i) % 1024)] = false;
            highest = sequence;
        }
        received[(int)(sequence % 1024)] = true;
    }
}
