using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ScreenCast.Core.Signaling;

public sealed class SignalingClient : IAsyncDisposable
{
    private readonly string host;
    private readonly int port;
    private readonly ConcurrentDictionary<SignalingId, TaskCompletionSource<byte[]>> pending = new();
    private Socket? socket;
    private CancellationTokenSource? lifetime;
    private Task? receiveTask;
    private Task? heartbeatTask;
    private int heartbeatIntervalMs = 15_000;
    private int deviceLeaseMs = 45_000;
    private bool disposed;

    public SignalingClient(string host, int port, string deviceId, byte roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (deviceId.Length > DeviceRoleRules.MaxDeviceIdBytes) throw new ArgumentException("deviceId is too long.", nameof(deviceId));
        if (roles is 0 or > DeviceRoleRules.All) throw new ArgumentOutOfRangeException(nameof(roles));
        this.host = host;
        this.port = port;
        DeviceId = deviceId;
        Roles = roles;
    }

    public string DeviceId { get; }

    public byte Roles { get; }

    public int HeartbeatIntervalMs => heartbeatIntervalMs;

    public int DeviceLeaseMs => deviceLeaseMs;

    public bool IsRegistered { get; private set; }

    public IPEndPoint? LocalEndPoint => socket?.LocalEndPoint as IPEndPoint;

    public event Action? Registered;

    public event Action<SessionView>? JoinRequested;

    public event Action<SessionView>? SessionAcceptedNotification;

    public event Action<SessionView>? SessionClosed;

    public event Action<PeerKeyMessage>? PeerKeyReceived;

    public event Action<SignalingException>? Failed;

    public event Action<Exception>? Faulted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (socket is not null) return;

        var created = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        created.Bind(new IPEndPoint(IPAddress.Any, 0));
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        IPAddress address = Array.Find(addresses, value => value.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        await created.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);

        socket = created;
        lifetime = new CancellationTokenSource();
        receiveTask = Task.Run(() => ReceiveLoopAsync(lifetime.Token));
        heartbeatTask = Task.Run(() => HeartbeatLoopAsync(lifetime.Token));

        byte[] reply = await RequestAsync(SignalingMessageType.Register, writer =>
        {
            writer.Text(DeviceId);
            writer.U8(Roles);
            writer.U8(0);
            writer.Blob(ReadOnlySpan<byte>.Empty);
            writer.Endpoints(Array.Empty<IPEndPoint>());
        }, cancellationToken).ConfigureAwait(false);

        if (!SignalingWire.TryParseHeader(reply, out SignalingHeader header) || header.Type != SignalingMessageType.Registered)
        {
            throw new SignalingException(SignalingErrorCode.Malformed, "registration reply is invalid");
        }

