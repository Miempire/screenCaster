using System.Net;
using System.Security.Cryptography;
using ScreenCast.Core.Signaling;

namespace ScreenCast.Server.Signaling;

/// <summary>
/// All signaling state and message handling. Single threaded by an internal gate: handlers run to
/// completion under the lock and only append outbound datagrams, so a socket error can never leave
/// half updated tables. Payload bytes of PeerKeyOffer/PeerKeyAnswer are forwarded verbatim and are
/// never decoded, logged or stored. All time comes from the caller as a monotonic millisecond value.
/// </summary>
public sealed class SignalingService
{
    private readonly SignalingOptions options;
    private readonly object gate = new();
    private readonly Dictionary<string, DeviceEntry> devices = new(StringComparer.Ordinal);
    private readonly Dictionary<IPEndPoint, string> endpointOwners = [];
    private readonly Dictionary<SignalingId, SessionEntry> sessions = [];
    private readonly Dictionary<string, SignalingId> invites = new(StringComparer.OrdinalIgnoreCase);
    private readonly ResponseCache replies;
    private long duplicateRequests;
    private long removedSessions;
    private long removedDevices;
    private long malformedMessages;

    public SignalingService(SignalingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
        replies = new ResponseCache(options.ReplyCacheCapacity, options.ReplyCacheMs);
    }

    public SignalingOptions Options => options;

    public int DeviceCount
    {
        get
        {
            lock (gate) return devices.Count;
        }
    }

    public int SessionCount
    {
        get
        {
            lock (gate) return sessions.Count;
        }
    }

    public int CachedReplyCount
    {
        get
        {
            lock (gate) return replies.Count;
        }
    }

    public long DuplicateRequests => Interlocked.Read(ref duplicateRequests);

    public long RemovedSessions => Interlocked.Read(ref removedSessions);

    public long RemovedDevices => Interlocked.Read(ref removedDevices);

    public long MalformedMessages => Interlocked.Read(ref malformedMessages);

    /// <summary>Test and diagnostics view of a session state; null once the session is gone.</summary>
    public SessionState? StateOf(SignalingId id)
    {
        lock (gate) return sessions.TryGetValue(id, out SessionEntry? session) ? session.State : null;
    }

    /// <summary>Handles one datagram and appends every response and notification to <paramref name="outbox"/>.</summary>
    public void Handle(byte[] datagram, IPEndPoint source, long nowMs, List<OutboundMessage> outbox)
    {
        ArgumentNullException.ThrowIfNull(datagram);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(outbox);
        if (!SignalingWire.TryParseHeader(datagram, out SignalingHeader header))
        {
            Interlocked.Increment(ref malformedMessages);
            return;
        }
        var key = new ResponseCache.ReplyKey(source, header.RequestId, header.Type);
        lock (gate)
        {
            if (replies.TryGet(key, nowMs, out byte[]? cached))
            {
                Interlocked.Increment(ref duplicateRequests);
                if (cached is not null) outbox.Add(new OutboundMessage(cached, source));
                return;
            }
            byte[]? reply;
            try
            {
                reply = Dispatch(header, datagram, source, nowMs, outbox);
            }
            catch (SignalingRequestException ex)
            {
                reply = BuildError(header.RequestId, ex.Code, ex.Message);
            }
            catch (FormatException ex)
            {
                Interlocked.Increment(ref malformedMessages);
                reply = BuildError(header.RequestId, SignalingErrorCode.Malformed, ex.Message);
            }
            replies.Store(key, nowMs, reply);
            if (reply is not null) outbox.Add(new OutboundMessage(reply, source));
        }
    }

