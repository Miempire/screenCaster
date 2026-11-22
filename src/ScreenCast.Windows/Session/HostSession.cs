using System.Diagnostics;
using System.Net;
using ScreenCast.Core.Transport;
using ScreenCast.Windows.Capture;
using ScreenCast.Windows.Input;
using ScreenCast.Windows.Media;

namespace ScreenCast.Windows.Session;

/// <summary>Live counters for the host side, surfaced in the UI.</summary>
public sealed record HostSessionStats(
    int CapturedFrames,
    int EncodedFrames,
    int KeyFrames,
    long SentBytes,
    long VideoDatagramsRejected,
    int PressedKeys,
    string? InputLeaseOwner,
    string LastError);

/// <summary>
/// The controlled side of a live session: capture the desktop, encode it, send it, and inject the
/// remote input that comes back.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece that turns the smoke tests into an actual remote desktop. The loop is:
/// DDA acquire, skip unchanged frames, convert to NV12, encode, fragment and send. A key frame is
/// forced when the viewer asks for one, which is what recovers a picture after packet loss.
/// </para>
/// <para>
/// Input is only injected while the injector holds a lease, and the lease is released together with
/// every held key when the session stops. A dropped connection must not leave a modifier stuck.
/// </para>
/// </remarks>
public sealed class HostSession : IAsyncDisposable
{
    private readonly DisplayInfo _display;
    private readonly H264EncoderSettings _settings;
    private readonly byte[] _sharedSecret;
    private readonly byte[] _sessionId;
    private readonly UdpTransport _videoTransport;
    private readonly UdpTransport _controlTransport;
    private readonly WindowsInputInjector _injector = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _statsGate = new();
    private MediaSession? _session;
    private DesktopDuplicator? _duplicator;
    private H264Encoder? _encoder;
    private Task? _loop;
    private Task? _waitTask;
    private IPEndPoint? _peerVideo;
    private IPEndPoint? _peerControl;
    private int _forceKeyFrame = 1;
    private int _capturedFrames;
    private int _encodedFrames;
    private int _keyFrames;
    private long _sentBytes;
    private string _lastError = string.Empty;
    private bool _disposed;

    public HostSession(
        DisplayInfo display,
        H264EncoderSettings settings,
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> sessionId,
        IPEndPoint? videoBind = null,
        IPEndPoint? controlBind = null)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(settings);
        if (sharedSecret.Length != 32)
        {
            throw new ArgumentException("会话密钥必须是 32 字节。", nameof(sharedSecret));
        }

        if (sessionId.Length != 16)
        {
            throw new ArgumentException("会话 ID 必须是 16 字节。", nameof(sessionId));
        }

