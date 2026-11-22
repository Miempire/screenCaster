using System.Net;

namespace ScreenCast.Core.Transport;

/// <summary>
/// The complete link path decision: NAT-driven strategy, hole punching with a hard attempt cap,
/// relay fallback and keepalive supervision for both components.
/// </summary>
/// <remarks>
/// <para>
/// This class owns policy only. Sending, encryption and authentication stay in
/// <see cref="MediaSession"/> and the caller: a punch can only complete through an authenticated
/// probe, never through a bare datagram.
/// </para>
/// <para>
/// Rules encoded here: symmetric mapping skips punching entirely; three failed punch attempts
/// request relay; a component that stops answering keepalives marks the path down so the caller
/// can release held input and reconnect.
/// </para>
/// </remarks>
public sealed class LinkController
{
    public const int PunchAttemptsBeforeRelay = 3;

    private readonly PathProbe _videoProbe;
    private readonly PathProbe _controlProbe;
    private readonly Func<long> _now;
    private PunchCoordinator? _punch;
    private IPEndPoint? _relayEndpoint;

    public LinkController(
        NatObservation natObservation,
        Func<long>? nowMicroseconds = null,
        long keepaliveIntervalMicroseconds = 10_000_000,
        int missesForDown = PathProbe.DefaultMissesForDown)
    {
        NatObservation = natObservation;
        // One clock for the whole controller: a punch scheduled on a different clock than the
        // keepalive probes would drift out of sync and could never exhaust deterministically.
        _now = nowMicroseconds ?? (static () => System.Diagnostics.Stopwatch.GetTimestamp()
            * 1_000_000L / System.Diagnostics.Stopwatch.Frequency);
        _videoProbe = new PathProbe(_now, missesForDown, keepaliveIntervalMicroseconds);
        _controlProbe = new PathProbe(_now, missesForDown, keepaliveIntervalMicroseconds);
    }

    public NatObservation NatObservation { get; }

    public LinkState State { get; private set; } = LinkState.Idle;

    /// <summary>1-based punch attempt number; 0 before punching starts.</summary>
    public int PunchAttempt => _punch?.Attempt ?? 0;

    public PathProbe VideoProbe => _videoProbe;

    public PathProbe ControlProbe => _controlProbe;

    public IPEndPoint? SelectedPeer { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>
    /// Choose the initial strategy. Symmetric mapping goes straight to relay; everything else,
    /// including an unknown NAT type, tries a real punch first.
    /// </summary>
    public void Begin(IReadOnlyList<IPEndPoint> remoteCandidates, int generation, Func<long>? nowMicroseconds = null)
    {
        ArgumentNullException.ThrowIfNull(remoteCandidates);
        if (remoteCandidates.Count == 0)
        {
            throw new ArgumentException("At least one remote candidate is required.", nameof(remoteCandidates));
        }

        if (NatObservation == NatObservation.Symmetric)
        {
            // A symmetric mapping cannot be predicted from another destination, so punching is
            // pure waste: go to the relay immediately.
            State = LinkState.RelayRequired;
            Reason = "symmetric NAT mapping";
            return;
        }

        _punch = new PunchCoordinator(generation, remoteCandidates, nowMicroseconds ?? _now);
        _punch.Start();
        State = LinkState.Punching;
    }

    /// <summary>
    /// Candidates that need a punch probe right now, for both components. Empty when punching is
    /// finished, relay is required, or nothing is due.
    /// </summary>
    public IReadOnlyList<IPEndPoint> TickPunch()
    {
        if (_punch is null || State is not (LinkState.Punching or LinkState.PunchingExhausted))
        {
            return [];
        }

        var due = _punch.Tick();
        if (due.Count > 0)
        {
            return due;
        }

        if (_punch.Exhausted && State == LinkState.Punching)
        {
            State = LinkState.PunchingExhausted;
            Reason = $"{PunchAttemptsBeforeRelay} punch attempts failed";
        }

        return [];
    }

    /// <summary>
    /// One authenticated probe arrived from <paramref name="source"/> on the named component.
    /// Returns true when this completed a direct path.
    /// </summary>
    public bool OnAuthenticatedProbe(TransportComponent component, IPEndPoint source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (State is not (LinkState.Punching or LinkState.PunchingExhausted))
        {
            return false;
        }

        // The probe proves the path works, so the keepalive clock starts from evidence.
        var probe = component == TransportComponent.Video ? _videoProbe : _controlProbe;
        probe.CreateRequest();

        if (_punch is null || !_punch.OnAuthenticatedProbe(source))
        {
            return false;
        }

        SelectedPeer = source;
        State = LinkState.Direct;
        Reason = null;
        return true;
    }

    /// <summary>Mark relay as the active path once the TURN allocation and probes succeeded.</summary>
    public void RelayEstablished(IPEndPoint relayEndpoint)
    {
        ArgumentNullException.ThrowIfNull(relayEndpoint);
        _relayEndpoint = relayEndpoint;
        SelectedPeer = relayEndpoint;
        State = LinkState.Relay;
        Reason = null;
    }

    public IPEndPoint? RelayEndpoint => _relayEndpoint;

    /// <summary>Relay could not be allocated or never answered: give up with a stated reason.</summary>
    public void Fail(string reason)
    {
        State = LinkState.Failed;
        Reason = reason;
    }

    /// <summary>
    /// Keepalive tick for both components. Returns true when the caller must send a probe on that
    /// component. A component that reaches <c>Down</c> moves the whole link to <see cref="LinkState.Down"/>.
    /// </summary>
    public bool TickKeepalive(out bool sendVideoProbe, out bool sendControlProbe)
    {
        sendVideoProbe = _videoProbe.Tick();
        sendControlProbe = _controlProbe.Tick();

        if (State is LinkState.Direct or LinkState.Relay)
        {
            if (_videoProbe.Status == PathStatus.Down || _controlProbe.Status == PathStatus.Down)
            {
                State = LinkState.Down;
                Reason = _videoProbe.Status == PathStatus.Down
                    ? "video component stopped answering keepalives"
                    : "control component stopped answering keepalives";
            }
        }

        return sendVideoProbe || sendControlProbe;
    }

    /// <summary>Feed an authenticated probe payload; returns the reply payload when it was a request.</summary>
    public byte[]? OnProbePayload(TransportComponent component, ReadOnlySpan<byte> payload) =>
        component == TransportComponent.Video
            ? _videoProbe.OnProbe(payload)
            : _controlProbe.OnProbe(payload);
}

public enum LinkState
{
    Idle,
    Punching,
    PunchingExhausted,
    Direct,
    RelayRequired,
    Relay,
    Down,
    Failed,
}