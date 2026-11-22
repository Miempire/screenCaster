using System.Net;

namespace ScreenCast.Server;

/// <summary>
/// Fixed one second window limiter shared by both UDP sockets (one instance per socket).
/// The address table is cleared on every rollover, so it can never hold more entries than the
/// global budget of the window it belongs to. An over budget source still consumes global budget.
/// </summary>
public sealed class RateLimiter
{
    private readonly int globalPerSecond;
    private readonly int perAddressPerSecond;
    private readonly IMonotonicClock clock;
    private readonly Dictionary<IPAddress, int> perAddress = [];
    private long windowStartedMs;
    private int windowRequests;

    public RateLimiter(int globalPerSecond, int perAddressPerSecond, IMonotonicClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (globalPerSecond < 1) throw new ArgumentOutOfRangeException(nameof(globalPerSecond));
        if (perAddressPerSecond < 1 || perAddressPerSecond > globalPerSecond) throw new ArgumentOutOfRangeException(nameof(perAddressPerSecond));
        this.globalPerSecond = globalPerSecond;
        this.perAddressPerSecond = perAddressPerSecond;
        this.clock = clock;
        windowStartedMs = clock.Milliseconds;
    }

    public int GlobalPerSecond => globalPerSecond;

    public int PerAddressPerSecond => perAddressPerSecond;

    /// <summary>Returns true when the datagram may be processed. False means drop silently.</summary>
    public bool TryAcquire(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        long now = clock.Milliseconds;
        if (now - windowStartedMs >= 1000)
        {
            windowStartedMs = now;
            windowRequests = 0;
            perAddress.Clear();
        }
        if (windowRequests >= globalPerSecond) return false;
        windowRequests++;
        perAddress.TryGetValue(address, out int count);
        if (count >= perAddressPerSecond) return false;
        perAddress[address] = count + 1;
        return true;
    }
}