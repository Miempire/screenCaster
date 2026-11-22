using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace ScreenCast.Core.Transport;

/// <summary>
/// One peer-to-peer media session: two independent UDP components (video and control), each
/// with its own direction-scoped keys, packet sequence space, keepalive and receive loop.
/// </summary>
/// <remarks>
/// <para>
/// The session does not allocate, authenticate or refresh TURN resources. Those belong to the
/// TURN client; this class only carries already-encrypted application traffic. When a relay is
/// used, the same two transports are pointed at the relay's allocation instead of the direct
/// peer address, and the payload stays end-to-end encrypted.
/// </para>
/// <para>
/// Cryptographic failures are counted and dropped, never propagated: a forged, replayed or
/// wrong-context datagram must not tear down a working session.
/// </para>
/// <para>
/// Control frames leave through a bounded queue consumed by a single sender task. That keeps the
/// key owner of the control protector single-threaded and stops a slow socket from blocking the
/// input path. Video is sent on the caller's thread and must be serialized by the caller.
/// </para>
/// </remarks>
public sealed class MediaSession : IAsyncDisposable
{
    private const int ControlQueueCapacity = 256;

    private readonly UdpTransport _video;
    private readonly UdpTransport _control;
    private readonly PacketProtector _videoSend;
    private readonly PacketProtector _videoReceive;
    private readonly PacketProtector _controlSend;
    private readonly PacketProtector _controlReceive;
    private readonly PacketProtector _videoProbeSend;
    private readonly PacketProtector _videoProbeReceive;
    private readonly PacketProtector _controlProbeSend;
    private readonly PacketProtector _controlProbeReceive;
    private readonly VideoAssembler _assembler = new();
    private readonly PathProbe _videoProbe = new();
    private readonly PathProbe _controlProbe = new();
    private readonly Channel<byte[]> _controlOutbound = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(ControlQueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action<AssembledVideoFrame> _onVideoFrame;
    private ReliableControlChannel _controlChannel = null!;
    private IPEndPoint _videoPeer;
    private IPEndPoint _controlPeer;
    private Task? _videoLoop;
    private Task? _controlReceiveLoop;
    private Task? _controlSendLoop;
    private long _videoDatagramsReceived;
    private long _videoDatagramsRejected;
    private long _controlDatagramsReceived;
    private long _controlDatagramsRejected;
    private long _controlFramesDropped;
    private bool _disposed;

    private MediaSession(
        UdpTransport video,
        UdpTransport control,
        PacketProtector videoSend,
        PacketProtector videoReceive,
        PacketProtector controlSend,
        PacketProtector controlReceive,
        PacketProtector videoProbeSend,
        PacketProtector videoProbeReceive,
        PacketProtector controlProbeSend,
        PacketProtector controlProbeReceive,
        Action<AssembledVideoFrame> onVideoFrame)
    {
        _video = video;
        _control = control;
        _videoSend = videoSend;
        _videoReceive = videoReceive;
        _controlSend = controlSend;
        _controlReceive = controlReceive;
        _videoProbeSend = videoProbeSend;
        _videoProbeReceive = videoProbeReceive;
        _controlProbeSend = controlProbeSend;
        _controlProbeReceive = controlProbeReceive;
        _onVideoFrame = onVideoFrame;
        _videoPeer = new IPEndPoint(IPAddress.Any, 0);
        _controlPeer = new IPEndPoint(IPAddress.Any, 0);
    }

    public long VideoDatagramsReceived => Interlocked.Read(ref _videoDatagramsReceived);

    public long VideoDatagramsRejected => Interlocked.Read(ref _videoDatagramsRejected);

    public long ControlDatagramsReceived => Interlocked.Read(ref _controlDatagramsReceived);

    public long ControlDatagramsRejected => Interlocked.Read(ref _controlDatagramsRejected);

    public long ControlFramesDropped => Interlocked.Read(ref _controlFramesDropped);

    public long AssembledFrames { get; private set; }

    public long DroppedFrames => _assembler.DroppedFrames;

    public ReliableControlChannel Control => _controlChannel;

    public UdpTransport VideoTransport => _video;

    public UdpTransport ControlTransport => _control;

    public IPEndPoint VideoPeer => _videoPeer;

    public IPEndPoint ControlPeer => _controlPeer;

    /// <summary>
    /// Build a session. A host sends HostToViewer and receives ViewerToHost; a viewer is the
    /// mirror image. Both directions get independent keys and packet sequence spaces.
    /// </summary>
    public static MediaSession Create(
        bool isHost,
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> sessionId,
        uint keyEpoch,
        UdpTransport videoTransport,
        UdpTransport controlTransport,
        IPEndPoint initialPeer,
        Action<AssembledVideoFrame> onVideoFrame,
        Action<ControlEventType, ReadOnlyMemory<byte>> onControlEvent,
        Func<long>? nowMicroseconds = null)
    {
        ArgumentNullException.ThrowIfNull(videoTransport);
        ArgumentNullException.ThrowIfNull(controlTransport);
        ArgumentNullException.ThrowIfNull(initialPeer);
        ArgumentNullException.ThrowIfNull(onVideoFrame);
        ArgumentNullException.ThrowIfNull(onControlEvent);

        if (videoTransport.LocalPort == controlTransport.LocalPort
            && Equals(videoTransport.LocalEndPoint, controlTransport.LocalEndPoint))
        {
            throw new ArgumentException("Video and control must use independent transports.", nameof(controlTransport));
        }

        PacketDirection outbound = isHost ? PacketDirection.HostToViewer : PacketDirection.ViewerToHost;
        PacketDirection inbound = isHost ? PacketDirection.ViewerToHost : PacketDirection.HostToViewer;

        PacketProtector videoSend = new(sharedSecret, sessionId, keyEpoch, outbound, PacketChannel.Video, 1);
        PacketProtector videoReceive = new(sharedSecret, sessionId, keyEpoch, inbound, PacketChannel.Video, 1);
        PacketProtector controlSend = new(sharedSecret, sessionId, keyEpoch, outbound, PacketChannel.ReliableControl, 2);
        PacketProtector controlReceive = new(sharedSecret, sessionId, keyEpoch, inbound, PacketChannel.ReliableControl, 2);
        PacketProtector videoProbeSend = new(sharedSecret, sessionId, keyEpoch, outbound, PacketChannel.Probe, 1);
        PacketProtector videoProbeReceive = new(sharedSecret, sessionId, keyEpoch, inbound, PacketChannel.Probe, 1);
        PacketProtector controlProbeSend = new(sharedSecret, sessionId, keyEpoch, outbound, PacketChannel.Probe, 2);
        PacketProtector controlProbeReceive = new(sharedSecret, sessionId, keyEpoch, inbound, PacketChannel.Probe, 2);

        try
        {
            var session = new MediaSession(videoTransport, controlTransport, videoSend, videoReceive,
                controlSend, controlReceive, videoProbeSend, videoProbeReceive, controlProbeSend,
                controlProbeReceive, onVideoFrame);
            session._controlChannel = new ReliableControlChannel(
                session.EnqueueControlFrame, onControlEvent, nowMicroseconds);
            session.SetPeer(initialPeer);
            return session;
        }
        catch
        {
            videoSend.Dispose();
            videoReceive.Dispose();
            controlSend.Dispose();
            controlReceive.Dispose();
            videoProbeSend.Dispose();
            videoProbeReceive.Dispose();
            controlProbeSend.Dispose();
            controlProbeReceive.Dispose();
            throw;
        }
    }

    private void EnqueueControlFrame(ReadOnlyMemory<byte> frame)
    {
        if (!_controlOutbound.Writer.TryWrite(frame.ToArray()))
        {
            Interlocked.Increment(ref _controlFramesDropped);
        }
    }

    /// <summary>Point both components at one peer address (relay allocation or a same-port peer).</summary>
    public void SetPeer(IPEndPoint peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        _videoPeer = peer;
        _controlPeer = peer;
    }

    /// <summary>
    /// Set component peer addresses independently. Direct P2P peers normally have different
    /// video and control ports because each component owns its own socket and mapping.
    /// </summary>
    public void SetPeers(IPEndPoint videoPeer, IPEndPoint controlPeer)
    {
        ArgumentNullException.ThrowIfNull(videoPeer);
        ArgumentNullException.ThrowIfNull(controlPeer);
        _videoPeer = videoPeer;
        _controlPeer = controlPeer;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _videoLoop ??= Task.Run(() => ReceiveVideoLoopAsync(_lifetime.Token));
        _controlReceiveLoop ??= Task.Run(() => ReceiveControlLoopAsync(_lifetime.Token));
        _controlSendLoop ??= Task.Run(() => SendControlLoopAsync(_lifetime.Token));
    }

    /// <summary>Fragment, encrypt and send one encoded frame across the video component.</summary>
    public async ValueTask SendVideoFrameAsync(
        ReadOnlyMemory<byte> encodedFrame,
        ulong frameId,
        ulong captureTimestampMicroseconds,
        uint configId,
        VideoFrameFlags flags,
        VideoCodec codec,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (encodedFrame.Length == 0)
        {
            throw new ArgumentException("Encoded frame is empty.", nameof(encodedFrame));
        }

        int count = VideoFragmenter.FragmentCountFor(encodedFrame.Length);
        byte[] buffer = new byte[VideoFragmentHeader.MaxFragmentPayload + VideoFragmentHeader.Size];
        for (int index = 0; index < count; index++)
        {
            int length = VideoFragmenter.WriteFragment(encodedFrame.Span, frameId,
                captureTimestampMicroseconds, configId, flags, codec, index, buffer);
            byte[] datagram = _videoSend.Encode(buffer.AsSpan(0, length));
            await _video.SendAsync(datagram, _videoPeer, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Queue one reliable control event. Returns false when the send window is full.</summary>
    public bool TrySendControl(ControlEventType eventType, ReadOnlySpan<byte> body)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _controlChannel.TryEnqueue(eventType, body);
    }

    public void Tick()
    {
        _controlChannel.Tick();

        // Probes keep the mapping alive and give the peer an RTT sample. A peer that speaks the
        // protocol but never probes would otherwise be marked Down by the other side, so both
        // directions send them independently of media traffic.
        long now = Environment.TickCount64 * 1000L;
        if (_videoProbe.Tick())
        {
            SendProbe(_videoProbeSend, _video, _videoPeer, _videoProbe.CreateRequest());
        }

        if (_controlProbe.Tick())
        {
            SendProbe(_controlProbeSend, _control, _controlPeer, _controlProbe.CreateRequest());
        }

        _ = now;
    }

    public PathProbe VideoProbe => _videoProbe;

    public PathProbe ControlProbe => _controlProbe;

    private void SendProbe(PacketProtector protector, UdpTransport transport, IPEndPoint peer, byte[] payload)
    {
        byte[] datagram = protector.Encode(payload);
        // Probes are best effort: a full socket buffer must not stall the caller's tick.
        _ = transport.SendAsync(datagram, peer, CancellationToken.None).AsTask()
            .ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Handle an inbound encrypted probe and reply when it is a request.</summary>
    private void OnProbeDatagram(DatagramLease lease, bool videoComponent)
    {
        PacketProtector receive = videoComponent ? _videoProbeReceive : _controlProbeReceive;
        PacketProtector send = videoComponent ? _videoProbeSend : _controlProbeSend;
        PathProbe probe = videoComponent ? _videoProbe : _controlProbe;
        UdpTransport transport = videoComponent ? _video : _control;
        IPEndPoint peer = videoComponent ? _videoPeer : _controlPeer;

        byte[] plaintext;
        try
        {
            plaintext = receive.Decode(lease.Payload.Span);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return;
        }

        byte[]? reply = probe.OnProbe(plaintext);
        if (reply is not null)
        {
            SendProbe(send, transport, peer, reply);
        }
    }

    private async Task ReceiveVideoLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DatagramLease? lease = null;
            try
            {
                lease = await _video.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                var kind = DatagramClassifier.Classify(lease.Payload.Span);
                if (kind != DatagramKind.Application)
                {
                    continue;
                }

                if (IsProbe(lease.Payload.Span))
                {
                    OnProbeDatagram(lease, videoComponent: true);
                    continue;
                }

                Interlocked.Increment(ref _videoDatagramsReceived);
                byte[] plaintext;
                try
                {
                    plaintext = _videoReceive.Decode(lease.Payload.Span);
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    Interlocked.Increment(ref _videoDatagramsRejected);
                    continue;
                }

                var frame = _assembler.Push(plaintext);
                if (frame is not null)
                {
                    AssembledFrames++;
                    _onVideoFrame(frame);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // A transient receive error must not end the session; retry the next datagram.
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lease?.Dispose();
            }
        }
    }

    private async Task ReceiveControlLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DatagramLease? lease = null;
            try
            {
                lease = await _control.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (DatagramClassifier.Classify(lease.Payload.Span) != DatagramKind.Application)
                {
                    continue;
                }

                if (IsProbe(lease.Payload.Span))
                {
                    OnProbeDatagram(lease, videoComponent: false);
                    continue;
                }

                Interlocked.Increment(ref _controlDatagramsReceived);
                byte[] plaintext;
                try
                {
                    plaintext = _controlReceive.Decode(lease.Payload.Span);
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    Interlocked.Increment(ref _controlDatagramsRejected);
                    continue;
                }

                _controlChannel.OnFrame(plaintext);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (System.Net.Sockets.SocketException)
            {
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lease?.Dispose();
            }
        }
    }

    private static bool IsProbe(ReadOnlySpan<byte> datagram) =>
        datagram.Length > 5 && datagram[5] == (byte)PacketChannel.Probe;

    private async Task SendControlLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _controlOutbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_controlOutbound.Reader.TryRead(out var frame))
                {
                    byte[] datagram = _controlSend.Encode(frame);
                    await _control.SendAsync(datagram, _controlPeer, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controlOutbound.Writer.TryComplete();
        _lifetime.Cancel();
        Task[] loops =
        [
            .. new[] { _videoLoop, _controlReceiveLoop, _controlSendLoop }
                .Where(task => task is not null)
                .Select(task => task!),
        ];
        if (loops.Length > 0)
        {
            try
            {
                await Task.WhenAll(loops).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cancellation of a receive loop is expected during shutdown.
            }
        }

        _lifetime.Dispose();
        _videoSend.Dispose();
        _videoReceive.Dispose();
        _controlSend.Dispose();
        _controlReceive.Dispose();
        _videoProbeSend.Dispose();
        _videoProbeReceive.Dispose();
        _controlProbeSend.Dispose();
        _controlProbeReceive.Dispose();
    }
}