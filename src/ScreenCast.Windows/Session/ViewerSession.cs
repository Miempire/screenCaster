using System.Diagnostics;
using System.Net;
using ScreenCast.Core.Transport;
using ScreenCast.Windows.Input;
using ScreenCast.Windows.Media;

namespace ScreenCast.Windows.Session;

/// <summary>Live counters for the viewer side.</summary>
public sealed record ViewerSessionStats(
    int DecodedFrames,
    int RenderedFrames,
    long ReceivedBytes,
    long VideoDatagramsRejected,
    long DroppedFrames,
    int Width,
    int Height,
    double Fps,
    double DecodeRenderMs,
    string LastError);

/// <summary>
/// The controlling side of a live session: receive encrypted fragments, reassemble, decode, convert
/// to BGRA and hand the frame to a renderer.
/// </summary>
/// <remarks>
/// <para>
/// Decoding runs on a background thread. The decoded frame is converted to BGRA there and only the
/// finished buffer crosses to the UI thread, so the render path never blocks on the decoder and the
/// decoder never touches WPF objects.
/// </para>
/// <para>
/// When a frame is dropped, the viewer asks the host for a new key frame. Without that request a
/// lost key frame leaves the picture broken forever, because every following frame references data
/// the decoder never saw.
/// </para>
/// </remarks>
public sealed class ViewerSession : IAsyncDisposable
{
    private readonly byte[] _sharedSecret;
    private readonly byte[] _sessionId;
    private readonly UdpTransport _videoTransport;
    private readonly UdpTransport _controlTransport;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _statsGate = new();
    private MediaSession? _session;
    private H264Decoder? _decoder;
    private Task? _loop;
    private long _lastKeyFrameRequestTicks;
    private int _decodedFrames;
    private int _renderedFrames;
    private long _receivedBytes;
    private int _width;
    private int _height;
    private double _fps;
    private double _decodeRenderMs;
    private string _lastError = string.Empty;
    private bool _disposed;

    public ViewerSession(
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> sessionId,
        IPEndPoint? videoBind = null,
        IPEndPoint? controlBind = null)
    {
        if (sharedSecret.Length != 32)
        {
            throw new ArgumentException("会话密钥必须是 32 字节。", nameof(sharedSecret));
        }

        if (sessionId.Length != 16)
        {
            throw new ArgumentException("会话 ID 必须是 16 字节。", nameof(sessionId));
        }

        _sharedSecret = sharedSecret.ToArray();
        _sessionId = sessionId.ToArray();
        _videoTransport = new UdpTransport(videoBind ?? new IPEndPoint(IPAddress.Any, 0));
        _controlTransport = new UdpTransport(controlBind ?? new IPEndPoint(IPAddress.Any, 0));
    }

    public int VideoPort => _videoTransport.LocalPort;

    public int ControlPort => _controlTransport.LocalPort;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Raised on a background thread for each decoded frame: width, height, BGRA pixels.</summary>
    public event Action<int, int, byte[]>? FrameDecoded;

    public event Action<ViewerSessionStats>? StatsUpdated;

    public ViewerSessionStats Snapshot()
    {
        lock (_statsGate)
        {
            return new ViewerSessionStats(_decodedFrames, _renderedFrames, _receivedBytes,
                _session?.VideoDatagramsRejected ?? 0, _session?.DroppedFrames ?? 0,
                _width, _height, _fps, _decodeRenderMs, _lastError);
        }
    }