        _display = display;
        _settings = settings;
        _sharedSecret = sharedSecret.ToArray();
        _sessionId = sessionId.ToArray();
        _videoTransport = new UdpTransport(videoBind ?? new IPEndPoint(IPAddress.Any, 0));
        _controlTransport = new UdpTransport(controlBind ?? new IPEndPoint(IPAddress.Any, 0));
    }

    /// <summary>
    /// Wait until a peer has sent something on either socket and return the observed addresses.
    /// A real deployment gets these from the signalling server; a direct host has to learn them
    /// from traffic, because the NAT mapping is only knowable after the peer has sent something.
    /// </summary>
    public (IPEndPoint? Video, IPEndPoint? Control) WaitForPeerSources(TimeSpan timeout, Action<string>? log = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        var buffer = new byte[2048];
        IPEndPoint? video = null;
        IPEndPoint? control = null;
        while (DateTime.UtcNow < deadline && control is null)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            int slice = (int)Math.Min(250, remaining.TotalMilliseconds);
            if (control is null && _controlTransport.TryPeekSource(slice, buffer, out var controlSource))
            {
                control = controlSource;
                log?.Invoke($"控制套接字收到来自 {controlSource} 的数据报");
                continue;
            }

            if (video is null && _videoTransport.TryPeekSource(slice, buffer, out var videoSource))
            {
                video = videoSource;
                log?.Invoke($"视频套接字收到来自 {videoSource} 的数据报");
            }
        }

        return (video, control);
    }

    public IPEndPoint? WaitForPeer(TimeSpan timeout, Action<string>? log = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        var buffer = new byte[2048];
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            // Peek without consuming: the session will do the real authenticated receive later.
            if (_videoTransport.TryPeekSource((int)Math.Min(500, remaining.TotalMilliseconds), buffer, out var source))
            {
                log?.Invoke($"收到来自 {source} 的数据报");
                return source;
            }
        }

        return null;
    }

    public int VideoPort => _videoTransport.LocalPort;

    public int ControlPort => _controlTransport.LocalPort;

    public int VideoReceiveBufferBytes => _videoTransport.ActualReceiveBufferBytes;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Raised on a background thread whenever counters change.</summary>
    public event Action<HostSessionStats>? StatsUpdated;

    public HostSessionStats Snapshot()
    {
        lock (_statsGate)
        {
            return new HostSessionStats(_capturedFrames, _encodedFrames, _keyFrames, _sentBytes,
                _session?.VideoDatagramsRejected ?? 0, _injector.PressedKeyCount,
                _injector.LeaseOwner, _lastError);
        }
    }

    /// <summary>
    /// Start in wait mode: bind the ports, wait for the viewer to send the first datagram, then
    /// stream to the address it came from. This is what lets a host start before the viewer exists,
    /// without a signalling server. The viewer's video port must be supplied because a viewer never
    /// sends video, so it cannot be observed.
    /// </summary>
    public void StartWaitingForPeer(int expectedPeerVideoPort, Action<string>? log = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null || _waitTask is not null)
        {
            throw new InvalidOperationException("会话已在运行。");
        }

        _waitTask = Task.Run(async () =>
        {
            try
            {
                var (observedVideo, observedControl) = await Task.Run(
                    () => WaitForPeerSources(TimeSpan.FromMinutes(10), log),
                    _lifetime.Token).ConfigureAwait(false);

                if (observedControl is null)
                {
                    return;
                }

                var videoPeer = observedVideo ?? new IPEndPoint(observedControl.Address, expectedPeerVideoPort);
                log?.Invoke($"对端就绪：视频 {videoPeer}，控制 {observedControl}");
                lock (_statsGate)
                {
                    _peerVideo = videoPeer;
                    _peerControl = observedControl;
                }

                StartCore(videoPeer, observedControl);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // Shutdown while waiting is normal.
            }
            catch (Exception ex)
            {
                lock (_statsGate)
                {
                    _lastError = $"{ex.GetType().Name}: {ex.Message}";
                }

                StatsUpdated?.Invoke(Snapshot());
            }
        });
    }

    public IPEndPoint? PeerVideo
    {
        get { lock (_statsGate) { return _peerVideo; } }
    }

    public IPEndPoint? PeerControl
    {
        get { lock (_statsGate) { return _peerControl; } }
    }

    /// <summary>Start capturing and sending to a known peer.</summary>
    public void Start(IPEndPoint peerVideo, IPEndPoint peerControl)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null)
        {
            throw new InvalidOperationException("会话已在运行。");
        }

        lock (_statsGate)
        {
            _peerVideo = peerVideo;
            _peerControl = peerControl;
        }

        StartCore(peerVideo, peerControl);
    }

    private void StartCore(IPEndPoint peerVideo, IPEndPoint peerControl)
    {
        _session = MediaSession.Create(
            isHost: true,
            _sharedSecret,
            _sessionId,
            keyEpoch: 1,
            _videoTransport,
            _controlTransport,
            initialPeer: peerVideo,
            onVideoFrame: _ => { },
            onControlEvent: OnControlEvent);

        _session.SetPeers(peerVideo, peerControl);
        _session.Start();

        _duplicator = new DesktopDuplicator(_display);
        _encoder = new H264Encoder(_settings);
        _loop = Task.Run(() => CaptureLoopAsync(_lifetime.Token));
    }

    private void OnControlEvent(ControlEventType type, ReadOnlyMemory<byte> body)
    {
        try
        {
            switch (type)
            {
                case ControlEventType.ControlLease:
                    if (body.Length >= 1 && body.Span[0] != 0)
                    {
                        _injector.AcquireLease("viewer");
                    }
                    else
                    {
                        _injector.ReleaseLease("viewer");
                    }

                    break;
                case ControlEventType.Key:
                    if (InputCodec.TryDecodeKey(body.Span, out var scanCode, out var extended, out var down))
                    {
                        _injector.SendScanCode(scanCode, extended, down);
                    }

                    break;
                case ControlEventType.MouseMove:
                    if (InputCodec.TryDecodeMouseMove(body.Span, out var x, out var y, out _))
                    {
                        _injector.MovePointer(x, y);
                    }

                    break;
                case ControlEventType.MouseButton:
                    if (InputCodec.TryDecodeMouseButton(body.Span, out var button, out var isDown))
                    {
                        _injector.SendMouseButton(button, isDown);
                    }

                    break;
                case ControlEventType.Wheel:
                    if (InputCodec.TryDecodeWheel(body.Span, out var deltaX, out var deltaY))
                    {
                        _injector.SendWheel(deltaX, deltaY);
                    }

                    break;
                case ControlEventType.ReleaseAll:
                    _injector.ReleaseAll();
                    break;
                case ControlEventType.RequestKeyFrame:
                    Volatile.Write(ref _forceKeyFrame, 1);
                    break;
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            lock (_statsGate)
            {
                _lastError = $"输入处理失败：{ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        byte[] nv12 = new byte[Nv12Converter.GetFrameSize(_settings.Width, _settings.Height)];
        ulong frameId = 0;
        long lastStatsAt = 0;
        long lastEncodedAt = 0;
        long frameIntervalTicks = Math.Max(1, Stopwatch.Frequency / Math.Max(1, _settings.FramesPerSecond));
        var clock = Stopwatch.StartNew();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_duplicator is null || _encoder is null || _session is null)
                {
                    break;
                }

                if (!_duplicator.TryAcquireFrame(50, out var captured) || captured is null)
                {
                    continue;
                }

                // Pace to the configured frame rate. DDA delivers a frame on every desktop change,
                // which on a busy screen is far above the target rate; encoding all of them inflates
                // the bitrate and the CPU cost for no visual benefit.
                bool tooEarly = clock.ElapsedTicks - lastEncodedAt < frameIntervalTicks;
                bool forceKey = Volatile.Read(ref _forceKeyFrame) != 0;

                bool skip;
                using (captured)
                {
                    // A frame with no dirty and no moved region is identical to the last one, so it
                    // is skipped — unless a key frame was requested. A viewer that connects to a
                    // completely idle desktop must still receive a picture, and a static screen
                    // would otherwise never produce one.
                    skip = (captured.IsStaticChange && !forceKey) || (tooEarly && !forceKey);
                    if (!skip)
                    {
                        Nv12Converter.Convert(captured, _settings.Width, _settings.Height, nv12);
                        lock (_statsGate)
                        {
                            _capturedFrames++;
                        }
                    }
                }

                if (skip)
                {
                    continue;
                }

                lastEncodedAt = clock.ElapsedTicks;
                long timestampUs = clock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
                bool requestKey = Interlocked.Exchange(ref _forceKeyFrame, 0) != 0;
                foreach (var sample in _encoder.Feed(nv12, timestampUs, requestKey))
                {
                    if (sample.Data.Length == 0)
                    {
                        continue;
                    }

                    bool isKey = sample.Summary.Idr > 0;
                    await _session.SendVideoFrameAsync(
                        sample.Data,
                        frameId++,
                        (ulong)timestampUs,
                        configId: 1,
                        isKey ? VideoFrameFlags.KeyFrame : VideoFrameFlags.None,
                        VideoCodec.H264,
                        cancellationToken).ConfigureAwait(false);

                    lock (_statsGate)
                    {
                        _encodedFrames++;
                        if (isKey)
                        {
                            _keyFrames++;
                        }

                        _sentBytes += sample.Data.Length;
                    }
                }

                _session.Tick();

                if (clock.ElapsedMilliseconds - lastStatsAt >= 500)
                {
                    lastStatsAt = clock.ElapsedMilliseconds;
                    StatsUpdated?.Invoke(Snapshot());
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            lock (_statsGate)
            {
                _lastError = $"{ex.GetType().Name}: {ex.Message}";
            }

            StatsUpdated?.Invoke(Snapshot());
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
        if (_waitTask is not null)
        {
            try
            {
                await _waitTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Waiting for a peer that never arrived is a normal shutdown.
            }
        }

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

        // Releasing held keys is the last thing that must happen on the controlled machine.
        _injector.ReleaseAll();
        _injector.Dispose();
        _encoder?.Dispose();
        _duplicator?.Dispose();
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }

        _lifetime.Dispose();
        await _videoTransport.DisposeAsync().ConfigureAwait(false);
        await _controlTransport.DisposeAsync().ConfigureAwait(false);
    }
}