        var reader = new SignalingReader(reply, header);
        reader.Text(DeviceRoleRules.MaxDeviceIdBytes);
        reader.U8();
        heartbeatIntervalMs = checked((int)reader.U32());
        deviceLeaseMs = checked((int)reader.U32());
        reader.RequireEnd();
        IsRegistered = true;
        Registered?.Invoke();
    }

    public Task<SessionView> CreateSessionAsync(string viewerId = "", CancellationToken cancellationToken = default)
    {
        return RequestSessionAsync(SignalingMessageType.CreateSession, writer =>
        {
            writer.Text(DeviceId);
            writer.Text(viewerId ?? string.Empty);
        }, cancellationToken);
    }

    public Task<SessionView> JoinSessionAsync(string inviteCode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteCode);
        return RequestSessionAsync(SignalingMessageType.JoinSession, writer =>
        {
            writer.Text(inviteCode.Trim());
            writer.Id(SignalingId.Zero);
        }, cancellationToken);
    }

    public Task<SessionView> AcceptSessionAsync(SignalingId sessionId, CancellationToken cancellationToken = default)
    {
        return RequestSessionAsync(SignalingMessageType.AcceptSession, writer => writer.Id(sessionId), cancellationToken);
    }

    public Task<SessionView> CloseSessionAsync(SignalingId sessionId, CancellationToken cancellationToken = default)
    {
        return RequestSessionAsync(SignalingMessageType.CloseSession, writer =>
        {
            writer.Id(sessionId);
            writer.U8((byte)SessionCloseReason.UserClosed);
        }, cancellationToken);
    }

    public Task SendPeerKeyAsync(SignalingMessageType type, SignalingId sessionId, ReadOnlySpan<byte> payload, CancellationToken cancellationToken = default)
    {
        if (type is not (SignalingMessageType.PeerKeyOffer or SignalingMessageType.PeerKeyAnswer))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }
        if (payload.Length > SignalingWire.MaxBlobBytes) throw new ArgumentOutOfRangeException(nameof(payload));
        return SendPeerKeyCoreAsync(type, sessionId, payload.ToArray(), cancellationToken);
    }

    private async Task SendPeerKeyCoreAsync(SignalingMessageType type, SignalingId sessionId, byte[] blob, CancellationToken cancellationToken)
    {
        byte[] reply = await RequestAsync(type, writer =>
        {
            writer.Id(sessionId);
            writer.Id(SignalingId.Zero);
            writer.Blob(blob);
        }, cancellationToken).ConfigureAwait(false);

        if (!SignalingWire.TryParseHeader(reply, out SignalingHeader header) || header.Type != SignalingMessageType.Ack)
        {
            throw new SignalingException(SignalingErrorCode.Malformed, "peer key reply is invalid");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime?.Cancel();
        socket?.Dispose();
        if (receiveTask is not null) await IgnoreAsync(receiveTask).ConfigureAwait(false);
        if (heartbeatTask is not null) await IgnoreAsync(heartbeatTask).ConfigureAwait(false);
        lifetime?.Dispose();
        foreach (TaskCompletionSource<byte[]> pendingReply in pending.Values)
        {
            pendingReply.TrySetException(new ObjectDisposedException(nameof(SignalingClient)));
        }
        pending.Clear();
    }

    private async Task<SessionView> RequestSessionAsync(SignalingMessageType type, Action<SignalingWriter> write, CancellationToken cancellationToken)
    {
        byte[] reply = await RequestAsync(type, write, cancellationToken).ConfigureAwait(false);
        if (!SignalingWire.TryParseHeader(reply, out SignalingHeader header))
        {
            throw new SignalingException(SignalingErrorCode.Malformed, "session reply is invalid");
        }
        return SessionView.Decode(reply, header);
    }

    private async Task<byte[]> RequestAsync(SignalingMessageType type, Action<SignalingWriter> write, CancellationToken cancellationToken)
    {
        Socket active = socket ?? throw new InvalidOperationException("StartAsync must be called first.");
        var requestId = SignalingId.NewRandom();
        var writer = new SignalingWriter();
        write(writer);
        byte[] message = writer.ToMessage(type, requestId);

        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[requestId] = completion;
        try
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                await active.SendAsync(message, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                Task finished = await Task.WhenAny(completion.Task, Task.Delay(500, cancellationToken)).ConfigureAwait(false);
                if (finished == completion.Task) return await completion.Task.ConfigureAwait(false);
            }

            throw new TimeoutException($"No reply to {type} from {host}:{port}.");
        }
        finally
        {
            pending.TryRemove(requestId, out _);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        Socket active = socket!;
        byte[] buffer = new byte[SignalingWire.MaxMessageBytes];
        while (!cancellationToken.IsCancellationRequested)
        {
            int received;
            try
            {
                received = await active.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                RaiseFault(ex);
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            byte[] datagram = buffer.AsSpan(0, received).ToArray();
            try
            {
                Dispatch(datagram);
            }
            catch (Exception ex)
            {
                RaiseFault(ex);
            }
        }
    }

    private void Dispatch(byte[] datagram)
    {
        if (!SignalingWire.TryParseHeader(datagram, out SignalingHeader header)) return;

        if (header.Type == SignalingMessageType.Error)
        {
            var reader = new SignalingReader(datagram, header);
            var code = (SignalingErrorCode)reader.U16();
            string detail = reader.Text(100);
            reader.RequireEnd();
            var error = new SignalingException(code, detail);
            if (pending.TryRemove(header.RequestId, out TaskCompletionSource<byte[]>? failed))
            {
                failed.TrySetException(error);
            }
            else
            {
                Failed?.Invoke(error);
            }
            return;
        }

        if (pending.TryRemove(header.RequestId, out TaskCompletionSource<byte[]>? completion))
        {
            completion.TrySetResult(datagram);
            return;
        }

        switch (header.Type)
        {
            case SignalingMessageType.JoinRequested:
                JoinRequested?.Invoke(SessionView.Decode(datagram, header));
                break;
            case SignalingMessageType.SessionAccepted:
                SessionAcceptedNotification?.Invoke(SessionView.Decode(datagram, header));
                break;
            case SignalingMessageType.SessionClosed:
                SessionClosed?.Invoke(SessionView.Decode(datagram, header));
                break;
            case SignalingMessageType.PeerKeyOffer:
            case SignalingMessageType.PeerKeyAnswer:
                PeerKeyReceived?.Invoke(PeerKeyMessage.Decode(datagram, header));
                break;
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(heartbeatIntervalMs, cancellationToken).ConfigureAwait(false);
                await RequestAsync(SignalingMessageType.Heartbeat, _ => { }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (TimeoutException)
            {
            }
            catch (SignalingException ex)
            {
                if (ex.Code == SignalingErrorCode.Unregistered)
                {
                    try
                    {
                        await RequestAsync(SignalingMessageType.Register, writer =>
                        {
                            writer.Text(DeviceId);
                            writer.U8(Roles);
                            writer.U8(0);
                            writer.Blob(ReadOnlySpan<byte>.Empty);
                            writer.Endpoints(Array.Empty<IPEndPoint>());
                        }, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    catch (Exception)
                    {
                    }
                }

                RaiseFault(ex);
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private void RaiseFault(Exception exception) => Faulted?.Invoke(exception);

    private static async Task IgnoreAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
