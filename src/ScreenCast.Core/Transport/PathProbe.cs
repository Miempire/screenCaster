using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ScreenCast.Core.Transport;

public enum ProbeKind : byte
{
    Request = 1,
    Response = 2,
}

/// <summary>
/// 32-byte encrypted probe payload: kind(1) reserved(7) token(8) sendMonotonicUs(8)
/// echoMonotonicUs(8).
/// </summary>
/// <remarks>
/// The echo carries the requester's own send timestamp back verbatim, so RTT is measured
/// entirely with the requester's monotonic clock and needs no clock synchronization between
/// peers. The token pairs a response with its request and is never trusted from an
/// unauthenticated datagram: the probe travels inside the session's AES-GCM envelope.
/// </remarks>
public readonly record struct ProbeMessage(
    ProbeKind Kind,
    ulong Token,
    ulong SendMonotonicMicroseconds,
    ulong EchoMonotonicMicroseconds)
{
    public const int Size = 32;

    public static byte[] EncodeRequest(ulong token, long nowMicroseconds) =>
        Encode(ProbeKind.Request, token, unchecked((ulong)nowMicroseconds), 0);

    public static byte[] EncodeResponse(ulong token, long nowMicroseconds, ulong echoMicroseconds) =>
        Encode(ProbeKind.Response, token, unchecked((ulong)nowMicroseconds), echoMicroseconds);

    private static byte[] Encode(ProbeKind kind, ulong token, ulong send, ulong echo)
    {
        byte[] payload = new byte[Size];
        payload[0] = (byte)kind;
        // payload[1..7] stay zero: reserved.
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(8), token);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(16), send);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(24), echo);
        return payload;
    }

    public static bool TryParse(ReadOnlySpan<byte> payload, out ProbeMessage message)
    {
        message = default;
        if (payload.Length != Size)
        {
            return false;
        }

        byte kind = payload[0];
        if (kind is not ((byte)ProbeKind.Request or (byte)ProbeKind.Response))
        {
            return false;
        }

        for (int i = 1; i < 8; i++)
        {
            if (payload[i] != 0)
            {
                return false;
            }
        }

        message = new ProbeMessage((ProbeKind)kind,
            BinaryPrimitives.ReadUInt64BigEndian(payload[8..]),
            BinaryPrimitives.ReadUInt64BigEndian(payload[16..]),
            BinaryPrimitives.ReadUInt64BigEndian(payload[24..]));
        return true;
    }

    public static ulong NewToken() => BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8));
}

public enum PathStatus
{
    /// <summary>Not enough evidence yet.</summary>
    Probing,

    /// <summary>Recent authenticated probes round-tripped.</summary>
    Alive,

    /// <summary>Consecutive keepalives produced no valid response.</summary>
    Down,
}

/// <summary>
/// Keepalive, RTT and loss tracking for one component. Sending a probe never refreshes the
/// "last seen a valid response" timestamp: only a matching, authenticated response does.
/// </summary>
public sealed class PathProbe
{
    /// <summary>Maximum outstanding requests kept for token matching.</summary>
    public const int MaxOutstanding = 32;
    public const int DefaultMissesForDown = 3;

    private readonly Func<long> _nowMicroseconds;
    private readonly int _missesForDown;
    private readonly Queue<OutstandingProbe> _outstanding = new();
    private readonly Dictionary<ulong, long> _lookup = [];
    private long _sent;
    private long _answered;
    private int _consecutiveMisses;
    private double _srtt = -1;
    private long _intervalMicroseconds;

    public PathProbe(Func<long>? nowMicroseconds = null, int missesForDown = DefaultMissesForDown,
        long keepaliveIntervalMicroseconds = 10_000_000)
    {
        _nowMicroseconds = nowMicroseconds ?? DefaultClock;
        _missesForDown = Math.Max(1, missesForDown);
        _intervalMicroseconds = keepaliveIntervalMicroseconds;
    }

