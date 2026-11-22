using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace ScreenCast.Windows.Capture;

/// <summary>Snapshot of the duplicator's counters.</summary>
public readonly record struct DdaStats(
    long AcquiredFrames,
    long Timeouts,
    long Rebuilds,
    long SizeChanges,
    long ReleaseFailures,
    long DirtyRectCount,
    long MoveRectCount,
    long DirtyAreaPixels,
    long MoveAreaPixels);

/// <summary>
/// Continuous Desktop Duplication capture for one display output.
///
/// Ownership model: the DDA frame is only valid between <c>AcquireNextFrame</c> and
/// <c>ReleaseFrame</c>. Every frame is therefore copied into a private CPU buffer (through a
/// staging texture) *inside* that window, the dirty/move rectangles are read inside it too, and
/// <c>ReleaseFrame</c> runs in a <c>finally</c> before the frame value leaves this class. Consumers
/// never touch a duplicated surface, so there is no dangling-resource race and no requirement for
/// the consumer to run on the capture thread.
///
/// Threading: all members must be called from the thread that owns the instance. DDA acquires and
/// releases frames in pairs, so serializing them on one worker thread is the intended usage.
/// </summary>
public sealed class DesktopDuplicator : IDisposable, IAsyncDisposable
{
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorAccessLost = unchecked((int)0x887A002A);
    private const int DxgiErrorInvalidCall = unchecked((int)0x887A0001);
    private const int MaxConsecutiveRebuilds = 5;
    private const uint MaxMetadataBufferBytes = 16u * 1024 * 1024;

    private readonly DisplayInfo _display;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _disposeGate = new();

    private IDXGIFactory1? _factory;
    private IDXGIAdapter1? _adapter;
    private IDXGIOutput? _output;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _staging;

    private RawRect[] _dirtyBuffer = [];
    private OutduplMoveRect[] _moveBuffer = [];

    private int _streamWidth;
    private int _streamHeight;
    private Format _streamFormat = Format.Unknown;
    private int _consecutiveRebuilds;
    private bool _disposed;

    private long _acquiredFrames;
    private long _timeouts;
    private long _rebuilds;
    private long _sizeChanges;
    private long _releaseFailures;
    private long _dirtyRectCount;
    private long _moveRectCount;
    private long _dirtyAreaPixels;
    private long _moveAreaPixels;

    public DesktopDuplicator(DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(display);
        _display = display;
        CreatePipeline();
    }

    public DisplayInfo Display => _display;

    public DdaStats Stats => new(
        _acquiredFrames, _timeouts, _rebuilds, _sizeChanges, _releaseFailures,
        _dirtyRectCount, _moveRectCount, _dirtyAreaPixels, _moveAreaPixels);

    /// <summary>D3D11 device owned by this capture instance (same adapter as the duplicated output).</summary>
    public ID3D11Device Device => _device ?? throw new ObjectDisposedException(nameof(DesktopDuplicator));

    /// <summary>
    /// Acquires one frame. Returns false on <c>DXGI_ERROR_WAIT_TIMEOUT</c> (no new desktop frame) and
    /// transparently rebuilds the duplication after <c>DXGI_ERROR_ACCESS_LOST</c>.
    /// </summary>
    public bool TryAcquireFrame(int timeoutMilliseconds, out CapturedFrame? frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (timeoutMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));

        if (NeedsRebuild)
        {
            NeedsRebuild = false;
            Rebuild();
        }