    /// <summary>Connect to a host's video and control endpoints and start receiving.</summary>
    public void Start(IPEndPoint peerVideo, IPEndPoint peerControl)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null)
        {
            throw new InvalidOperationException("会话已在运行。");
        }

        _session = MediaSession.Create(
            isHost: false,
            _sharedSecret,
            _sessionId,
            keyEpoch: 1,
            _videoTransport,
            _controlTransport,
            initialPeer: peerVideo,
            onVideoFrame: OnVideoFrame,
            onControlEvent: (_, _) => { });

        _session.SetPeers(peerVideo, peerControl);
        _session.Start();
        _decoder = new H264Decoder(requestHardwareAcceleration: true);
        _loop = Task.Run(() => ReceiveLoopAsync(_lifetime.Token));
    }

    /// <summary>Ask the host for a fresh key frame. Rate limited so a bad link cannot flood it.</summary>
    public void RequestKeyFrame()
    {
        if (_session is null)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        long previous = Interlocked.Read(ref _lastKeyFrameRequestTicks);
        if (previous != 0 && (now - previous) * 1000 / Stopwatch.Frequency < 500)
        {
            return;
        }

        Interlocked.Exchange(ref _lastKeyFrameRequestTicks, now);
        _session.TrySendControl(ControlEventType.RequestKeyFrame, ReadOnlySpan<byte>.Empty);
    }

    public void SendKey(ushort scanCode, bool extended, bool isDown) =>
        _session?.TrySendControl(ControlEventType.Key, InputCodec.EncodeKey(scanCode, extended, isDown));

    public void SendMouseMove(ushort normalizedX, ushort normalizedY, ushort monitorId = 0) =>
        _session?.TrySendControl(ControlEventType.MouseMove, InputCodec.EncodeMouseMove(normalizedX, normalizedY, monitorId));

    public void SendMouseButton(MouseButtonId button, bool isDown) =>
        _session?.TrySendControl(ControlEventType.MouseButton, InputCodec.EncodeMouseButton(button, isDown));

    public void SendWheel(short deltaX, short deltaY) =>
        _session?.TrySendControl(ControlEventType.Wheel, InputCodec.EncodeWheel(deltaX, deltaY));

    public void SendReleaseAll() =>
        _session?.TrySendControl(ControlEventType.ReleaseAll, ReadOnlySpan<byte>.Empty);

    public void SendLease(bool acquire) =>
        _session?.TrySendControl(ControlEventType.ControlLease, [(byte)(acquire ? 1 : 0)]);

    private void OnVideoFrame(AssembledVideoFrame frame)
    {
        if (_decoder is null)
        {
            return;
        }

        try
        {
            var clock = Stopwatch.StartNew();
            IReadOnlyList<DecodedFrame> pictures = _decoder.Decode(frame.Data, (long)frame.CaptureTimestampMicroseconds);
            lock (_statsGate)
            {
                // Counted once per assembled frame, not per emitted picture: a frame can produce
                // more than one picture and counting inside the loop double-counts the bytes.
                _receivedBytes += frame.Data.Length;
            }

            foreach (var picture in pictures)
            {
                _decodedFrames++;
                if (picture.Width <= 0 || picture.Height <= 0)
                {
                    continue;
                }

                byte[] bgra = new byte[Nv12ToBgra.BgraSize(picture.Width, picture.Height)];
                picture.ToBgra(bgra);

                // Local decode-and-convert cost. End-to-end latency needs the probe round trip,
                // which is not wired into this path yet, so this reports only what it measures.
                double decodeMs = clock.Elapsed.TotalMilliseconds;
                lock (_statsGate)
                {
                    _width = picture.Width;
                    _height = picture.Height;
                    _decodeRenderMs = _decodeRenderMs <= 0 ? decodeMs : (_decodeRenderMs * 7 + decodeMs) / 8;
                }

                FrameDecoded?.Invoke(picture.Width, picture.Height, bgra);
                _renderedFrames++;
            }
        }
        catch (Exception ex)
        {
            lock (_statsGate)
            {
                _lastError = $"{ex.GetType().Name}: {ex.Message}";
            }

            // A failed decode almost always means the reference chain broke: ask for a key frame.
            RequestKeyFrame();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        long lastFpsAt = 0;
        int framesAtLastSample = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                if (_session is null)
                {
                    break;
                }

                _session.Tick();

                // Dropped frames mean the decoder has a hole; recovery requires a key frame.
                if (_session.DroppedFrames > 0)
                {
                    RequestKeyFrame();
                }

                long now = clock.ElapsedMilliseconds;
                if (now - lastFpsAt >= 1000)
                {
                    int rendered = _renderedFrames;
                    _fps = (rendered - framesAtLastSample) * 1000.0 / Math.Max(1, now - lastFpsAt);
                    framesAtLastSample = rendered;
                    lastFpsAt = now;
                    StatsUpdated?.Invoke(Snapshot());
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Shutdown of a cancelled loop is expected.
            }
        }

        _decoder?.Dispose();
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }

        _lifetime.Dispose();
        await _videoTransport.DisposeAsync().ConfigureAwait(false);
        await _controlTransport.DisposeAsync().ConfigureAwait(false);
    }
}