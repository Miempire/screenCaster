using System.Buffers.Binary;

namespace ScreenCast.Core.Transport;

public enum ControlEventType : ushort
{
    Key = 1,
    MouseMove = 2,
    MouseButton = 3,
    Wheel = 4,
    Touch = 5,
    ReleaseAll = 6,
    RequestKeyFrame = 7,
    SetQuality = 8,
    PathSwitch = 9,
    ControlLease = 10,
}

/// <summary>
/// 32-byte control header: kind(1) reserved(3) messageSequence(8) ackBase(8) ackMask(8)
/// eventType(2) bodyLength(2), followed by the body.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Sequence"/> is the reliable message number and is the only value that a
/// retransmission reuses. <see cref="AckBase"/> is the highest contiguously received message
/// number; <c>ulong.MaxValue</c> is the "nothing contiguous yet" sentinel, so <c>AckBase + 1</c>
/// reads as message 0. <see cref="AckMask"/> bit i means <c>AckBase + 1 + i</c> was received.
/// </para>
/// <para>
/// RTT is measured by the sender locally from send time to ACK arrival, so an ACK-only frame
/// carries no body and no timestamp. Only samples from messages that were never retransmitted
/// update the estimate (Karn's rule).
/// </para>
/// </remarks>
public readonly record struct ControlFrameMessage(
    bool IsAck,
    ulong Sequence,
    ulong AckBase,
    ulong AckMask,
    ControlEventType EventType,
    ReadOnlyMemory<byte> Body);

public static class ControlCodec
{
    public const int HeaderSize = 32;
    public const byte KindData = 1;
    public const byte KindAck = 2;

    /// <summary>Largest body that still fits one protected datagram (1200 - 64 - 32).</summary>
    public const int MaxBodyLength = PacketHeader.MaxDatagramSize - PacketHeader.Size - HeaderSize;

    public static int EncodedLength(int bodyLength) => HeaderSize + bodyLength;

    public static byte[] EncodeData(ulong sequence, ulong ackBase, ulong ackMask,
        ControlEventType eventType, ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxBodyLength)
        {
            throw new ArgumentOutOfRangeException(nameof(body), "Control body exceeds datagram budget.");
        }

        byte[] frame = new byte[EncodedLength(body.Length)];
        frame[0] = KindData;
        // frame[1..3] stay zero: reserved.
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(12), ackBase);
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(20), ackMask);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(28), (ushort)eventType);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(30), (ushort)body.Length);
        body.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    public static byte[] EncodeAck(ulong ackBase, ulong ackMask)
    {
        byte[] frame = new byte[HeaderSize];
        frame[0] = KindAck;
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(12), ackBase);
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(20), ackMask);
        return frame;
    }

    public static bool TryDecode(ReadOnlySpan<byte> frame, out ControlFrameMessage message)
    {
        message = default;
        if (frame.Length < HeaderSize)
        {
            return false;
        }

        byte kind = frame[0];
        if (kind is not (KindData or KindAck))
        {
            return false;
        }

        if (frame[1] != 0 || frame[2] != 0 || frame[3] != 0)
        {
            return false;
        }

        int bodyLength = BinaryPrimitives.ReadUInt16BigEndian(frame[30..]);
        if (bodyLength > MaxBodyLength || frame.Length != HeaderSize + bodyLength)
        {
            return false;
        }

        ulong ackBase = BinaryPrimitives.ReadUInt64BigEndian(frame[12..]);
        ulong ackMask = BinaryPrimitives.ReadUInt64BigEndian(frame[20..]);

        if (kind == KindAck)
        {
            // An ACK-only frame must carry no body and no event type.
            if (bodyLength != 0)
            {
                return false;
            }

            message = new ControlFrameMessage(true, 0, ackBase, ackMask, 0, ReadOnlyMemory<byte>.Empty);
            return true;
        }

        message = new ControlFrameMessage(false,
            BinaryPrimitives.ReadUInt64BigEndian(frame[4..]),
            ackBase,
            ackMask,
            (ControlEventType)BinaryPrimitives.ReadUInt16BigEndian(frame[28..]),
            frame[HeaderSize..].ToArray());
        return true;
    }
}