        frame = null;
        for (var attempt = 0; ; attempt++)
        {
            var duplication = _duplication ?? throw new InvalidOperationException("DDA duplication 尚未建立。");
            var result = duplication.AcquireNextFrame((uint)timeoutMilliseconds, out var info, out var resource);
            if (result.Code == DxgiErrorWaitTimeout)
            {
                _timeouts++;
                return false;
            }

            if (result.Code is DxgiErrorAccessLost or DxgiErrorInvalidCall)
            {
                resource?.Dispose();
                Rebuild();
                if (attempt >= 1)
                    return false;
                continue;
            }

            result.CheckError();
            try
            {
                frame = BuildFrame(info, resource, duplication);
                _acquiredFrames++;
                _consecutiveRebuilds = 0;

                if (frame.Width != _streamWidth || frame.Height != _streamHeight)
                {
                    // A mode change invalidates the duplication on most drivers; rebuild before the next acquire.
                    _sizeChanges++;
                    NeedsRebuild = true;
                }

                return true;
            }
            finally
            {
                resource.Dispose();
                var release = duplication.ReleaseFrame();
                if (release.Failure)
                {
                    _releaseFailures++;
                    if (release.Code is DxgiErrorAccessLost or DxgiErrorInvalidCall)
                        NeedsRebuild = true;
                }
            }
        }
    }

    private bool NeedsRebuild { get; set; }

    /// <summary>
    /// Producer loop for a channel. Each frame handed to the writer is already a private CPU copy,
    /// so the consumer may use it on any thread with any latency.
    /// </summary>
    public async Task ProduceToChannelAsync(ChannelWriter<CapturedFrame> writer, int acquireTimeoutMilliseconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (NeedsRebuild)
                {
                    NeedsRebuild = false;
                    Rebuild();
                }

                if (TryAcquireFrame(acquireTimeoutMilliseconds, out var frame) && frame is not null)
                {
                    await writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown path; the loop is cancellation-driven.
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private CapturedFrame BuildFrame(OutduplFrameInfo info, IDXGIResource resource, IDXGIOutputDuplication duplication)
    {
        var dirtyRects = ReadDirtyRects(duplication, info.TotalMetadataBufferSize);
        var moveRects = ReadMoveRects(duplication, info.TotalMetadataBufferSize);
        var pixels = CopyPixels(resource, out var width, out var height, out var stride, out var isBgra);
        var timestampUs = (long)(_clock.ElapsedTicks * (1_000_000.0 / Stopwatch.Frequency));

        foreach (var rect in dirtyRects)
            _dirtyAreaPixels += rect.Area;
        _dirtyRectCount += dirtyRects.Length;
        _moveRectCount += moveRects.Length;
        foreach (var rect in moveRects)
            _moveAreaPixels += rect.Destination.Area;

        try
        {
            return new CapturedFrame(
                pixels, width, height, stride, isBgra, dirtyRects, moveRects,
                timestampUs, info.LastPresentTime, info.AccumulatedFrames, info.RectsCoalesced);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(pixels);
            throw;
        }
    }

    private CaptureRect[] ReadDirtyRects(IDXGIOutputDuplication duplication, uint totalMetadataBytes)
    {
        if (totalMetadataBytes == 0 || totalMetadataBytes > MaxMetadataBufferBytes)
            return [];

        var itemSize = Marshal.SizeOf<RawRect>();
        var capacity = (int)((totalMetadataBytes / (uint)itemSize) + 1);
        if (_dirtyBuffer.Length < capacity)
            _dirtyBuffer = new RawRect[capacity];

        duplication.GetFrameDirtyRects((uint)(capacity * itemSize), _dirtyBuffer, out var bytes).CheckError();
        var count = (int)(bytes / (uint)itemSize);
        var rects = new CaptureRect[count];
        for (var i = 0; i < count; i++)
        {
            var r = _dirtyBuffer[i];
            rects[i] = new CaptureRect(r.Left, r.Top, r.Right, r.Bottom);
        }

        return rects;
    }

    private CaptureMoveRect[] ReadMoveRects(IDXGIOutputDuplication duplication, uint totalMetadataBytes)
    {
        if (totalMetadataBytes == 0 || totalMetadataBytes > MaxMetadataBufferBytes)
            return [];

        var itemSize = Marshal.SizeOf<OutduplMoveRect>();
        var capacity = (int)((totalMetadataBytes / (uint)itemSize) + 1);
        if (_moveBuffer.Length < capacity)
            _moveBuffer = new OutduplMoveRect[capacity];

        duplication.GetFrameMoveRects((uint)(capacity * itemSize), _moveBuffer, out var bytes).CheckError();
        var count = (int)(bytes / (uint)itemSize);
        var rects = new CaptureMoveRect[count];
        for (var i = 0; i < count; i++)
        {
            var m = _moveBuffer[i];
            var r = m.DestinationRect;
            rects[i] = new CaptureMoveRect(
                new CaptureRect(r.Left, r.Top, r.Right, r.Bottom), m.SourcePoint.X, m.SourcePoint.Y);
        }

        return rects;
    }

    private byte[] CopyPixels(IDXGIResource resource, out int width, out int height, out int stride, out bool isBgra)
    {
        using var texture = resource.QueryInterface<ID3D11Texture2D>();
        var description = texture.Description;
        width = (int)description.Width;
        height = (int)description.Height;
        stride = width * 4;

        isBgra = description.Format switch
        {
            Format.B8G8R8A8_UNorm or Format.B8G8R8X8_UNorm => true,
            Format.R8G8B8A8_UNorm => false,
            _ => throw new NotSupportedException($"不支持的桌面格式 {description.Format}；本采集器只处理 32 位 BGRA/RGBA 桌面。"),
        };

        EnsureStaging(description);
        var context = _context ?? throw new ObjectDisposedException(nameof(DesktopDuplicator));
        context.CopyResource(_staging!, texture);
        context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
        try
        {
            var pixels = ArrayPool<byte>.Shared.Rent(stride * height);
            unsafe
            {
                fixed (byte* destination = pixels)
                {
                    for (var y = 0; y < height; y++)
                    {
                        var source = (byte*)mapped.DataPointer + ((long)y * mapped.RowPitch);
                        Buffer.MemoryCopy(source, destination + ((long)y * stride), stride, stride);
                    }
                }
            }

            return pixels;
        }
        finally
        {
            context.Unmap(_staging!, 0);
        }
    }

    private void EnsureStaging(Texture2DDescription description)
    {
        if (_staging is not null
            && _streamWidth == description.Width
            && _streamHeight == description.Height
            && _streamFormat == description.Format)
            return;

        _staging?.Dispose();
        _staging = null;

        // Staging textures cannot be bound; they exist purely as a GPU-side copy target that the CPU can map.
        _staging = (_device ?? throw new ObjectDisposedException(nameof(DesktopDuplicator))).CreateTexture2D(new Texture2DDescription
        {
            Format = description.Format,
            Width = description.Width,
            Height = description.Height,
            ArraySize = 1,
            MipLevels = 1,
            BindFlags = BindFlags.None,
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            SampleDescription = new SampleDescription(1, 0),
            MiscFlags = ResourceOptionFlags.None,
        });
        _streamWidth = (int)description.Width;
        _streamHeight = (int)description.Height;
        _streamFormat = description.Format;
    }

    private void Rebuild()
    {
        if (++_consecutiveRebuilds > MaxConsecutiveRebuilds)
            throw new InvalidOperationException(
                $"DDA duplication 连续 {MaxConsecutiveRebuilds} 次重建失败（DXGI_ERROR_ACCESS_LOST）。显示器/会话状态可能已改变。");
        _rebuilds++;
        CreatePipeline();
        NeedsRebuild = false;
    }

    private void CreatePipeline()
    {
        ReleasePipeline();

        _factory = CreateDXGIFactory1<IDXGIFactory1>();
        _factory.EnumAdapters1(_display.AdapterIndex, out var adapter).CheckError();
        _adapter = adapter;
        if (_adapter.Description1.Luid != _display.AdapterLuid)
            throw new InvalidOperationException("显示适配器已改变（LUID 不匹配），请重新枚举显示器。");

        _adapter.EnumOutputs(_display.OutputIndex, out var output).CheckError();
        _output = output;
        if (_output.Description.DeviceName != _display.DeviceName)
            throw new InvalidOperationException("显示输出已改变（设备名不匹配），请重新枚举显示器。");

        D3D11CreateDevice(_adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        _device = device;
        _context = context;

        using var output1 = _output.QueryInterface<IDXGIOutput1>();
        _duplication = output1.DuplicateOutput(_device);

        _streamWidth = 0;
        _streamHeight = 0;
        _streamFormat = Format.Unknown;
        _staging?.Dispose();
        _staging = null;
        _consecutiveRebuilds = 0;
    }

    private void ReleasePipeline()
    {
        _duplication?.Dispose();
        _duplication = null;
        _staging?.Dispose();
        _staging = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _output?.Dispose();
        _output = null;
        _adapter?.Dispose();
        _adapter = null;
        _factory?.Dispose();
        _factory = null;
    }

    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            ReleasePipeline();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
