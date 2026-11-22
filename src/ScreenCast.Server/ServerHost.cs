using System.Net;
using System.Net.Sockets;
using ScreenCast.Core;
using ScreenCast.Core.Signaling;
using ScreenCast.Server.Signaling;

namespace ScreenCast.Server;

/// <summary>Wires the STUN socket and the signaling socket into one process lifetime.</summary>
public static class ServerHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        ServerOptions options;
        try
        {
            options = ServerOptions.Parse(args);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Use --help for usage.");
            return 2;
        }
        if (options.HelpRequested)
        {
            Console.WriteLine(ServerOptions.Usage);
            return 0;
        }
        if (options.SelfTestRequested) return await SelfTestAsync().ConfigureAwait(false);
        options.Validate();

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, press) =>
        {
            press.Cancel = true;
            cancellation.Cancel();
        };

        StunService? stun = null;
        SignalingServer? signaling = null;
        try
        {
            var clock = SystemMonotonicClock.Instance;
            if (options.StunEnabled)
            {
                stun = new StunService(options.BindAddress, options.StunPort, clock,
                    new RateLimiter(options.Signaling.GlobalRequestsPerSecond, options.Signaling.PerAddressRequestsPerSecond, clock));
                stun.Start(cancellation.Token);
            }
            if (options.SignalingEnabled)
            {
                signaling = new SignalingServer(options.Signaling, clock);
                signaling.Start(cancellation.Token);
            }
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"bind failed: {ex.SocketErrorCode}");
            if (signaling is not null) await signaling.DisposeAsync().ConfigureAwait(false);
            if (stun is not null) await stun.DisposeAsync().ConfigureAwait(false);
            return 3;
        }

        if (stun is not null)
        {
            Console.WriteLine($"ScreenCast Binding listening on {stun.LocalEndPoint} (RFC5389 Binding only; TURN not implemented)");
        }
        if (signaling is not null)
        {
            SignalingOptions limits = options.Signaling;
            Console.WriteLine($"Signaling listening on {signaling.LocalEndPoint} (SCSG v1, max {SignalingWire.MaxMessageBytes} B per message)");
            Console.WriteLine($"limits: devices<={limits.MaxDevices} sessions<={limits.MaxSessions} perDevice<={limits.MaxSessionsPerDevice} " +
                $"perHost<={limits.MaxSessionsPerHost} global={limits.GlobalRequestsPerSecond}/s perIp={limits.PerAddressRequestsPerSecond}/s");
            Console.WriteLine($"leases: device={limits.DeviceLeaseMs}ms session={limits.SessionIdleMs}ms invite={limits.InviteTimeoutMs}ms scan={limits.ScanIntervalMs}ms");
        }
        Console.WriteLine("Ctrl+C stops both sockets.");

        try
        {
            await Task.Delay(Timeout.Infinite, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
        Console.WriteLine("shutting down");
        if (signaling is not null) await signaling.DisposeAsync().ConfigureAwait(false);
        if (stun is not null) await stun.DisposeAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Socket level self check: real UDP server, real clients, the same code paths used in production.
    /// Exists so a deployment host can verify the build without the test project.
    /// </summary>
    public static async Task<int> SelfTestAsync()
    {
        int passed = 0;
        void Pass(string name)
        {
            passed++;
            Console.WriteLine($"PASS selftest.{name}");
        }
        async Task RunAsync()
        {
            var signalingOptions = new SignalingOptions { BindAddress = IPAddress.Loopback, Port = 0 };
            var server = new SignalingServer(signalingOptions);
            server.Start();
            try
            {
                using var host = new ProbeClient(server.LocalEndPoint);
                using var viewer = new ProbeClient(server.LocalEndPoint);

                host.Send(SignalingMessageType.Register, SignalingId.NewRandom(), w =>
                {
                    w.Text("selftest-host");
                    w.U8((byte)DeviceRoles.Host);
                    w.U8(0);
                    w.Blob([]);
                    w.Endpoints([host.LocalEndPoint]);
                });
                var registered = new ProbeReply(host.Expect(SignalingMessageType.Registered));
                Check(registered.Text() == "selftest-host", "registered device id");
                Pass("register");

                viewer.Send(SignalingMessageType.Register, SignalingId.NewRandom(), w =>
                {
                    w.Text("selftest-viewer");
                    w.U8((byte)DeviceRoles.Viewer);
                    w.U8(0);
                    w.Blob([]);
                    w.Endpoints([viewer.LocalEndPoint]);
                });
                viewer.Expect(SignalingMessageType.Registered);
                Pass("register-second-device");

                host.Send(SignalingMessageType.CreateSession, SignalingId.NewRandom(), w =>
                {
                    w.Text(string.Empty);
                    w.Text(string.Empty);
                });
                var created = new ProbeReply(host.Expect(SignalingMessageType.SessionCreated));
                SignalingId sessionId = created.Id();
                Check(created.U8() == (byte)SessionState.Created, "created state");
                Check(created.U32() == 0, "initial generation");
                Check(created.U8() == (byte)DeviceRoles.Host, "creator role");
                string inviteCode = created.Text();
                Check(created.Text() == "selftest-host", "host id in the view");
                Check(created.Text() == string.Empty, "no viewer before anyone joins");
                Check(!sessionId.IsZero, "session id is 16 random bytes");
                Check(inviteCode.Length == 9 && inviteCode.All(char.IsAsciiDigit), "invite code is nine digits");
                IPEndPoint? createdHostEndpoint = created.Endpoint();
                Check(createdHostEndpoint is not null && createdHostEndpoint.Equals(host.LocalEndPoint), "host endpoint from the register source");
                Pass("create-session");

                viewer.Send(SignalingMessageType.JoinSession, SignalingId.NewRandom(), w =>
                {
                    w.Text(inviteCode);
                    w.Id(SignalingId.Zero);
                });
                var joined = new ProbeReply(viewer.Expect(SignalingMessageType.SessionJoined));
                Check(joined.Id() == sessionId, "joined session id");
                joined.U8();
                joined.U32();
                Check(joined.U8() == (byte)DeviceRoles.Viewer, "viewer role");
                joined.Text();
                Check(joined.Text() == "selftest-host", "host id exchanged");
                Check(joined.Text() == "selftest-viewer", "viewer id exchanged");
                IPEndPoint? hostEndpoint = joined.Endpoint();
                IPEndPoint? viewerEndpoint = joined.Endpoint();
                Check(hostEndpoint is not null && hostEndpoint.Equals(host.LocalEndPoint), "host public endpoint returned");
                Check(viewerEndpoint is not null && viewerEndpoint.Equals(viewer.LocalEndPoint), "viewer public endpoint returned");
                var joinRequest = new ProbeReply(host.Expect(SignalingMessageType.JoinRequested));
                Check(joinRequest.Id() == sessionId, "host notified of the join");
                Pass("join-session");

                host.Send(SignalingMessageType.AcceptSession, SignalingId.NewRandom(), w => w.Id(sessionId));
                host.Expect(SignalingMessageType.SessionAccepted);
                viewer.Expect(SignalingMessageType.SessionAccepted);
                Pass("accept-session");

                viewer.Send(SignalingMessageType.PunchStart, SignalingId.NewRandom(), w =>
                {
                    w.Id(sessionId);
                    w.U32(1);
                });
                var punch = new ProbeReply(viewer.Expect(SignalingMessageType.PunchStartNotify));
                Check(punch.Id() == sessionId, "punch notify session");
                punch.U8();
                Check(punch.U32() == 1, "punch notify generation");
                var peerPunch = new ProbeReply(host.Expect(SignalingMessageType.PunchStartNotify));
                Check(peerPunch.Id() == sessionId, "host also receives the punch command");
                Pass("punch-start");

                byte[] keyPayload = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
                SignalingId origin = SignalingId.NewRandom();
                host.Send(SignalingMessageType.PeerKeyOffer, SignalingId.NewRandom(), w =>
                {
                    w.Id(sessionId);
                    w.Id(origin);
                    w.Blob(keyPayload);
                });
                host.Expect(SignalingMessageType.Ack);
                var forwarded = new ProbeReply(viewer.Expect(SignalingMessageType.PeerKeyOffer));
                Check(forwarded.Id() == sessionId, "forwarded session");
                Check(forwarded.Id() == origin, "origin request id preserved");
                Check(forwarded.Blob().SequenceEqual(keyPayload), "opaque payload forwarded byte for byte");
                Pass("peer-key-forward");

                host.Send(SignalingMessageType.PathCommit, SignalingId.NewRandom(), w =>
                {
                    w.Id(sessionId);
                    w.U32(1);
                    w.U8(1);
                    w.U8(3);
                });
                var active = new ProbeReply(host.Expect(SignalingMessageType.SessionActive));
                Check(active.Id() == sessionId, "active session");
                Check(server.Service.StateOf(sessionId) == SessionState.Active, "server session state is active");
                viewer.Expect(SignalingMessageType.SessionActive);
                Pass("path-commit");

                viewer.Send(SignalingMessageType.CloseSession, SignalingId.NewRandom(), w =>
                {
                    w.Id(sessionId);
                    w.U8((byte)SessionCloseReason.UserClosed);
                });
                viewer.Expect(SignalingMessageType.SessionClosed);
                host.Expect(SignalingMessageType.SessionClosed);
                Check(server.Service.SessionCount == 0, "session removed after close");
                Pass("close-session");

                using (var stranger = new ProbeClient(server.LocalEndPoint))
                {
                    stranger.Send(SignalingMessageType.CreateSession, SignalingId.NewRandom(), w =>
                    {
                        w.Text(string.Empty);
                        w.Text(string.Empty);
                    });
                    var error = new ProbeReply(stranger.Expect(SignalingMessageType.Error));
                    Check(error.U16() == (ushort)SignalingErrorCode.Unregistered, "unregistered device rejected");
                }
                Pass("unregistered-rejected");
            }
            finally
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }
        try
        {
            await RunAsync().ConfigureAwait(false);
            var stun = new StunService(IPAddress.Loopback, 0);
            stun.Start();
            try
            {
                using var stunClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                byte[] transaction = StunBindingCodec.NewTransactionId();
                stunClient.Client.ReceiveTimeout = 2000;
                stunClient.Send(StunBindingCodec.EncodeRequest(transaction), stun.LocalEndPoint);
                UdpReceiveResult answer = await stunClient.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                IPEndPoint mapped = StunBindingCodec.DecodeSuccess(answer.Buffer, transaction, answer.RemoteEndPoint, stun.LocalEndPoint);
                Check(mapped.Equals(stunClient.Client.LocalEndPoint), "STUN Binding still returns the mapped endpoint");
                Pass("stun-binding");
            }
            finally
            {
                await stun.DisposeAsync().ConfigureAwait(false);
            }
            Console.WriteLine($"self-test passed: {passed} checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL selftest: {ex.Message}");
            return 1;
        }
    }

    private static void Check(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }

    /// <summary>Minimal client used only by --self-test; the test project builds its own independent codec.</summary>
    private sealed class ProbeClient : IDisposable
    {
        private readonly UdpClient client = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly IPEndPoint server;

        public ProbeClient(IPEndPoint server)
        {
            this.server = server;
            client.Client.ReceiveTimeout = 2000;
        }

        public IPEndPoint LocalEndPoint => (IPEndPoint)client.Client.LocalEndPoint!;

        public void Send(SignalingMessageType type, SignalingId requestId, Action<SignalingWriter>? body = null)
        {
            var writer = new SignalingWriter();
            body?.Invoke(writer);
            byte[] datagram = writer.ToMessage(type, requestId);
            client.Send(datagram, datagram.Length, server);
        }

        public byte[] Expect(SignalingMessageType type)
        {
            byte[] buffer;
            try
            {
                IPEndPoint remote = new(IPAddress.Any, 0);
                buffer = client.Receive(ref remote);
                if (!remote.Equals(server))
                {
                    throw new InvalidOperationException($"reply came from an unexpected endpoint {remote}");
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                throw new InvalidOperationException($"timed out waiting for {type}");
            }
            if (!SignalingWire.TryParseHeader(buffer, out SignalingHeader header))
                throw new InvalidOperationException("server sent an unframed datagram");
            if (header.Type != type) throw new InvalidOperationException($"expected {type}, got {header.Type}");
            return buffer;
        }

        public void Dispose() => client.Dispose();
    }

    private sealed class ProbeReply
    {
        private readonly SignalingReader reader;

        public ProbeReply(byte[] datagram)
        {
            if (!SignalingWire.TryParseHeader(datagram, out SignalingHeader header))
                throw new InvalidOperationException("unframed reply");
            reader = new SignalingReader(datagram, header);
        }

        public SignalingId Id() => reader.Id();

        public byte U8() => reader.U8();

        public ushort U16() => reader.U16();

        public uint U32() => reader.U32();

        public string Text() => reader.Text(SignalingWire.MaxTextBytes);

        public ReadOnlySpan<byte> Blob() => reader.Blob(SignalingWire.MaxBlobBytes).Span;

        public IPEndPoint? Endpoint() => reader.Endpoint();

        public List<IPEndPoint> Endpoints() => reader.Endpoints(DeviceRoleRules.MaxCandidates);
    }
}