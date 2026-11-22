using System.Net;

namespace ScreenCast.Core;

public enum ConnectionState { Idle, Discovering, Punching, Direct, Relay, ConnectedRelay, Failed, Closed }
public enum NatObservation { Unknown, EndpointIndependent, Symmetric }
public enum TransportComponent { Video, Control }

/// <summary>Policy only. Relay means fallback requested, not an allocated or connected transport.</summary>
public sealed class ConnectionStateMachine
{
    public ConnectionState State { get; private set; } = ConnectionState.Idle;
    public int FailedPunchAttempts { get; private set; }
    public int Generation { get; private set; }
    public NatObservation Nat { get; private set; }
    private readonly HashSet<TransportComponent> confirmed = [];
    public void Start()
    {
        Require(ConnectionState.Idle, ConnectionState.Failed, ConnectionState.Closed);
        Generation = checked(Generation + 1); FailedPunchAttempts = 0; confirmed.Clear(); Nat = NatObservation.Unknown;
        State = ConnectionState.Discovering;
    }
    public void DiscoveryCompleted(NatObservation observation)
    {
        Require(ConnectionState.Discovering); Nat = observation;
        State = observation == NatObservation.Symmetric ? ConnectionState.Relay : ConnectionState.Punching;
    }
    public void PunchAttemptFailed(int generation)
    {
        if (generation != Generation) return;
        Require(ConnectionState.Punching); confirmed.Clear();
        if (++FailedPunchAttempts >= 3) State = ConnectionState.Relay;
    }
    public void DirectComponentConfirmed(int generation, TransportComponent component)
    {
        if (generation != Generation) return;
        Require(ConnectionState.Punching); ValidateComponent(component); confirmed.Add(component);
        if (confirmed.Count == 2) State = ConnectionState.Direct;
    }
    public void RelayComponentConfirmed(int generation, TransportComponent component)
    {
        if (generation != Generation) return;
        Require(ConnectionState.Relay); ValidateComponent(component); confirmed.Add(component);
        if (confirmed.Count == 2) State = ConnectionState.ConnectedRelay;
    }
    public void PathLost()
    {
        Require(ConnectionState.Direct, ConnectionState.ConnectedRelay); confirmed.Clear(); State = ConnectionState.Failed;
    }
    public void RelayFailed() { Require(ConnectionState.Relay); confirmed.Clear(); State = ConnectionState.Failed; }
    public void Close() { confirmed.Clear(); State = ConnectionState.Closed; }
    private static void ValidateComponent(TransportComponent c)
    { if (c is not (TransportComponent.Video or TransportComponent.Control)) throw new ArgumentOutOfRangeException(nameof(c)); }
    private void Require(params ConnectionState[] states)
    { if (!states.Contains(State)) throw new InvalidOperationException($"Invalid transition from {State}."); }
}

/// <summary>Mapping changes across destinations on the SAME socket suggest symmetric NAT.
/// An equal mapping does not prove endpoint-independent filtering or traversal success.</summary>
public static class NatClassifier
{
    public static NatObservation Observe(IPEndPoint firstDestination, IPEndPoint firstMapping,
        IPEndPoint secondDestination, IPEndPoint secondMapping)
    {
        if (firstDestination.Equals(secondDestination)) return NatObservation.Unknown;
        return firstMapping.Equals(secondMapping) ? NatObservation.Unknown : NatObservation.Symmetric;
    }
}

/// <summary>Binding transaction using a caller-owned, stable media socket. One receive owner must demultiplex replies.</summary>
public static class StunBindingTransaction
{
    public static async Task<IPEndPoint> DiscoverAsync(IBindingTransport transport, IPEndPoint server,
        CancellationToken cancellationToken = default)
    {
        byte[] id = StunBindingCodec.NewTransactionId(); byte[] request = StunBindingCodec.EncodeRequest(id);
        // RFC5389 defaults: Rc=7 transmissions, initial RTO=500ms, Rm=16 final wait.
        int[] waitsMs = [500, 1000, 2000, 4000, 8000, 16000, 8000];
        foreach (int wait in waitsMs)
        {
            await transport.SendAsync(request, server, cancellationToken).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(wait);
            try
            {
                while (true)
                {
                    var reply = await transport.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                    try { return StunBindingCodec.DecodeSuccess(reply.Payload.Span, id, reply.Source, server); }
                    catch (FormatException) { /* Ignore unrelated or malformed replies without resetting timeout. */ }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
        throw new TimeoutException("STUN Binding transaction exhausted retries.");
    }
}
