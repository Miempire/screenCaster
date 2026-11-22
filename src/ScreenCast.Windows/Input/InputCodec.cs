using System.Buffers.Binary;
using ScreenCast.Core.Transport;

namespace ScreenCast.Windows.Input;

/// <summary>Mouse button identifiers used on the wire.</summary>
public enum MouseButtonId : byte
{
    Left = 0,
    Right = 1,
    Middle = 2,
}

/// <summary>Touch phases used on the wire.</summary>
public enum TouchPhase : byte
{
    Down = 0,
    Move = 1,
    Up = 2,
    Cancel = 3,
}

/// <summary>
/// Wire encoding for control events. Every body is fixed-length and big-endian so the host can
/// validate it without parsing a variable structure from an untrusted peer.
/// </summary>
/// <remarks>
/// Key events carry a hardware scan code, not a virtual-key code: the scan code is what
/// <c>SendInput</c> injects and what a physical keyboard produces, so it survives layout
/// differences between the two machines. Text input that has no scan code is a separate concern
/// and is not encoded here.
/// </remarks>
public static class InputCodec
{
    public const int KeyBodySize = 3;
    public const int MouseMoveBodySize = 6;
    public const int MouseButtonBodySize = 2;
    public const int WheelBodySize = 4;
    public const int TouchBodySize = 7;

    /// <summary>Key body: scanCode(2) flags(1), where bit0 = extended and bit1 = down.</summary>
    public static byte[] EncodeKey(ushort scanCode, bool extended, bool isDown)
    {
        byte[] body = new byte[KeyBodySize];
        BinaryPrimitives.WriteUInt16BigEndian(body, scanCode);
        body[2] = (byte)((extended ? 1 : 0) | (isDown ? 2 : 0));
        return body;
    }

    public static bool TryDecodeKey(ReadOnlySpan<byte> body, out ushort scanCode, out bool extended, out bool isDown)
    {
        scanCode = 0;
        extended = false;
        isDown = false;
        if (body.Length != KeyBodySize)
        {
            return false;
        }

        scanCode = BinaryPrimitives.ReadUInt16BigEndian(body);
        extended = (body[2] & 1) != 0;
        isDown = (body[2] & 2) != 0;
        return true;
    }

    /// <summary>Mouse move: normalizedX(2) normalizedY(2) monitorId(2), all 0..65535.</summary>
    public static byte[] EncodeMouseMove(ushort normalizedX, ushort normalizedY, ushort monitorId)
    {
        byte[] body = new byte[MouseMoveBodySize];
        BinaryPrimitives.WriteUInt16BigEndian(body, normalizedX);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2), normalizedY);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(4), monitorId);
        return body;
    }

    public static bool TryDecodeMouseMove(ReadOnlySpan<byte> body, out ushort x, out ushort y, out ushort monitorId)
    {
        x = 0;
        y = 0;
        monitorId = 0;
        if (body.Length != MouseMoveBodySize)
        {
            return false;
        }

        x = BinaryPrimitives.ReadUInt16BigEndian(body);
        y = BinaryPrimitives.ReadUInt16BigEndian(body[2..]);
        monitorId = BinaryPrimitives.ReadUInt16BigEndian(body[4..]);
        return true;
    }

    /// <summary>Mouse button: button(1) down(1).</summary>
    public static byte[] EncodeMouseButton(MouseButtonId button, bool isDown) =>
        [(byte)button, (byte)(isDown ? 1 : 0)];

    public static bool TryDecodeMouseButton(ReadOnlySpan<byte> body, out MouseButtonId button, out bool isDown)
    {
        button = MouseButtonId.Left;
        isDown = false;
        if (body.Length != MouseButtonBodySize || body[0] > (byte)MouseButtonId.Middle || body[1] > 1)
        {
            return false;
        }

        button = (MouseButtonId)body[0];
        isDown = body[1] != 0;
        return true;
    }

    /// <summary>Wheel: deltaX(2 signed) deltaY(2 signed), in Windows wheel units (120 per notch).</summary>
    public static byte[] EncodeWheel(short deltaX, short deltaY)
    {
        byte[] body = new byte[WheelBodySize];
        BinaryPrimitives.WriteInt16BigEndian(body, deltaX);
        BinaryPrimitives.WriteInt16BigEndian(body.AsSpan(2), deltaY);
        return body;
    }

    public static bool TryDecodeWheel(ReadOnlySpan<byte> body, out short deltaX, out short deltaY)
    {
        deltaX = 0;
        deltaY = 0;
        if (body.Length != WheelBodySize)
        {
            return false;
        }

        deltaX = BinaryPrimitives.ReadInt16BigEndian(body);
        deltaY = BinaryPrimitives.ReadInt16BigEndian(body[2..]);
        return true;
    }

    /// <summary>Touch: contactId(2) phase(1) x(2) y(2).</summary>
    public static byte[] EncodeTouch(ushort contactId, TouchPhase phase, ushort x, ushort y)
    {
        byte[] body = new byte[TouchBodySize];
        BinaryPrimitives.WriteUInt16BigEndian(body, contactId);
        body[2] = (byte)phase;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(3), x);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(5), y);
        return body;
    }

    public static bool TryDecodeTouch(ReadOnlySpan<byte> body, out ushort contactId, out TouchPhase phase, out ushort x, out ushort y)
    {
        contactId = 0;
        phase = TouchPhase.Down;
        x = 0;
        y = 0;
        if (body.Length != TouchBodySize || body[2] > (byte)TouchPhase.Cancel)
        {
            return false;
        }

        contactId = BinaryPrimitives.ReadUInt16BigEndian(body);
        phase = (TouchPhase)body[2];
        x = BinaryPrimitives.ReadUInt16BigEndian(body[3..]);
        y = BinaryPrimitives.ReadUInt16BigEndian(body[5..]);
        return true;
    }

    /// <summary>Map a pixel position inside a surface to the 0..65535 normalized range.</summary>
    public static ushort Normalize(int position, int extent)
    {
        if (extent <= 1)
        {
            return 0;
        }

        double ratio = Math.Clamp((double)position / (extent - 1), 0, 1);
        return (ushort)Math.Round(ratio * ushort.MaxValue);
    }

    /// <summary>Map a normalized 0..65535 coordinate back to a pixel position inside a surface.</summary>
    public static int Denormalize(ushort normalized, int extent)
    {
        if (extent <= 1)
        {
            return 0;
        }

        return (int)Math.Round(normalized / (double)ushort.MaxValue * (extent - 1));
    }
}