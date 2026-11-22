using System.Net;

namespace ScreenCast.Core.Transport;

/// <summary>
/// Schedules hole-punch probes for one component across at most three attempts.
/// </summary>
/// <remarks>
/// <para>
/// The coordinator only decides when to send toward which address. Encryption, token matching
/// and authentication stay in <see cref="MediaSession"/>: an unauthenticated datagram must never
/// be able to declare a punch successful.
/// </para>
/// <para>
/// Any authenticated probe that arrives, even from an address outside the candidate list, wins.
/// That is the normal outcome for endpoint-independent mappings, where the NAT assigns a
/// mapping the signalling server never saw.
/// </para>
/// </remarks>
public sealed class PunchCoordinator
{
    public const int MaxAttempts = 3;
    public const long AttemptDurationMicroseconds = 1_500_000;

    /// <summary>Send offsets inside one attempt, in microseconds.</summary>
    public static readonly long[] SendOffsetsMicroseconds = [0, 100_000, 250_000, 500_000, 900_000];

    private readonly int _generation;
    private readonly IReadOnlyList<IPEndPoint> _remoteCandidates;
    private readonly Func<long> _nowMicroseconds;
    private readonly HashSet<(long Offset, IPEndPoint Target)> _sent = [];
    private long _attemptStartedAt = long.MinValue;
    private int _attempt;

    public PunchCoordinator(int generation, IReadOnlyList<IPEndPoint> remoteCandidates,
        Func<long>? nowMicroseconds = null)
    {
        ArgumentNullException.ThrowIfNull(remoteCandidates);
        if (remoteCandidates.Count == 0)
        {
            throw new ArgumentException("At least one remote candidate is required.", nameof(remoteCandidates));
        }

        _generation = generation;
        _remoteCandidates = remoteCandidates;
        _nowMicroseconds = nowMicroseconds ?? DefaultClock;
    }

    public int Generation => _generation;

    /// <summary>Current attempt number, 1-based. Zero before <see cref="Start"/>.</summary>
    public int Attempt => _attempt;

    public bool Succeeded { get; private set; }

    public bool Exhausted { get; private set; }

    public IPEndPoint? SelectedPeer { get; private set; }

    private static long DefaultClock() =>
        System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Begin the first attempt. Calling twice keeps the existing attempt.</summary>
    public void Start()
    {
        if (_attempt != 0 || Succeeded || Exhausted)
        {
            return;
        }

        BeginAttempt(1);
    }

    private void BeginAttempt(int attempt)
    {
        _attempt = attempt;
        _attemptStartedAt = _nowMicroseconds();
        _sent.Clear();
    }

    /// <summary>
    /// Returns the candidates that must receive a probe now. Empty when nothing is due, when the
    /// punch already succeeded, or when attempts are exhausted.
    /// </summary>
    public IReadOnlyList<IPEndPoint> Tick()
    {
        if (Succeeded || Exhausted)
        {
            return [];
        }

        if (_attempt == 0)
        {
            Start();
        }

        long now = _nowMicroseconds();
        long elapsed = now - _attemptStartedAt;

        if (elapsed >= AttemptDurationMicroseconds)
        {
            if (_attempt >= MaxAttempts)
            {
                Exhausted = true;
                return [];
            }

            BeginAttempt(_attempt + 1);
            elapsed = 0;
        }

        List<IPEndPoint>? due = null;
        foreach (long offset in SendOffsetsMicroseconds)
        {
            if (offset > elapsed)
            {
                continue;
            }

            foreach (var candidate in _remoteCandidates)
            {
                if (_sent.Add((offset, candidate)))
                {
                    (due ??= []).Add(candidate);
                }
            }
        }

        return due ?? [];
    }

    /// <summary>
    /// Record one authenticated probe from <paramref name="source"/>. Returns true when this call
    /// completed the punch.
    /// </summary>
    public bool OnAuthenticatedProbe(IPEndPoint source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Succeeded || Exhausted)
        {
            return false;
        }

        SelectedPeer = source;
        Succeeded = true;
        return true;
    }
}