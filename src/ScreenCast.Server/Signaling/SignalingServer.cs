using System.Net;
using System.Net.Sockets;

namespace ScreenCast.Server.Signaling;

/// <summary>
/// Owns the signaling UDP socket: rate limiting, framing, dispatch, cleanup scans and socket release.
/// One receive loop and one scan loop; no per client tasks and no unbounded queues.
/// </summary>
public sealed class SignalingServer : IAsyncDisposable
{
    private readonly SignalingOptions options;
    private readonly IMonotonicClock clock;
    private readonly SignalingService service;
    private readonly RateLimiter limiter;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? linkedLifetime;
    private UdpClient? socket;
    private Task? receiveLoop;
    private Task? scanLoop;
    private int started;
    private int disposed;
    private long received;
    private long oversize;
    private long rateLimited;
    private long sendFailures;
    private long socketErrors;
    private long dispatchFailures;

    public SignalingServer(SignalingOptions options, IMonotonicClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
        this.clock = clock ?? SystemMonotonicClock.Instance;
        service = new SignalingService(options);
        limiter = new RateLimiter(options.GlobalRequestsPerSecond, options.PerAddressRequestsPerSecond, this.clock);
    }

    public SignalingOptions Options => options;

    public SignalingService Service => service;

    /// <summary>Actual bound endpoint. Port 0 in the options means the OS chose this port.</summary>
    public IPEndPoint LocalEndPoint { get; private set; } = new(IPAddress.Loopback, 0);

    public long ReceivedCount => Interlocked.Read(ref received);

    public long OversizeDropped => Interlocked.Read(ref oversize);

    public long RateLimitedDropped => Interlocked.Read(ref rateLimited);

    public long SendFailures => Interlocked.Read(ref sendFailures);

    public long SocketErrors => Interlocked.Read(ref socketErrors);

    public long DispatchFailures => Interlocked.Read(ref dispatchFailures);

    /// <summary>Binds the socket and starts both loops. Throws <see cref="SocketException"/> if the port is unavailable.</summary>
    public void Start(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Signaling server already started.");
        var client = new UdpClient(options.BindAddress.AddressFamily);
        if (options.BindAddress.AddressFamily == AddressFamily.InterNetworkV6) client.Client.DualMode = false;
        try
        {
            client.Client.Bind(new IPEndPoint(options.BindAddress, options.Port));
        }
        catch
        {
            client.Dispose();
            throw;
        }
        socket = client;
        LocalEndPoint = (IPEndPoint)client.Client.LocalEndPoint!;
        linkedLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        receiveLoop = ReceiveLoopAsync(client, linkedLifetime.Token);
        scanLoop = ScanLoopAsync(linkedLifetime.Token);
    }

    /// <summary>Runs one cleanup scan synchronously and sends the resulting notifications.</summary>
    public int ScanOnce()
    {
        var outbox = new List<OutboundMessage>();
        int removed = service.Scan(clock.Milliseconds, outbox);
        foreach (OutboundMessage message in outbox) SendBlocking(message);
        return removed;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        try
        {
            socket?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already closed; nothing to release.
        }
        foreach (Task? loop in new[] { receiveLoop, scanLoop })
        {
            if (loop is null) continue;
            try
            {
                await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // Loop terminated by shutdown; expected.
            }
        }
        receiveLoop = null;
        scanLoop = null;
        socket = null;
        linkedLifetime?.Dispose();
        linkedLifetime = null;
        sendGate.Dispose();
        lifetime.Dispose();
    }

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Interlocked.Increment(ref socketErrors);
                if (token.IsCancellationRequested) break;
                // ICMP port unreachable surfaces as ConnectionReset on Windows and only affects one datagram.
                if (ex.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted) break;
                continue;
            }
            Interlocked.Increment(ref received);
            if (result.Buffer.Length > SignalingWire.MaxMessageBytes)
            {
                Interlocked.Increment(ref oversize);
                continue;
            }
            if (!limiter.TryAcquire(result.RemoteEndPoint.Address))
            {
                Interlocked.Increment(ref rateLimited);
                continue;
            }
            var outbox = new List<OutboundMessage>(4);
            try
            {
                service.Handle(result.Buffer, result.RemoteEndPoint, clock.Milliseconds, outbox);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A defect must not stop the loop; nothing about the payload is logged.
                Interlocked.Increment(ref dispatchFailures);
                continue;
            }
            foreach (OutboundMessage message in outbox)
            {
                await SendAsync(client, message, token).ConfigureAwait(false);
            }
        }
    }

    private async Task ScanLoopAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.ScanIntervalMs));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                ScanOnce();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Socket closed while scanning; shutdown path.
        }
    }

    private async Task SendAsync(UdpClient client, OutboundMessage message, CancellationToken token)
    {
        try
        {
            await sendGate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            await client.SendAsync(message.Datagram.AsMemory(), message.Destination, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            Interlocked.Increment(ref sendFailures);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private void SendBlocking(OutboundMessage message)
    {
        UdpClient? client = socket;
        if (client is null) return;
        if (!sendGate.Wait(TimeSpan.FromSeconds(5)))
        {
            Interlocked.Increment(ref sendFailures);
            return;
        }
        try
        {
            client.Send(message.Datagram, message.Datagram.Length, message.Destination);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
            Interlocked.Increment(ref sendFailures);
        }
        finally
        {
            sendGate.Release();
        }
    }
}