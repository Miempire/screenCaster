using System.Buffers.Binary;
using System.IO;

namespace ScreenCast.Windows.Media;

/// <summary>
/// Minimal 24-bit uncompressed BMP writer. Used only for smoke verification: a decoded frame has to
/// be inspectable by an ordinary image viewer to prove the decoder produced real pixels, and BMP
/// needs no third-party image encoder.
/// </summary>
public static class BitmapWriter
{
    /// <summary>Write BGRA pixels as a bottom-up 24-bit BMP.</summary>
    public static void WriteBgra(string path, ReadOnlySpan<byte> bgra, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int required = width * height * 4;
        if (bgra.Length < required)
        {
            throw new ArgumentException($"BGRA 缓冲不足：需要 {required} 字节，实际 {bgra.Length} 字节。", nameof(bgra));
        }

        // BMP rows are 4-byte aligned and stored bottom-up in BGR order.
        int rowBytes = width * 3;
        int padding = (4 - (rowBytes % 4)) % 4;
        int stride = rowBytes + padding;
        int pixelBytes = stride * height;
        const int headerBytes = 14 + 40;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Span<byte> header = stackalloc byte[headerBytes];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(header[2..], headerBytes + pixelBytes);
        BinaryPrimitives.WriteInt32LittleEndian(header[10..], headerBytes);
        BinaryPrimitives.WriteInt32LittleEndian(header[14..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(header[18..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[22..], height);
        BinaryPrimitives.WriteInt16LittleEndian(header[26..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[28..], 24);
        BinaryPrimitives.WriteInt32LittleEndian(header[34..], pixelBytes);
        stream.Write(header);

        byte[] row = new byte[stride];
        for (int y = height - 1; y >= 0; y--)
        {
            int source = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int offset = source + x * 4;
                row[x * 3] = bgra[offset];
                row[x * 3 + 1] = bgra[offset + 1];
                row[x * 3 + 2] = bgra[offset + 2];
            }

            stream.Write(row, 0, stride);
        }
    }

    /// <summary>Average the three channels of a BGRA buffer, for a cheap "is the frame blank" check.</summary>
    public static (double Mean, double StdDev) Statistics(ReadOnlySpan<byte> bgra, int width, int height)
    {
        int pixels = width * height;
        if (pixels <= 0)
        {
            return (0, 0);
        }

        double sum = 0;
        double sumSquares = 0;
        for (int i = 0; i < pixels; i++)
        {
            int offset = i * 4;
            double luma = (bgra[offset] + bgra[offset + 1] + bgra[offset + 2]) / 3.0;
            sum += luma;
            sumSquares += luma * luma;
        }

        double mean = sum / pixels;
        double variance = Math.Max(0, (sumSquares / pixels) - (mean * mean));
        return (mean, Math.Sqrt(variance));
    }
}