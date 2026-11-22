namespace ScreenCast.Windows.Media;

/// <summary>
/// Converts a 32-bit desktop frame (BGRA or RGBA) into NV12 for the Media Foundation H.264 encoder.
/// Scaling is nearest-neighbour; chroma is averaged over each covered 2x2 block. Colour conversion is
/// BT.601 limited range (Y in 16..235, C in 16..240), which is what the Windows H.264 MFT expects for
/// the NV12 subtype without a nominal-range attribute.
/// </summary>
public static class Nv12Converter
{
    /// <summary>Convenience overload that reads geometry and pixel order from a captured frame.</summary>
    public static void Convert(Capture.CapturedFrame frame, int destinationWidth, int destinationHeight, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Convert(
            frame.PixelSpan,
            frame.Width,
            frame.Height,
            frame.Stride,
            frame.IsBgra,
            destination,
            destinationWidth,
            destinationHeight);
    }
    public static int GetFrameSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentException("NV12 需要正的偶数宽高。");
        return width * height * 3 / 2;
    }

    public static void Convert(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        bool sourceIsBgra,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || sourceStride < sourceWidth * 4)
            throw new ArgumentException("源像素参数非法。");
        if (source.Length < sourceStride * sourceHeight)
            throw new ArgumentException("源像素缓冲区小于声明尺寸。");
        var required = GetFrameSize(destinationWidth, destinationHeight);
        if (destination.Length < required)
            throw new ArgumentException($"NV12 目标缓冲区需要 {required} 字节，实际 {destination.Length}。");

        unsafe
        {
            fixed (byte* sourcePointer = source)
            fixed (byte* destinationPointer = destination)
            {
                ConvertCore(sourcePointer, sourceWidth, sourceHeight, sourceStride, sourceIsBgra,
                    destinationPointer, destinationWidth, destinationHeight);
            }
        }
    }

    private static unsafe void ConvertCore(
        byte* source, int sourceWidth, int sourceHeight, int sourceStride, bool sourceIsBgra,
        byte* destination, int destinationWidth, int destinationHeight)
    {
        var yPlane = destination;
        var uvPlane = destination + ((long)destinationWidth * destinationHeight);

        for (var outY = 0; outY < destinationHeight; outY += 2)
        {
            for (var outX = 0; outX < destinationWidth; outX += 2)
            {
                var sumR = 0;
                var sumG = 0;
                var sumB = 0;

                for (var dy = 0; dy < 2; dy++)
                {
                    var targetY = outY + dy;
                    if (targetY >= destinationHeight)
                        break;
                    var sourceY = Math.Min(sourceHeight - 1, targetY * sourceHeight / destinationHeight);
                    var sourceRow = source + ((long)sourceY * sourceStride);

                    for (var dx = 0; dx < 2; dx++)
                    {
                        var targetX = outX + dx;
                        if (targetX >= destinationWidth)
                            break;
                        var sourceX = Math.Min(sourceWidth - 1, targetX * sourceWidth / destinationWidth);
                        var pixel = sourceRow + ((long)sourceX * 4);
                        var b = sourceIsBgra ? pixel[0] : pixel[2];
                        var g = pixel[1];
                        var r = sourceIsBgra ? pixel[2] : pixel[0];

                        var luma = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                        yPlane[((long)targetY * destinationWidth) + targetX] = (byte)Math.Clamp(luma, 0, 255);

                        sumR += r;
                        sumG += g;
                        sumB += b;
                    }
                }

                var u = ((-38 * sumR - 74 * sumG + 112 * sumB + 512) >> 10) + 128;
                var v = ((112 * sumR - 94 * sumG - 18 * sumB + 512) >> 10) + 128;
                var uvIndex = (((long)(outY / 2)) * (destinationWidth / 2) + (outX / 2)) * 2;
                uvPlane[uvIndex] = (byte)Math.Clamp(u, 0, 255);
                uvPlane[uvIndex + 1] = (byte)Math.Clamp(v, 0, 255);
            }
        }
    }
}
