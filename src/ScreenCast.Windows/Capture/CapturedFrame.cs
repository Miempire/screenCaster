using System.Buffers;

namespace ScreenCast.Windows.Capture;

/// <summary>Rectangle in desktop pixel coordinates; right and bottom are exclusive.</summary>
public readonly record struct CaptureRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);

    public int Height => Math.Max(0, Bottom - Top);

    public long Area => (long)Width * Height;

    public override string ToString() => $"({Left},{Top}) {Width}x{Height}";
}

/// <summary>A move region: Destination now holds the pixels that were at SourceLeft/SourceTop.</summary>
public readonly record struct CaptureMoveRect(CaptureRect Destination, int SourceLeft, int SourceTop);

/// <summary>
/// One captured desktop frame carrying a private CPU pixel copy.
/// The copy is taken (and mapped) while the DDA frame is still acquired, so the DDA resource is
/// released before this object reaches a consumer; nothing here aliases a duplicated surface.
/// <see cref="Pixels"/> is rented from <see cref="ArrayPool{T}.Shared"/>: call <see cref="Dispose"/>
/// once the pixels are no longer needed so the buffer returns to the pool.
/// </summary>
public sealed class CapturedFrame : IDisposable
{
    private byte[]? _pixels;

    internal CapturedFrame(
        byte[] pixels,
        int width,
        int height,
        int stride,
        bool isBgra,
        CaptureRect[] dirtyRects,
        CaptureMoveRect[] moveRects,
        long timestampUs,
        long presentTimeQpc,
        uint accumulatedFrames,
        bool rectsCoalesced)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        IsBgra = isBgra;
        DirtyRects = dirtyRects;
        MoveRects = moveRects;
        TimestampUs = timestampUs;
        PresentTimeQpc = presentTimeQpc;
        AccumulatedFrames = accumulatedFrames;
        RectsCoalesced = rectsCoalesced;

        long dirtyArea = 0;
        foreach (var rect in dirtyRects)
            dirtyArea += rect.Area;
        DirtyAreaPixels = dirtyArea;

        long moveArea = 0;
        foreach (var rect in moveRects)
            moveArea += rect.Destination.Area;
        MoveAreaPixels = moveArea;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    /// <summary>True when the pixel order is BGRA (B8G8R8A8/B8G8R8X8); false for RGBA order.</summary>
    public bool IsBgra { get; }

    public IReadOnlyList<CaptureRect> DirtyRects { get; }

    public IReadOnlyList<CaptureMoveRect> MoveRects { get; }

    /// <summary>Capture time in microseconds on the duplicator's monotonic clock.</summary>
    public long TimestampUs { get; }

    /// <summary>Raw DXGI last-present QPC value reported by DDA (diagnostic only).</summary>
    public long PresentTimeQpc { get; }

    public uint AccumulatedFrames { get; }

    public bool RectsCoalesced { get; }

    public long DirtyAreaPixels { get; }

    public long MoveAreaPixels { get; }

    /// <summary>True when DDA reported no content change at all for this frame.</summary>
    public bool IsStaticChange => DirtyRects.Count == 0 && MoveRects.Count == 0;

    /// <summary>Fraction of the surface covered by dirty rectangles (0..1).</summary>
    public double DirtyAreaRatio => Width <= 0 || Height <= 0 ? 0 : Math.Min(1, (double)DirtyAreaPixels / (Width * (double)Height));

    /// <summary>Rented pixel buffer; valid until <see cref="Dispose"/>.</summary>
    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(CapturedFrame));

    public ReadOnlySpan<byte> PixelSpan
    {
        get
        {
            var pixels = _pixels;
            if (pixels is null)
                throw new ObjectDisposedException(nameof(CapturedFrame));
            return pixels.AsSpan(0, Stride * Height);
        }
    }

    public void Dispose()
    {
        var pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels is null)
            return;
        ArrayPool<byte>.Shared.Return(pixels);
    }
}
