using System.Net;
using System.Net.Sockets;
using ScreenCast.Core;

namespace ScreenCast.Server;

/// <summary>
/// RFC5389 UDP Binding only (same behaviour as the original single-socket server): 1200 byte request
/// cap, 200 requests per second globally, 20 per source address, unknown mandatory attributes get 420.
/// TURN Allocate and other methods are dropped.
/// </summary>
public sealed class StunService : IAsyncDisposable
{
    private const int MaxRequestBytes = 1200;

    private readonly IPAddress bindAddress;
    private readonly int port;
    private readonly IMonotonicClock clock;
    private readonly RateLimiter limiter;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? linkedLifetime;
    private UdpClient? socket;
    private Task? loop;
    private int started;
    private int disposed;

    public StunService(IPAddress bindAddress, int port, IMonotonicClock? clock = null, RateLimiter? limiter = null)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        this.bindAddress = bindAddress;
        this.port = port;
        this.clock = clock ?? SystemMonotonicClock.Instance;
        this.limiter = limiter ?? new RateLimiter(200, 20, this.clock);
    }

    public IPEndPoint LocalEndPoint { get; private set; } = new(IPAddress.Loopback, 0);

    public RateLimiter Limiter => limiter;

    public void Start(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("STUN service already started.");
        var client = new UdpClient(bindAddress.AddressFamily);
        if (bindAddress.AddressFamily == AddressFamily.InterNetworkV6) client.Client.DualMode = false;
        try
        {
            client.Client.Bind(new IPEndPoint(bindAddress, port));
        }
        catch
        {
            client.Dispose();
            throw;
        }
        socket = client;
        LocalEndPoint = (IPEndPoint)client.Client.LocalEndPoint!;
        linkedLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        loop = RunAsync(client, linkedLifetime.Token);
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
            // Already closed.
        }
        if (loop is not null)
        {
            try
            {
                await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // Expected during shutdown.
            }
        }
        loop = null;
        socket = null;
        linkedLifetime?.Dispose();
        linkedLifetime = null;
        sendGate.Dispose();
        lifetime.Dispose();
    }

    private async Task RunAsync(UdpClient client, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await client.ReceiveAsync(token).ConfigureAwait(false);
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
                if (token.IsCancellationRequested) break;
                if (ex.SocketErrorCode is not (SocketError.ConnectionReset or SocketError.MessageSize))
                {
                    Console.Error.WriteLine($"UDP error: {ex.SocketErrorCode}");
                }
                continue;
            }
            if (!limiter.TryAcquire(datagram.RemoteEndPoint.Address)) continue;
            if (datagram.Buffer.Length > MaxRequestBytes) continue;
            byte[]? response;
            try
            {
                response = StunBindingCodec.CreateBindingResponse(datagram.Buffer, datagram.RemoteEndPoint);
            }
            catch (FormatException)
            {
                continue;
            }
            if (response is null) continue;
            await SendAsync(client, response, datagram.RemoteEndPoint, token).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(UdpClient client, byte[] response, IPEndPoint destination, CancellationToken token)
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
            await client.SendAsync(response.AsMemory(), destination, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // A failed reply is dropped; the client retransmits per RFC5389 timing.
        }
        finally
        {
            sendGate.Release();
        }
    }
}