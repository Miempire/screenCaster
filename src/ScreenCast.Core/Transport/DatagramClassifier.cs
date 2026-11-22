using System.Buffers.Binary;

namespace ScreenCast.Core.Transport;

public enum DatagramKind
{
    /// <summary>ScreenCast application packet (SCT1) protected by AES-GCM.</summary>
    Application,

    /// <summary>ScreenCast handshake packet (SCSH), authenticated before any application data.</summary>
    Handshake,

    /// <summary>RFC5389 STUN message (Binding request/response).</summary>
    Stun,

    /// <summary>TURN ChannelData whose channel number was assigned by a TURN server.</summary>
    TurnChannelData,

    /// <summary>Unrecognized bytes; discard without allocating or replying.</summary>
    Unknown,
}

/// <summary>
/// Classifies a datagram by structural magic only. A positive <see cref="DatagramKind.TurnChannelData"/>
/// result is not authority to relay it: the caller must additionally prove the source is a
/// registered TURN server, that an active allocation owns the channel and that its length is exact.
/// </summary>
public static class DatagramClassifier
{
    /// <summary>ASCII "SCT1", the application magic. Also the reason TURN must never allocate 0x5343.</summary>
    public const uint ApplicationMagic = 0x53435431;

    /// <summary>ASCII "SCSH", the handshake magic.</summary>
    public const uint HandshakeMagic = 0x53435348;

    private const uint StunCookie = 0x2112A442;

    public static DatagramKind Classify(ReadOnlySpan<byte> datagram)
    {
        // Full application magic wins over the TURN 0x4000-0x7FFF channel range it overlaps.
        if (datagram.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(datagram) == ApplicationMagic)
        {
            return DatagramKind.Application;
        }

        if (datagram.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(datagram) == HandshakeMagic)
        {
            return DatagramKind.Handshake;
        }

        // RFC5389: the two most significant bits of the message type are zero and the magic
        // cookie occupies bytes 4-7. Length alignment is validated later by the STUN codec.
        if (datagram.Length >= 20
            && (datagram[0] & 0xC0) == 0
            && BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]) == StunCookie)
        {
            return DatagramKind.Stun;
        }

        if (datagram.Length >= 4)
        {
            ushort channel = BinaryPrimitives.ReadUInt16BigEndian(datagram);
            if (channel is >= 0x4000 and <= 0x7FFF)
            {
                return DatagramKind.TurnChannelData;
            }
        }

        return DatagramKind.Unknown;
    }
}