    public PathStatus Status { get; private set; } = PathStatus.Probing;

    public int ConsecutiveMisses => _consecutiveMisses;

    public long KeepaliveIntervalMicroseconds
    {
        get => _intervalMicroseconds;
        set => _intervalMicroseconds = Math.Max(1_000_000, value);
    }

    /// <summary>Smoothed RTT in microseconds; -1 until a response arrives.</summary>
    public double SmoothedRttMicroseconds => _srtt;

    public long ProbesSent => _sent;

    public long ProbesAnswered => _answered;

    /// <summary>Fraction of probes never answered, 0..1.</summary>
    public double LossRatio => _sent == 0 ? 0 : 1.0 - ((double)_answered / _sent);

    private static long DefaultClock() =>
        System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Build a request payload and remember its send time for token matching.</summary>
    public byte[] CreateRequest()
    {
        ulong token = ProbeMessage.NewToken();
        long now = _nowMicroseconds();
        Remember(token, now);
        _sent++;

        // The very first send starts the silence clock. Later sends must not refresh it,
        // otherwise a path that never answers could never be declared down.
        if (_silenceSince == long.MinValue)
        {
            _silenceSince = now;
        }

        return ProbeMessage.EncodeRequest(token, now);
    }

    /// <summary>
    /// Feed a received probe payload. Returns an optional response payload to send back.
    /// A request is answered and refreshes liveness; a response completes an RTT sample.
    /// </summary>
    public byte[]? OnProbe(ReadOnlySpan<byte> payload)
    {
        if (!ProbeMessage.TryParse(payload, out var message))
        {
            return null;
        }

        long now = _nowMicroseconds();

        if (message.Kind == ProbeKind.Request)
        {
            MarkAlive(now);
            return ProbeMessage.EncodeResponse(message.Token, now, message.SendMonotonicMicroseconds);
        }

        if (!_lookup.Remove(message.Token, out long sentAt))
        {
            return null;
        }

        long sample = now - sentAt;
        if (sample < 0)
        {
            return null;
        }

        _srtt = _srtt < 0 ? sample : (_srtt * 7.0 + sample) / 8.0;
        _answered++;
        MarkAlive(now);
        return null;
    }

    private void MarkAlive(long now)
    {
        _silenceSince = now;
        _consecutiveMisses = 0;
        Status = PathStatus.Alive;
    }

    private void Remember(ulong token, long sentAt)
    {
        // Bounded outstanding set: drop the oldest token rather than growing without limit.
        while (_outstanding.Count >= MaxOutstanding)
        {
            var evicted = _outstanding.Dequeue();
            _lookup.Remove(evicted.Token);
        }

        _outstanding.Enqueue(new OutstandingProbe(token, sentAt));
        _lookup[token] = sentAt;
    }

    /// <summary>
    /// Drive keepalive. Call from a timer. Returns true when the caller should send a keepalive
    /// request for this component.
    /// </summary>
    public bool Tick()
    {
        long now = _nowMicroseconds();

        if (_silenceSince != long.MinValue && now - _silenceSince > _intervalMicroseconds)
        {
            _consecutiveMisses++;
            // Advance the reference by one interval so one long stall is not counted N times.
            _silenceSince = now;
            if (_consecutiveMisses >= _missesForDown)
            {
                Status = PathStatus.Down;
            }
        }

        if (_nextProbeAt == long.MinValue || now >= _nextProbeAt)
        {
            _nextProbeAt = now + _intervalMicroseconds;
            return true;
        }

        return false;
    }

    /// <summary>
    /// When the silence clock started: the first send, or the most recent valid response.
    /// <c>long.MinValue</c> means no probe was ever sent.
    /// </summary>
    public long SilenceSinceMicroseconds => _silenceSince;

    private long _silenceSince = long.MinValue;

    private long _nextProbeAt = long.MinValue;

    private sealed record OutstandingProbe(ulong Token, long SentAt);
}