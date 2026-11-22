using System.Net;

namespace ScreenCast.Core.Signaling;

public sealed record SessionView(
    SignalingMessageType Type,
    SignalingId SessionId,
    SessionState State,
    uint Generation,
    DeviceRoles Role,
    string InviteCode,
    string HostId,
    string ViewerId,
    IPEndPoint? HostEndpoint,
    IPEndPoint? ViewerEndpoint,
    byte HostNat,
    byte ViewerNat,
    IReadOnlyList<IPEndPoint> HostCandidates,
    IReadOnlyList<IPEndPoint> ViewerCandidates,
    uint PunchStartDelayMs,
    SessionCloseReason? CloseReason)
{
    public IPEndPoint? PeerEndpoint => Role == DeviceRoles.Host ? ViewerEndpoint : HostEndpoint;

    public IPEndPoint? OwnEndpoint => Role == DeviceRoles.Host ? HostEndpoint : ViewerEndpoint;

    public IReadOnlyList<IPEndPoint> PeerCandidates => Role == DeviceRoles.Host ? ViewerCandidates : HostCandidates;

    public static SessionView Decode(byte[] datagram, SignalingHeader header)
    {
        var reader = new SignalingReader(datagram, header);
        SignalingId sessionId = reader.Id();
        SessionState state = (SessionState)reader.U8();
        uint generation = reader.U32();
        DeviceRoles role = (DeviceRoles)reader.U8();
        string inviteCode = reader.Text(16);
        string hostId = reader.Text(DeviceRoleRules.MaxDeviceIdBytes);
        string viewerId = reader.Text(DeviceRoleRules.MaxDeviceIdBytes);
        IPEndPoint? hostEndpoint = reader.Endpoint();
        IPEndPoint? viewerEndpoint = reader.Endpoint();
        byte hostNat = reader.U8();
        byte viewerNat = reader.U8();
        IReadOnlyList<IPEndPoint> hostCandidates = reader.Endpoints(DeviceRoleRules.MaxCandidates);
        IReadOnlyList<IPEndPoint> viewerCandidates = reader.Endpoints(DeviceRoleRules.MaxCandidates);
        uint punchStartDelayMs = reader.U32();
        SessionCloseReason? closeReason = null;
        if (reader.Remaining > 0) closeReason = (SessionCloseReason)reader.U8();
        reader.RequireEnd();
        return new SessionView(header.Type, sessionId, state, generation, role, inviteCode, hostId, viewerId,
            hostEndpoint, viewerEndpoint, hostNat, viewerNat, hostCandidates, viewerCandidates, punchStartDelayMs, closeReason);
    }
}

public sealed record PeerKeyMessage(SignalingMessageType Type, SignalingId SessionId, SignalingId OriginRequestId, byte[] Payload)
{
    public static PeerKeyMessage Decode(byte[] datagram, SignalingHeader header)
    {
        var reader = new SignalingReader(datagram, header);
        SignalingId sessionId = reader.Id();
        SignalingId originRequestId = reader.Id();
        ReadOnlyMemory<byte> payload = reader.Blob(SignalingWire.MaxBlobBytes);
        reader.RequireEnd();
        return new PeerKeyMessage(header.Type, sessionId, originRequestId, payload.ToArray());
    }
}

public sealed class SignalingException : Exception
{
    public SignalingException(SignalingErrorCode code, string detail) : base(detail) => Code = code;

    public SignalingErrorCode Code { get; }
}