    /// <summary>
    /// Removes expired devices and expired or idle sessions and notifies the surviving peer.
    /// Removal happens under the lock before any notification is queued, so repeated scans are
    /// idempotent: a second scan finds nothing and appends nothing.
    /// </summary>
    public int Scan(long nowMs, List<OutboundMessage> outbox)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        lock (gate)
        {
            int removed = 0;
            foreach (DeviceEntry device in devices.Values.ToArray())
            {
                if (nowMs - device.LastActivityMs <= options.DeviceLeaseMs) continue;
                RemoveDevice(device, outbox);
                removed++;
            }
            foreach (SessionEntry session in sessions.Values.ToArray())
            {
                bool idle = nowMs - session.LastActivityMs > options.SessionIdleMs;
                bool inviteExpired = session.State == SessionState.Created && nowMs - session.CreatedMs > options.InviteTimeoutMs;
                if (!idle && !inviteExpired) continue;
                CloseAndNotify(session, SessionCloseReason.Timeout, string.Empty, outbox);
                removed++;
            }
            return removed;
        }
    }

    private byte[]? Dispatch(SignalingHeader header, byte[] datagram, IPEndPoint source, long nowMs, List<OutboundMessage> outbox)
    {
        var reader = new SignalingReader(datagram, header);
        switch (header.Type)
        {
            case SignalingMessageType.Register:
                return HandleRegister(header.RequestId, reader, source, nowMs, outbox);
            case SignalingMessageType.Heartbeat:
                return HandleHeartbeat(header.RequestId, reader, source, nowMs);
            case SignalingMessageType.CreateSession:
                return HandleCreateSession(header.RequestId, reader, source, nowMs, outbox);
            case SignalingMessageType.JoinSession:
                return HandleJoinSession(header.RequestId, reader, source, nowMs, outbox);
            case SignalingMessageType.AcceptSession:
                return HandleAcceptSession(header.RequestId, reader, source, nowMs, outbox);
            case SignalingMessageType.RejectSession:
                return HandleRejectSession(header.RequestId, reader, source, nowMs, outbox);
            case SignalingMessageType.PunchStart:
                return HandlePunchStart(header.RequestId, reader, source, nowMs, outbox);
            case SignalingMessageType.PathCommit:
                return HandlePathCommit(header.RequestId, reader, source, nowMs, outbox);
            case SignalingMessageType.CloseSession:
                return HandleCloseSession(header.RequestId, reader, source, nowMs, outbox);
            case SignalingMessageType.PeerKeyOffer:
            case SignalingMessageType.PeerKeyAnswer:
                return HandlePeerKey(header, reader, source, nowMs, outbox);
            default:
                throw new SignalingRequestException(SignalingErrorCode.Unsupported, "unknown message type");
        }
    }

    private byte[] HandleRegister(SignalingId requestId, SignalingReader reader, IPEndPoint source, long nowMs, List<OutboundMessage> outbox)
    {
        string deviceId = reader.Text(DeviceRoleRules.MaxDeviceIdBytes);
        byte roles = reader.U8();
        byte nat = reader.U8();
        ReadOnlyMemory<byte> capabilities = reader.Blob(DeviceRoleRules.MaxCapabilitiesBytes);
        List<IPEndPoint> reported = reader.Endpoints(DeviceRoleRules.MaxCandidates);
        reader.RequireEnd();
        ValidateDeviceId(deviceId);
        if (roles is 0 or > DeviceRoleRules.All) throw new SignalingRequestException(SignalingErrorCode.Malformed, "roles must be 1..3");
        if (nat > 2) throw new SignalingRequestException(SignalingErrorCode.Malformed, "nat observation must be 0..2");

        if (endpointOwners.TryGetValue(source, out string? previousDevice) && previousDevice != deviceId &&
            devices.TryGetValue(previousDevice, out DeviceEntry? held))
        {
            RemoveDevice(held, outbox);
        }

        if (!devices.TryGetValue(deviceId, out DeviceEntry? device))
        {
            if (devices.Count >= options.MaxDevices) throw new SignalingRequestException(SignalingErrorCode.LimitExceeded, "device table full");
            device = new DeviceEntry(deviceId, roles, source, nowMs);
            devices[deviceId] = device;
        }
        else
        {
            if (!device.Endpoint.Equals(source) && endpointOwners.TryGetValue(device.Endpoint, out string? oldOwner) && oldOwner == deviceId)
                endpointOwners.Remove(device.Endpoint);
            device.Endpoint = source;
            device.Roles = roles;
        }
        endpointOwners[source] = deviceId;
        device.Nat = nat;
        device.Candidates = reported.Where(IsUsableCandidate).ToArray();
        device.Capabilities = capabilities.ToArray();
        device.LastActivityMs = nowMs;
        return Encode(SignalingMessageType.Registered, requestId, writer =>
        {
            writer.Text(deviceId);
            writer.U8(roles);
            writer.U32((uint)options.HeartbeatIntervalMs);
            writer.U32((uint)options.DeviceLeaseMs);
        });
    }

    private byte[] HandleHeartbeat(SignalingId requestId, SignalingReader reader, IPEndPoint source, long nowMs)
    {
        reader.RequireEnd();
        DeviceEntry device = RequireDevice(source, nowMs);
        device.LastActivityMs = nowMs;
        return Encode(SignalingMessageType.HeartbeatAck, requestId, _ => { });
    }

    private byte[] HandleCreateSession(SignalingId requestId, SignalingReader reader, IPEndPoint source, long nowMs, List<OutboundMessage> outbox)
    {
        string hostIdText = reader.Text(DeviceRoleRules.MaxDeviceIdBytes);
        string viewerIdText = reader.Text(DeviceRoleRules.MaxDeviceIdBytes);
        reader.RequireEnd();
        DeviceEntry sender = RequireDevice(source, nowMs);

        DeviceEntry host;
        if (hostIdText.Length == 0 || string.Equals(hostIdText, sender.DeviceId, StringComparison.Ordinal))
        {
            if (!sender.HasRole(DeviceRoles.Host)) throw new SignalingRequestException(SignalingErrorCode.RoleMismatch, "sender is not a host device");
            host = sender;
        }
        else
        {
            if (!sender.HasRole(DeviceRoles.Viewer)) throw new SignalingRequestException(SignalingErrorCode.RoleMismatch, "sender is not a viewer device");
            host = FindDeviceOrThrow(hostIdText);
            if (!host.HasRole(DeviceRoles.Host)) throw new SignalingRequestException(SignalingErrorCode.NotFound, "device is not registered as a host");
        }

        DeviceEntry? viewer = null;
        if (viewerIdText.Length > 0)
        {
            viewer = FindDeviceOrThrow(viewerIdText);
            if (!viewer.HasRole(DeviceRoles.Viewer)) throw new SignalingRequestException(SignalingErrorCode.NotFound, "device is not registered as a viewer");
            if (ReferenceEquals(viewer, host)) throw new SignalingRequestException(SignalingErrorCode.Malformed, "host and viewer must differ");
        }
        else if (!ReferenceEquals(host, sender))
        {
            viewer = sender;
        }
        if (!ReferenceEquals(host, sender) && (viewer is null || !ReferenceEquals(viewer, sender)))
        {
            throw new SignalingRequestException(SignalingErrorCode.NotParticipant, "creator must be the host or the viewer");
        }

        AssertGlobalCapacity();
        AssertDeviceCapacity(sender.DeviceId);
        if (viewer is not null && !ReferenceEquals(viewer, sender)) AssertDeviceCapacity(viewer.DeviceId);
        AssertHostCapacity(host.DeviceId);

        SignalingId sessionId = SignalingId.NewRandom();
        string inviteCode = viewer is null ? NewInviteCode() : string.Empty;
        var session = new SessionEntry(sessionId, host.DeviceId, viewer?.DeviceId, inviteCode, nowMs);
        sessions[sessionId] = session;
        if (inviteCode.Length > 0) invites[inviteCode] = sessionId;

        if (viewer is not null && !ReferenceEquals(viewer, sender))
        {
            outbox.Add(new OutboundMessage(BuildView(session, SignalingMessageType.SessionInvited, SignalingId.NewRandom(), DeviceRoles.Viewer), viewer.Endpoint));
        }
        else if (viewer is not null)
        {
            outbox.Add(new OutboundMessage(BuildView(session, SignalingMessageType.JoinRequested, SignalingId.NewRandom(), DeviceRoles.Host), host.Endpoint));
        }
        DeviceRoles senderRole = ReferenceEquals(host, sender) ? DeviceRoles.Host : DeviceRoles.Viewer;
        return BuildView(session, SignalingMessageType.SessionCreated, requestId, senderRole);
    }

    private byte[] HandleJoinSession(SignalingId requestId, SignalingReader reader, IPEndPoint source, long nowMs, List<OutboundMessage> outbox)
    {
        string inviteCode = reader.Text(16);
        SignalingId sessionId = reader.Id();
        reader.RequireEnd();
        DeviceEntry sender = RequireDevice(source, nowMs);
        if (!sender.HasRole(DeviceRoles.Viewer)) throw new SignalingRequestException(SignalingErrorCode.RoleMismatch, "sender is not a viewer device");
        if (sessionId.IsZero && inviteCode.Length == 0) throw new SignalingRequestException(SignalingErrorCode.Malformed, "sessionId or inviteCode required");

        SessionEntry? session = null;
        if (!sessionId.IsZero)
        {
            sessions.TryGetValue(sessionId, out session);
        }
        else if (invites.TryGetValue(inviteCode, out SignalingId byCode))
        {
            sessions.TryGetValue(byCode, out session);
        }
        if (session is null)
        {
            throw new SignalingRequestException(SignalingErrorCode.NotFound, inviteCode.Length > 0 ? "invite is invalid or expired" : "session not found");
        }
        if (session.HostId == sender.DeviceId) throw new SignalingRequestException(SignalingErrorCode.RoleMismatch, "host cannot join as a viewer");
        if (session.HasViewer && session.ViewerId != sender.DeviceId)
        {
            throw new SignalingRequestException(session.State == SessionState.Created ? SignalingErrorCode.Busy : SignalingErrorCode.InvalidState, "session already has a viewer");
        }
        session.LastActivityMs = nowMs;
        if (!session.HasViewer)
        {
            AssertDeviceCapacity(sender.DeviceId);
            session.ViewerId = sender.DeviceId;
            if (devices.TryGetValue(session.HostId, out DeviceEntry? host))
            {
                outbox.Add(new OutboundMessage(BuildView(session, SignalingMessageType.JoinRequested, SignalingId.NewRandom(), DeviceRoles.Host), host.Endpoint));
            }
        }
        return BuildView(session, SignalingMessageType.SessionJoined, requestId, DeviceRoles.Viewer);
    }

    private byte[] HandleAcceptSession(SignalingId requestId, SignalingReader reader, IPEndPoint source, long nowMs, List<OutboundMessage> outbox)
    {
        SignalingId sessionId = reader.Id();
        reader.RequireEnd();
        DeviceEntry sender = RequireDevice(source, nowMs);
        SessionEntry session = RequireSession(sessionId);
        if (session.HostId != sender.DeviceId)
        {
            throw new SignalingRequestException(session.ViewerId == sender.DeviceId ? SignalingErrorCode.RoleMismatch : SignalingErrorCode.NotParticipant, "only the host can accept");
        }
        if (session.State != SessionState.Created) throw new SignalingRequestException(SignalingErrorCode.InvalidState, "session is not awaiting approval");
        if (!session.HasViewer) throw new SignalingRequestException(SignalingErrorCode.InvalidState, "no viewer has joined");
        session.State = SessionState.Accepted;
        session.LastActivityMs = nowMs;
        NotifyPeer(session, SignalingMessageType.SessionAccepted, SignalingId.NewRandom(), sender.DeviceId, outbox);
        return BuildView(session, SignalingMessageType.SessionAccepted, requestId, DeviceRoles.Host);
    }

    private byte[] HandleRejectSession(SignalingId requestId, SignalingReader reader, IPEndPoint source, long nowMs, List<OutboundMessage> outbox)
    {
        SignalingId sessionId = reader.Id();
        byte reason = reader.U8();
        reader.RequireEnd();
        DeviceEntry sender = RequireDevice(source, nowMs);
        SessionEntry session = RequireSession(sessionId);
        if (session.HostId != sender.DeviceId)
        {
            throw new SignalingRequestException(session.ViewerId == sender.DeviceId ? SignalingErrorCode.RoleMismatch : SignalingErrorCode.NotParticipant, "only the host can reject");
        }
        if (session.State != SessionState.Created) throw new SignalingRequestException(SignalingErrorCode.InvalidState, "session is not awaiting approval");
        session.State = SessionState.Closed;
        session.LastActivityMs = nowMs;
        byte[] reply = BuildView(session, SignalingMessageType.SessionRejected, requestId, DeviceRoles.Host, reason);
        NotifyPeer(session, SignalingMessageType.SessionRejected, SignalingId.NewRandom(), sender.DeviceId, outbox, reason);
        RemoveSession(session);
        return reply;
    }

    private byte[] HandlePunchStart(SignalingId requestId, SignalingReader reader, IPEndPoint source, long nowMs, List<OutboundMessage> outbox)
    {
        SignalingId sessionId = reader.Id();
        uint generation = reader.U32();
        reader.RequireEnd();
        DeviceEntry sender = RequireDevice(source, nowMs);
        SessionEntry session = RequireSession(sessionId);
        DeviceRoles role = RequireParticipant(session, sender);
        if (session.State != SessionState.Accepted) throw new SignalingRequestException(SignalingErrorCode.InvalidState, "session is not accepted");
        if (generation == 0 || generation <= session.Generation || generation != unchecked(session.Generation + 1))
        {
            throw new SignalingRequestException(SignalingErrorCode.GenerationMismatch, "generation must be the current generation plus one");
        }
        session.Generation = generation;
        session.State = SessionState.Negotiating;
        session.LastActivityMs = nowMs;
        NotifyPeer(session, SignalingMessageType.PunchStartNotify, SignalingId.NewRandom(), sender.DeviceId, outbox);
        return BuildView(session, SignalingMessageType.PunchStartNotify, requestId, role);
    }

    private byte[] HandlePathCommit(SignalingId requestId, SignalingReader reader, IPEndPoint source, long nowMs, List<OutboundMessage> outbox)
    {
        SignalingId sessionId = reader.Id();
        uint generation = reader.U32();
        byte path = reader.U8();
        byte components = reader.U8();
        reader.RequireEnd();
        DeviceEntry sender = RequireDevice(source, nowMs);
        SessionEntry session = RequireSession(sessionId);
        DeviceRoles role = RequireParticipant(session, sender);
        if (session.State is not (SessionState.Negotiating or SessionState.Active))
        {
            throw new SignalingRequestException(SignalingErrorCode.InvalidState, "session is not negotiating");
        }
        if (generation != session.Generation) throw new SignalingRequestException(SignalingErrorCode.GenerationMismatch, "generation does not match");
        if (path is < 1 or > 2) throw new SignalingRequestException(SignalingErrorCode.Malformed, "path must be 1 (direct) or 2 (relay)");
        if (components is < 1 or > 3) throw new SignalingRequestException(SignalingErrorCode.Malformed, "components mask must be 1..3");
        session.Path = path;
        session.Components = components;
        session.LastActivityMs = nowMs;
        if (session.State != SessionState.Active)
        {
            session.State = SessionState.Active;
            NotifyPeer(session, SignalingMessageType.SessionActive, SignalingId.NewRandom(), sender.DeviceId, outbox);
        }
        return BuildView(session, SignalingMessageType.SessionActive, requestId, role);
    }

    private byte[] HandleCloseSession(SignalingId requestId, SignalingReader reader, IPEndPoint source, long nowMs, List<OutboundMessage> outbox)
    {
        SignalingId sessionId = reader.Id();
        byte reason = reader.U8();
        reader.RequireEnd();
        DeviceEntry sender = RequireDevice(source, nowMs);
        SessionEntry session = RequireSession(sessionId);
        DeviceRoles role = RequireParticipant(session, sender);
        session.State = SessionState.Closed;
        session.LastActivityMs = nowMs;
        byte[] reply = BuildView(session, SignalingMessageType.SessionClosed, requestId, role, reason);
        NotifyPeer(session, SignalingMessageType.SessionClosed, SignalingId.NewRandom(), sender.DeviceId, outbox, reason);
        RemoveSession(session);
        return reply;
    }

    /// <summary>
    /// Forwards the opaque PeerKeyOffer/PeerKeyAnswer body to the other participant. The payload is
    /// copied byte for byte and never inspected: the server holds no media key material.
    /// </summary>
    private byte[] HandlePeerKey(SignalingHeader header, SignalingReader reader, IPEndPoint source, long nowMs, List<OutboundMessage> outbox)
    {
        SignalingId sessionId = reader.Id();
        SignalingId originRequestId = reader.Id();
        ReadOnlyMemory<byte> payload = reader.Blob(SignalingWire.MaxBlobBytes);
        reader.RequireEnd();
        DeviceEntry sender = RequireDevice(source, nowMs);
        SessionEntry session = RequireSession(sessionId);
        RequireParticipant(session, sender);
        if (session.State is SessionState.Created or SessionState.Closed)
        {
            throw new SignalingRequestException(SignalingErrorCode.InvalidState, "peer key exchange requires an accepted session");
        }
        session.LastActivityMs = nowMs;
        byte[] forwarded = Encode(header.Type, SignalingId.NewRandom(), writer =>
        {
            writer.Id(sessionId);
            writer.Id(originRequestId);
            writer.Blob(payload.Span);
        });
        if (session.HostId != sender.DeviceId && devices.TryGetValue(session.HostId, out DeviceEntry? host))
            outbox.Add(new OutboundMessage(forwarded, host.Endpoint));
        if (session.HasViewer && session.ViewerId != sender.DeviceId && devices.TryGetValue(session.ViewerId!, out DeviceEntry? viewer))
            outbox.Add(new OutboundMessage(forwarded, viewer.Endpoint));
        return Encode(SignalingMessageType.Ack, header.RequestId, _ => { });
    }

    private void CloseAndNotify(SessionEntry session, SessionCloseReason reason, string excludeDeviceId, List<OutboundMessage> outbox)
    {
        session.State = SessionState.Closed;
        NotifyPeer(session, SignalingMessageType.SessionClosed, SignalingId.NewRandom(), excludeDeviceId, outbox, (byte)reason);
        RemoveSession(session);
    }

    private void RemoveDevice(DeviceEntry device, List<OutboundMessage> outbox)
    {
        devices.Remove(device.DeviceId);
        if (endpointOwners.TryGetValue(device.Endpoint, out string? owner) && owner == device.DeviceId) endpointOwners.Remove(device.Endpoint);
        Interlocked.Increment(ref removedDevices);
        foreach (SessionEntry session in sessions.Values.ToArray())
        {
            if (session.HostId != device.DeviceId && session.ViewerId != device.DeviceId) continue;
            CloseAndNotify(session, SessionCloseReason.PeerOffline, device.DeviceId, outbox);
        }
    }

    private void RemoveSession(SessionEntry session)
    {
        sessions.Remove(session.Id);
        if (session.InviteCode.Length > 0) invites.Remove(session.InviteCode);
        Interlocked.Increment(ref removedSessions);
    }

    private void NotifyPeer(SessionEntry session, SignalingMessageType type, SignalingId requestId, string excludeDeviceId, List<OutboundMessage> outbox, byte? reason = null)
    {
        if (session.HostId != excludeDeviceId && devices.TryGetValue(session.HostId, out DeviceEntry? host))
            outbox.Add(new OutboundMessage(BuildView(session, type, requestId, DeviceRoles.Host, reason), host.Endpoint));
        if (session.HasViewer && session.ViewerId != excludeDeviceId && devices.TryGetValue(session.ViewerId!, out DeviceEntry? viewer))
            outbox.Add(new OutboundMessage(BuildView(session, type, requestId, DeviceRoles.Viewer, reason), viewer.Endpoint));
    }

    private byte[] BuildView(SessionEntry session, SignalingMessageType type, SignalingId requestId, DeviceRoles role, byte? reason = null)
    {
        devices.TryGetValue(session.HostId, out DeviceEntry? host);
        DeviceEntry? viewer = null;
        if (session.HasViewer) devices.TryGetValue(session.ViewerId!, out viewer);
        var writer = new SignalingWriter();
        writer.Id(session.Id);
        writer.U8((byte)session.State);
        writer.U32(session.Generation);
        writer.U8((byte)role);
        writer.Text(session.InviteCode);
        writer.Text(session.HostId);
        writer.Text(session.ViewerId ?? string.Empty);
        writer.Endpoint(host?.Endpoint);
        writer.Endpoint(viewer?.Endpoint);
        writer.U8(host?.Nat ?? 0);
        writer.U8(viewer?.Nat ?? 0);
        writer.Endpoints(host?.Candidates ?? Array.Empty<IPEndPoint>());
        writer.Endpoints(viewer?.Candidates ?? Array.Empty<IPEndPoint>());
        writer.U32((uint)options.PunchStartDelayMs);
        if (reason.HasValue) writer.U8(reason.Value);
        return writer.ToMessage(type, requestId);
    }

    private DeviceEntry RequireDevice(IPEndPoint source, long nowMs)
    {
        if (endpointOwners.TryGetValue(source, out string? deviceId) && devices.TryGetValue(deviceId, out DeviceEntry? device))
        {
            device.LastActivityMs = nowMs;
            return device;
        }
        throw new SignalingRequestException(SignalingErrorCode.Unregistered, "source endpoint is not registered");
    }

    private DeviceEntry FindDeviceOrThrow(string deviceId) =>
        devices.TryGetValue(deviceId, out DeviceEntry? device)
            ? device
            : throw new SignalingRequestException(SignalingErrorCode.NotFound, "device is not registered");

    private SessionEntry RequireSession(SignalingId sessionId) =>
        sessions.TryGetValue(sessionId, out SessionEntry? session)
            ? session
            : throw new SignalingRequestException(SignalingErrorCode.NotFound, "session not found");

    private static DeviceRoles RequireParticipant(SessionEntry session, DeviceEntry device)
    {
        if (session.HostId == device.DeviceId) return DeviceRoles.Host;
        if (session.ViewerId == device.DeviceId) return DeviceRoles.Viewer;
        throw new SignalingRequestException(SignalingErrorCode.NotParticipant, "device is not a session participant");
    }

    private void AssertGlobalCapacity()
    {
        if (sessions.Count >= options.MaxSessions) throw new SignalingRequestException(SignalingErrorCode.LimitExceeded, "server session limit reached");
    }

    private void AssertDeviceCapacity(string deviceId)
    {
        int count = 0;
        foreach (SessionEntry session in sessions.Values)
        {
            if (session.HostId == deviceId || session.ViewerId == deviceId) count++;
        }
        if (count >= options.MaxSessionsPerDevice) throw new SignalingRequestException(SignalingErrorCode.LimitExceeded, "device session limit reached");
    }

    private void AssertHostCapacity(string hostId)
    {
        int count = 0;
        foreach (SessionEntry session in sessions.Values)
        {
            if (session.HostId == hostId) count++;
        }
        if (count >= options.MaxSessionsPerHost) throw new SignalingRequestException(SignalingErrorCode.LimitExceeded, "host session limit reached");
    }

    private static void ValidateDeviceId(string deviceId)
    {
        if (deviceId.Length is 0 or > DeviceRoleRules.MaxDeviceIdBytes) throw new SignalingRequestException(SignalingErrorCode.Malformed, "deviceId must be 1..64 bytes");
        foreach (char character in deviceId)
        {
            if (character is < '!' or > '~') throw new SignalingRequestException(SignalingErrorCode.Malformed, "deviceId must be printable ASCII without spaces");
        }
    }

    private static bool IsUsableCandidate(IPEndPoint endpoint)
    {
        if (endpoint.Port == 0) return false;
        IPAddress address = endpoint.Address;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.IsIPv6Multicast) return false;
        return !(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && address.GetAddressBytes() is [255, 255, 255, 255]);
    }

    private string NewInviteCode()
    {
        Span<char> code = stackalloc char[9];
        for (int attempt = 0; attempt < 100; attempt++)
        {
            code[0] = (char)('1' + RandomNumberGenerator.GetInt32(0, 9));
            for (int i = 1; i < code.Length; i++)
            {
                code[i] = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
            }

            string value = new(code);
            if (!invites.ContainsKey(value)) return value;
        }

        throw new SignalingRequestException(SignalingErrorCode.LimitExceeded, "invite code space exhausted");
    }

    private static byte[] Encode(SignalingMessageType type, SignalingId requestId, Action<SignalingWriter> body)
    {
        var writer = new SignalingWriter();
        body(writer);
        return writer.ToMessage(type, requestId);
    }

    private static byte[] BuildError(SignalingId requestId, SignalingErrorCode code, string detail)
    {
        string text = detail.Length > 100 ? detail[..100] : detail;
        return Encode(SignalingMessageType.Error, requestId, writer =>
        {
            writer.U16((ushort)code);
            writer.Text(text, 128);
        });
    }
}