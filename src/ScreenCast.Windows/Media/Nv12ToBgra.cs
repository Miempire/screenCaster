namespace ScreenCast.Windows.Media;

/// <summary>Which YCbCr matrix the stream uses. Selected from the SPS VUI when available.</summary>
public enum YuvMatrix
{
    Bt601,
    Bt709,
}

/// <summary>
/// NV12 to BGRA conversion using the same fixed-point coefficients FFmpeg uses in
/// <c>libswscale/yuv2rgb.c</c>. The table there is {crv, cbu, cgu, cgv} as 16.16 values derived from
/// <c>crv = (255/224) * 65536 * (1 - cr) / 0.5</c>; limited range is expanded by 255/219 on luma.
/// </summary>
/// <remarks>
/// <para>
/// Row stride of the NV12 source is passed in explicitly instead of assuming width, because both
/// D3D11 staging textures and Media Foundation buffers pad rows. Reading with a guessed stride is
/// the usual cause of a skewed picture.
/// </para>
/// <para>
/// The chroma plane is sampled once per 2x2 luma block, matching NV12's 4:2:0 layout.
/// </para>
/// </remarks>
public static class Nv12ToBgra
{
    // FFmpeg libswscale/yuv2rgb.c ff_yuv2rgb_coeffs[1] (ITU-R Rec. 709) and [6] (SMPTE 170M / BT.601).
    private const int Bt601Crv = 104597;
    private const int Bt601Cbu = 132201;
    private const int Bt601Cgu = 25675;
    private const int Bt601Cgv = 53279;
    private const int Bt709Crv = 117489;
    private const int Bt709Cbu = 138438;
    private const int Bt709Cgu = 13975;
    private const int Bt709Cgv = 34925;

    public static int BgraSize(int width, int height) => width * height * 4;

    /// <summary>
    /// Convert one NV12 frame to BGRA. <paramref name="yStride"/> and <paramref name="uvStride"/>
    /// are the source row pitches; the destination is tightly packed at <c>width * 4</c>.
    /// </summary>
    public static void Convert(
        ReadOnlySpan<byte> nv12,
        int width,
        int height,
        int yStride,
        int uvStride,
        Span<byte> bgra,
        YuvMatrix matrix = YuvMatrix.Bt601)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (yStride < width)
        {
            throw new ArgumentException($"Y 平面行跨度 {yStride} 小于宽度 {width}。", nameof(yStride));
        }

        if (uvStride < width)
        {
            throw new ArgumentException($"UV 平面行跨度 {uvStride} 小于宽度 {width}。", nameof(uvStride));
        }

        int chromaHeight = (height + 1) / 2;
        int required = yStride * height + uvStride * chromaHeight;
        if (nv12.Length < required)
        {
            throw new ArgumentException($"NV12 数据不足：需要 {required} 字节，实际 {nv12.Length} 字节。", nameof(nv12));
        }

        if (bgra.Length < BgraSize(width, height))
        {
            throw new ArgumentException("BGRA 目标缓冲不足。", nameof(bgra));
        }

        (int crv, int cbu, int cgu, int cgv) = matrix == YuvMatrix.Bt709
            ? (Bt709Crv, Bt709Cbu, Bt709Cgu, Bt709Cgv)
            : (Bt601Crv, Bt601Cbu, Bt601Cgu, Bt601Cgv);

        int uvPlane = yStride * height;
        for (int y = 0; y < height; y++)
        {
            int yRow = y * yStride;
            int uvRow = uvPlane + (y >> 1) * uvStride;
            int dstRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int luma = nv12[yRow + x];
                int chromaIndex = uvRow + (x & ~1);
                int u = nv12[chromaIndex] - 128;
                int v = nv12[chromaIndex + 1] - 128;

                // Limited range expansion: cy = (255/219) applied as (Y - 16) * 255 / 219.
                // The intermediate is kept in 64-bit: 235 * 255 * 65536 exceeds Int32 and silently
                // wraps to a negative value, which clamps every pixel to black.
                long lumaScaled = (long)(luma - 16) * 255 * 65536 / 219;

                int r = (int)((lumaScaled + (long)crv * v + 32768) >> 16);
                int g = (int)((lumaScaled - (long)cgu * u - (long)cgv * v + 32768) >> 16);
                int b = (int)((lumaScaled + (long)cbu * u + 32768) >> 16);

                int offset = dstRow + x * 4;
                bgra[offset] = Clamp(b);
                bgra[offset + 1] = Clamp(g);
                bgra[offset + 2] = Clamp(r);
                bgra[offset + 3] = 255;
            }
        }
    }

    /// <summary>Convert assuming tightly packed NV12 (stride equals width).</summary>
    public static void Convert(ReadOnlySpan<byte> nv12, int width, int height, Span<byte> bgra,
        YuvMatrix matrix = YuvMatrix.Bt601) =>
        Convert(nv12, width, height, width, width, bgra, matrix);

    private static byte Clamp(int value) => value switch
    {
        < 0 => 0,
        > 255 => 255,
        _ => (byte)value,
    };
}