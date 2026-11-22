using System.Net;
using ScreenCast.Core.Signaling;

namespace ScreenCast.Server.Signaling;

/// <summary>One datagram the server must send after the dispatch returns.</summary>
public readonly record struct OutboundMessage(byte[] Datagram, IPEndPoint Destination);

/// <summary>Thrown by request handlers to produce a typed error reply.</summary>
public sealed class SignalingRequestException : Exception
{
    public SignalingRequestException(SignalingErrorCode code, string detail) : base(detail)
    {
        Code = code;
    }

    public SignalingErrorCode Code { get; }
}

/// <summary>A registered device. Endpoint is the source address of its last Register.</summary>
public sealed class DeviceEntry
{
    public DeviceEntry(string deviceId, byte roles, IPEndPoint endpoint, long nowMs)
    {
        DeviceId = deviceId;
        Roles = roles;
        Endpoint = endpoint;
        LastActivityMs = nowMs;
        Candidates = Array.Empty<IPEndPoint>();
        Capabilities = Array.Empty<byte>();
    }

    public string DeviceId { get; }

    public byte Roles { get; set; }

    public byte Nat { get; set; }

    public IPEndPoint Endpoint { get; set; }

    /// <summary>Client reported candidates, filtered to usable unicast addresses.</summary>
    public IPEndPoint[] Candidates { get; set; }

    public byte[] Capabilities { get; set; }

    public long LastActivityMs { get; set; }

    public bool HasRole(DeviceRoles role) => (Roles & (byte)role) != 0;
}

/// <summary>A signaling session. Exactly one host and at most one viewer.</summary>
public sealed class SessionEntry
{
    public SessionEntry(SignalingId id, string hostId, string? viewerId, string inviteCode, long nowMs)
    {
        Id = id;
        HostId = hostId;
        ViewerId = viewerId;
        InviteCode = inviteCode;
        CreatedMs = nowMs;
        LastActivityMs = nowMs;
    }

    public SignalingId Id { get; }

    public string HostId { get; }

    public string? ViewerId { get; set; }

    /// <summary>Short join code; empty when the session was created with an explicit viewer.</summary>
    public string InviteCode { get; }

    public SessionState State { get; set; } = SessionState.Created;

    public uint Generation { get; set; }

    public byte Path { get; set; }

    public byte Components { get; set; }

    public long CreatedMs { get; }

    public long LastActivityMs { get; set; }

    public bool HasViewer => !string.IsNullOrEmpty(ViewerId);
}

/// <summary>Bounded reply cache that makes retransmitted requestIds idempotent for a short window.</summary>
public sealed class ResponseCache
{
    private readonly int capacity;
    private readonly long ttlMs;
    private readonly Dictionary<ReplyKey, CachedReply> entries = [];
    private readonly Queue<ReplyKey> order = [];

    public ResponseCache(int capacity, long ttlMs)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (ttlMs < 1) throw new ArgumentOutOfRangeException(nameof(ttlMs));
        this.capacity = capacity;
        this.ttlMs = ttlMs;
    }

    public int Count => entries.Count;

    public bool TryGet(ReplyKey key, long nowMs, out byte[]? reply)
    {
        reply = null;
        if (!entries.TryGetValue(key, out CachedReply cached)) return false;
        if (nowMs - cached.StoredMs > ttlMs)
        {
            entries.Remove(key);
            return false;
        }
        reply = cached.Reply;
        return true;
    }

    public void Store(ReplyKey key, long nowMs, byte[]? reply)
    {
        if (entries.ContainsKey(key)) return;
        while (entries.Count >= capacity && order.Count > 0)
        {
            ReplyKey oldest = order.Dequeue();
            entries.Remove(oldest);
        }
        entries[key] = new CachedReply(reply, nowMs);
        order.Enqueue(key);
    }

    public readonly record struct ReplyKey(IPEndPoint Source, SignalingId RequestId, SignalingMessageType Type);

    private readonly record struct CachedReply(byte[]? Reply, long StoredMs);
}