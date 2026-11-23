using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ScreenCast.Core;
using ScreenCast.Core.Signaling;

int passed = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
void Throws<T>(Action action) where T : Exception
{ try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
void Test(string name, Action test) { test(); passed++; Console.WriteLine($"PASS {name}"); }
byte[] secret = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
byte[] session = Enumerable.Range(0, 16).Select(x => (byte)(0xA0 + x)).ToArray();
PacketProtector Protector(PacketDirection direction = PacketDirection.HostToViewer, uint stream = 0x12345678) => new(secret, session, 0x01020304, direction, PacketChannel.Video, stream);

Test("shared Rust header fixture", () =>
{
    byte[] bytes = Convert.FromHexString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "header-v1.hex")).Trim());
    var h = PacketHeader.Parse(bytes);
    Check(h.Channel == PacketChannel.ReliableControl && h.Flags == 1 && h.KeyEpoch == 0x01020304 && h.StreamId == 0x05060708 && h.PacketSequence == 0x0102030405060708, "shared fields");
    Check(h.SessionId.SequenceEqual(Enumerable.Range(0, 16).Select(i => (byte)i)), "shared raw session");
    Check(Convert.ToHexString(bytes.AsSpan(64)) == "CAFE01", "shared payload");
});
Test("deterministic AES cross-language vector", () =>
{
    byte[] rawSession = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    using var p = new PacketProtector(secret, rawSession, 1, PacketDirection.HostToViewer, PacketChannel.Video, 0);
    byte[] vector = p.Encode([1, 2, 3]);
    const string expected = "5343543101010000000102030405060708090A0B0C0D0E0F0000000100000000000000000000000000030040000000000A92411A02B67657D95A187A5A25A623C8226B";
    Check(Convert.ToHexString(vector) == expected, "deterministic encrypted datagram");
    Console.WriteLine("AES_VECTOR=" + Convert.ToHexString(vector));
});
Test("64-byte header and network byte order", () =>
{
    var header = new PacketHeader(session, 0x01020304, 0x12345678, 0x0102030405060708, PacketChannel.Video, 1, 3);
    byte[] bytes = new byte[67]; header.Write(bytes);
    Check(Convert.ToHexString(bytes.AsSpan(0, 8)) == "5343543101010001", "magic/version/channel/flags");
    Check(bytes.AsSpan(8, 16).SequenceEqual(session), "raw session");
    Check(Convert.ToHexString(bytes.AsSpan(24, 24)) == "010203041234567801020304050607080003004000000000", "network integer layout");
    Check(PacketHeader.Parse(bytes).PacketSequence == 0x0102030405060708, "header parse");
});
Test("AES roundtrip, tag and ciphertext tampering", () =>
{
    using var tx = Protector(); using var rx = Protector(); byte[] packet = tx.Encode([1, 2, 3]);
    foreach (int index in new[] { 48, 64, 32, 7 }) { byte[] altered = packet.ToArray(); altered[index] ^= 1; Throws<CryptographicException>(() => rx.Decode(altered)); }
    Check(rx.Decode(packet).SequenceEqual(new byte[] { 1, 2, 3 }), "valid after failed authentication");
    Throws<CryptographicException>(() => rx.Decode(packet));
});
Test("context separation and bounded lengths", () =>
{
    using var tx = Protector(); using var reverse = Protector(PacketDirection.ViewerToHost); using var otherStream = Protector(stream: 9);
    var packet = tx.Encode([1]); Throws<CryptographicException>(() => reverse.Decode(packet)); Throws<CryptographicException>(() => otherStream.Decode(packet));
    Throws<FormatException>(() => PacketHeader.Parse(packet[..^1])); Throws<FormatException>(() => PacketHeader.Parse([.. packet, 0]));
    byte[] bad = packet.ToArray(); bad[44] = 1; Throws<FormatException>(() => PacketHeader.Parse(bad));
    bad = packet.ToArray(); bad[42] = 1; Throws<FormatException>(() => PacketHeader.Parse(bad));
    Throws<ArgumentOutOfRangeException>(() => tx.Encode(new byte[1137]));
    Throws<ArgumentOutOfRangeException>(() => tx.Encode(new byte[1133], maxDatagramSize: 1196));
    Check(tx.Encode(new byte[1136]).Length == 1200, "maximum payload");
});
Test("1024 replay window, out of order and forged future", () =>
{
    using var tx = Protector(); using var rx = Protector(); var packets = Enumerable.Range(0, 1026).Select(_ => tx.Encode([])).ToArray();
    byte[] forged = packets[1025].ToArray(); forged[48] ^= 1; Throws<CryptographicException>(() => rx.Decode(forged));
    rx.Decode(packets[0]); rx.Decode(packets[1024]); rx.Decode(packets[1]); rx.Decode(packets[3]); rx.Decode(packets[2]);
    Throws<CryptographicException>(() => rx.Decode(packets[0])); Throws<CryptographicException>(() => rx.Decode(packets[2]));
    rx.Decode(packets[1025]);
});
Test("ACK consumes sequence and empty payload authenticates", () =>
{
    using var tx = Protector(); using var rx = Protector(); var a = tx.Encode([], true); var b = tx.Encode([]);
    Check(PacketHeader.Parse(a).Flags == 1 && PacketHeader.Parse(b).PacketSequence == 1, "ACK sequence"); Check(rx.Decode(a).Length == 0, "empty decrypt");
});
var server = new IPEndPoint(IPAddress.Loopback, 3478);
byte[] transaction = Convert.FromHexString("B7E7A701BC34D686FA87DFAE");
Test("STUN IPv4 RFC5769 XOR vector and header", () =>
{
    var endpoint = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 32853);
    byte[] response = StunBindingCodec.EncodeSuccess(transaction, endpoint);
    Check(Convert.ToHexString(response.AsSpan(20)) == "002000080001A147E112A643", "RFC XOR IPv4 vector");
    Check(Convert.ToHexString(response.AsSpan(0, 8)) == "0101000C2112A442", "STUN header network order");
    Check(StunBindingCodec.DecodeSuccess(response, transaction, server, server).Equals(endpoint), "IPv4 decode");
});
Test("STUN IPv6 RFC5769 XOR vector", () =>
{
    var endpoint = new IPEndPoint(IPAddress.Parse("2001:db8:1234:5678:11:2233:4455:6677"), 32853);
    byte[] response = StunBindingCodec.EncodeSuccess(transaction, endpoint);
    Check(Convert.ToHexString(response.AsSpan(24)) == "0002A1470113A9FAA5D3F179BC25F4B5BED2B9D9", "RFC XOR IPv6 vector");
    Check(StunBindingCodec.DecodeSuccess(response, transaction, server, server).Equals(endpoint), "IPv6 decode");
});
Test("STUN lengths, padding, source, transaction, reserved bits", () =>
{
    byte[] packet = StunBindingCodec.EncodeSuccess(transaction, server);
    Throws<FormatException>(() => StunBindingCodec.Parse(packet[..^1]));
    Throws<FormatException>(() => StunBindingCodec.DecodeSuccess(packet, new byte[12], server, server));
    Throws<FormatException>(() => StunBindingCodec.DecodeSuccess(packet, transaction, new(IPAddress.Loopback, 9999), server));
    byte[] bad = packet.ToArray(); bad[0] |= 0xC0; Throws<FormatException>(() => StunBindingCodec.Parse(bad));
    bad = packet.ToArray(); bad[23] = 255; Throws<FormatException>(() => StunBindingCodec.Parse(bad));
    bad = packet.ToArray(); bad[24] = 1; Throws<FormatException>(() => StunBindingCodec.DecodeSuccess(bad, transaction, server, server));
    var padded = StunBindingCodec.Encode(1, transaction, [new(0x8022, [42])]);
    Check(padded.Length == 28 && StunBindingCodec.Parse(padded).Attributes[0].Value.Length == 1, "attribute padding");
});
Test("STUN optional fingerprint validates CRC and position", () =>
{
    byte[] request = Convert.FromHexString("000100582112A442B7E7A701BC34D686FA87DFAE802200105354554E207465737420636C69656E74002400046E0001FF80290008932FF9B151263B36000600096576746A3A68367659202020000800149AEAA70CBFD8CB56781EF2B5B2D3F249C1B571A280280004E57A3BCF");
    StunBindingCodec.Parse(request);
    byte[] bad = request.ToArray(); bad[^1] ^= 1; Throws<FormatException>(() => StunBindingCodec.Parse(bad));
    bad = StunBindingCodec.Encode(1, transaction, [new(0x8028, new byte[4]), new(0x8022, [])]);
    Throws<FormatException>(() => StunBindingCodec.Parse(bad));
    var success = StunBindingCodec.Parse(StunBindingCodec.EncodeSuccess(transaction, server));
    byte[] both = StunBindingCodec.Encode(StunBindingCodec.BindingSuccess, transaction, [new(0x0001, [0, 1, 0x0D, 0x96, 127, 0, 0, 1]), .. success.Attributes]);
    Check(StunBindingCodec.DecodeSuccess(both, transaction, server, server).Equals(server), "Mapped alongside XOR accepted");
});
Test("STUN Binding unknown required attribute returns 420", () =>
{
    byte[] request = StunBindingCodec.Encode(1, transaction, [new(0x1234, [1]), new(0x1234, [2])]);
    var message = StunBindingCodec.Parse(StunBindingCodec.CreateBindingResponse(request, server)!);
    Check(message.Type == StunBindingCodec.BindingError, "error type");
    var code = message.Attributes.Single(a => a.Type == 9).Value; Check(code[2] == 4 && code[3] == 20, "420");
    Check(Convert.ToHexString(message.Attributes.Single(a => a.Type == 10).Value) == "1234", "unknown dedup");
    Check(StunBindingCodec.CreateBindingResponse(StunBindingCodec.Encode(0x0003, transaction, []), server) == null, "Allocate unsupported");
});
Test("three failed attempts request Relay without false connection", () =>
{
    var c = new ConnectionStateMachine(); c.Start(); c.DiscoveryCompleted(NatObservation.Unknown);
    c.PunchAttemptFailed(c.Generation); c.PunchAttemptFailed(c.Generation); Check(c.State == ConnectionState.Punching, "two failures");
    c.PunchAttemptFailed(c.Generation); Check(c.State == ConnectionState.Relay && c.FailedPunchAttempts == 3, "three failures");
    c.RelayComponentConfirmed(c.Generation, TransportComponent.Video); Check(c.State == ConnectionState.Relay, "one relay component");
    c.RelayComponentConfirmed(c.Generation, TransportComponent.Control); Check(c.State == ConnectionState.ConnectedRelay, "both relay components");
});
Test("symmetric NAT goes Relay; two direct components required", () =>
{
    var second = new IPEndPoint(IPAddress.Parse("192.0.2.20"), 3478);
    var observation = NatClassifier.Observe(server, new(IPAddress.Loopback, 1000), second, new(IPAddress.Loopback, 1001));
    Check(observation == NatObservation.Symmetric, "destination dependent mapping");
    var c = new ConnectionStateMachine(); c.Start(); c.DiscoveryCompleted(observation); Check(c.State == ConnectionState.Relay, "symmetric fallback");
    c.RelayFailed(); c.Start(); c.DiscoveryCompleted(NatObservation.Unknown);
    c.DirectComponentConfirmed(c.Generation - 1, TransportComponent.Control); Check(c.State == ConnectionState.Punching, "stale generation");
    c.DirectComponentConfirmed(c.Generation, TransportComponent.Video); Check(c.State == ConnectionState.Punching, "partial direct");
    c.DirectComponentConfirmed(c.Generation, TransportComponent.Control); Check(c.State == ConnectionState.Direct, "both direct");
});
Test("loopback UDP Binding IPv4 and IPv6", () =>
{
    foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
    {
        using var listener = new UdpClient(new IPEndPoint(address, 0)); using var client = new UdpClient(new IPEndPoint(address, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var endpoint = (IPEndPoint)listener.Client.LocalEndPoint!;
        Task service = Task.Run(async () => { var d = await listener.ReceiveAsync(timeout.Token); var answer = StunBindingCodec.CreateBindingResponse(d.Buffer, d.RemoteEndPoint)!; await listener.SendAsync(answer.AsMemory(), d.RemoteEndPoint, timeout.Token); });
        var request = StunBindingCodec.EncodeRequest(transaction); client.Send(request, endpoint);
        var result = client.ReceiveAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
        Check(StunBindingCodec.DecodeSuccess(result.Buffer, transaction, result.RemoteEndPoint, endpoint).Equals(client.Client.LocalEndPoint), "actual UDP mapped endpoint");
        service.GetAwaiter().GetResult();
    }
});
Test("HKDF media key known answer", () =>
{
    byte[] sessionId = Enumerable.Range(0, 16).Select(index => (byte)index).ToArray();
    string key = Convert.ToHexString(SignalingKeys.DeriveMediaSecret("123456789", sessionId));
    Check(key == "9F61E5F76747EB556ECAC57750B7AED28ED013CBE2C57D34796DD512CABABEB2", "cross-language HKDF vector");
});
Test("signaling nine-digit invite and peer media exchange", () =>
{
    RunSignalingFlowAsync().GetAwaiter().GetResult();
});
Test("stable pairing code survives idle sessions and server restarts", () =>
{
    void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
    string file = Path.Combine(Path.GetTempPath(), $"screencast-pair-{Guid.NewGuid():N}.json");
    try
    {
        var options = new ScreenCast.Server.SignalingOptions();
        var service = new ScreenCast.Server.Signaling.SignalingService(options, new ScreenCast.Server.Signaling.PairingTable(file));
        var hostEnd = new IPEndPoint(IPAddress.Loopback, 41000);
        var viewerEnd = new IPEndPoint(IPAddress.Loopback, 41001);
        long now = 1_000;

        List<ScreenCast.Server.Signaling.OutboundMessage> Send(IPEndPoint from, SignalingMessageType type, Action<SignalingWriter> body)
        {
            var writer = new SignalingWriter();
            body(writer);
            var outbox = new List<ScreenCast.Server.Signaling.OutboundMessage>();
            service.Handle(writer.ToMessage(type, SignalingId.NewRandom()), from, now, outbox);
            return outbox;
        }
        byte[] Reply(List<ScreenCast.Server.Signaling.OutboundMessage> outbox, IPEndPoint to) =>
            outbox.Single(message => message.Destination.Equals(to)).Datagram;
        SignalingReader Open(byte[] datagram)
        {
            Check(SignalingWire.TryParseHeader(datagram, out SignalingHeader header), "framed reply");
            return new SignalingReader(datagram, header);
        }

        Send(hostEnd, SignalingMessageType.Register, writer =>
        {
            writer.Text("stable-host");
            writer.U8((byte)DeviceRoles.Host);
            writer.U8(0);
            writer.Blob(ReadOnlySpan<byte>.Empty);
            writer.Endpoints(Array.Empty<IPEndPoint>());
        });
        Send(viewerEnd, SignalingMessageType.Register, writer =>
        {
            writer.Text("stable-viewer");
            writer.U8((byte)DeviceRoles.Viewer);
            writer.U8(0);
            writer.Blob(ReadOnlySpan<byte>.Empty);
            writer.Endpoints(Array.Empty<IPEndPoint>());
        });

        SignalingReader created = Open(Reply(Send(hostEnd, SignalingMessageType.CreateSession, writer =>
        {
            writer.Text(string.Empty);
            writer.Text(string.Empty);
        }), hostEnd));
        SignalingId firstSession = created.Id();
        Check(created.U8() == (byte)SessionState.Created, "created state");
        created.U32();
        created.U8();
        string code = created.Text(16);
        Check(code.Length == 9 && code[0] != '0', "nine-digit code");

        now += options.InviteTimeoutMs * 10L + options.SessionIdleMs * 10L;
        Send(hostEnd, SignalingMessageType.Heartbeat, _ => { });
        Send(viewerEnd, SignalingMessageType.Heartbeat, _ => { });
        var scanOutbox = new List<ScreenCast.Server.Signaling.OutboundMessage>();
        service.Scan(now, scanOutbox);
        Check(service.SessionCount == 1, "waiting session is kept alive by the stable code");
        Check(scanOutbox.Count == 0, "no close notification while the host waits");

        List<ScreenCast.Server.Signaling.OutboundMessage> joined = Send(viewerEnd, SignalingMessageType.JoinSession, writer =>
        {
            writer.Text(code);
            writer.Id(SignalingId.Zero);
        });
        SignalingReader joinedView = Open(Reply(joined, viewerEnd));
        Check(joinedView.Id().Equals(firstSession), "viewer joins the waiting session");
        Check(joined.Any(message => message.Destination.Equals(hostEnd)), "host is notified of the join");

        Send(hostEnd, SignalingMessageType.CloseSession, writer =>
        {
            writer.Id(firstSession);
            writer.U8((byte)SessionCloseReason.UserClosed);
        });
        Check(service.SessionCount == 0, "session is gone after close");

        List<ScreenCast.Server.Signaling.OutboundMessage> rejoined = Send(viewerEnd, SignalingMessageType.JoinSession, writer =>
        {
            writer.Text(code);
            writer.Id(SignalingId.Zero);
        });
        SignalingReader rejoinedView = Open(Reply(rejoined, viewerEnd));
        Check(!rejoinedView.Id().Equals(firstSession), "code opens a fresh session without a new invite");
        Check(rejoined.Any(message => message.Destination.Equals(hostEnd)), "host is woken by the stored code");
        Check(service.SessionCount == 1, "only the fresh session exists");

        var reloaded = new ScreenCast.Server.Signaling.PairingTable(file);
        Check(reloaded.HostForCode(code) == "stable-host", "pairing survives a server restart");
        Check(reloaded.CodeForHost("stable-host") == code, "host keeps the same code");
    }
    finally
    {
        File.Delete(file);
    }
});

Console.WriteLine($"All {passed} tests passed.");

static async Task RunSignalingFlowAsync()
{
    void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
    byte[] SessionBytes(SignalingId id) { byte[] bytes = new byte[SignalingId.Size]; id.WriteTo(bytes); return bytes; }

    var options = new ScreenCast.Server.SignalingOptions();
    var service = new ScreenCast.Server.Signaling.SignalingService(options);
    using var relay = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var relayEndpoint = (IPEndPoint)relay.Client.LocalEndPoint!;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var stopwatch = Stopwatch.StartNew();
    Task relayTask = Task.Run(async () =>
    {
        while (!timeout.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await relay.ReceiveAsync(timeout.Token);
            }
            catch (Exception)
            {
                return;
            }

            var outbox = new List<ScreenCast.Server.Signaling.OutboundMessage>();
            service.Handle(datagram.Buffer, datagram.RemoteEndPoint, stopwatch.ElapsedMilliseconds, outbox);
            foreach (ScreenCast.Server.Signaling.OutboundMessage message in outbox)
            {
                await relay.SendAsync(message.Datagram, message.Destination, timeout.Token);
            }
        }
    });

    await using var host = new SignalingClient(relayEndpoint.Address.ToString(), relayEndpoint.Port, "host-device-1", DeviceRoleRules.All);
    await using var viewer = new SignalingClient(relayEndpoint.Address.ToString(), relayEndpoint.Port, "viewer-device-1", DeviceRoleRules.All);
    await host.StartAsync(timeout.Token);
    await viewer.StartAsync(timeout.Token);

    var joinRequested = new TaskCompletionSource<SessionView>(TaskCreationOptions.RunContinuationsAsynchronously);
    host.JoinRequested += view => joinRequested.TrySetResult(view);

    SessionView created = await host.CreateSessionAsync(string.Empty, timeout.Token);
    string code = created.InviteCode;
    Check(code.Length == 9 && code.All(char.IsAsciiDigit) && code[0] != '0', "nine-digit numeric invite");

    var answered = new TaskCompletionSource<PeerKeyMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    host.PeerKeyReceived += message =>
    {
        if (message.Type == SignalingMessageType.PeerKeyAnswer) answered.TrySetResult(message);
    };
    var offered = new TaskCompletionSource<PeerKeyMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    viewer.PeerKeyReceived += message =>
    {
        if (message.Type == SignalingMessageType.PeerKeyOffer) offered.TrySetResult(message);
    };

    SessionView joined = await viewer.JoinSessionAsync(code, timeout.Token);
    Check(joined.SessionId.Equals(created.SessionId), "viewer sees the same session id");
    SessionView request = await joinRequested.Task.WaitAsync(timeout.Token);
    Check(request.SessionId.Equals(created.SessionId), "host is notified of the join");

    await host.AcceptSessionAsync(created.SessionId, timeout.Token);

    var hostInfo = new PeerMediaInfo(PeerMediaInfo.HostRole, 42000, 42001, new[] { IPAddress.Loopback });
    await host.SendPeerKeyAsync(SignalingMessageType.PeerKeyOffer, created.SessionId, hostInfo.Encode(), timeout.Token);
    PeerKeyMessage offer = await offered.Task.WaitAsync(timeout.Token);
    PeerMediaInfo decodedHost = PeerMediaInfo.Decode(offer.Payload);
    Check(decodedHost.VideoPort == 42000 && decodedHost.ControlPort == 42001 && decodedHost.IsHost, "offer carries host ports");

    var viewerInfo = new PeerMediaInfo(PeerMediaInfo.ViewerRole, 42100, 42101, new[] { IPAddress.Parse("192.168.1.20") });
    await viewer.SendPeerKeyAsync(SignalingMessageType.PeerKeyAnswer, created.SessionId, viewerInfo.Encode(), timeout.Token);
    PeerKeyMessage answer = await answered.Task.WaitAsync(timeout.Token);
    PeerMediaInfo decodedViewer = PeerMediaInfo.Decode(answer.Payload);
    Check(decodedViewer.VideoPort == 42100 && decodedViewer.ControlPort == 42101 && !decodedViewer.IsHost, "answer carries viewer ports");
    Check(decodedViewer.LocalAddresses[0].Equals(IPAddress.Parse("192.168.1.20")), "local candidate survives the round trip");

    byte[] hostSecret = SignalingKeys.DeriveMediaSecret(code, SessionBytes(created.SessionId));
    byte[] viewerSecret = SignalingKeys.DeriveMediaSecret(code, SessionBytes(joined.SessionId));
    Check(hostSecret.Length == 32 && hostSecret.SequenceEqual(viewerSecret), "both sides derive the same media secret");
    Check(!hostSecret.SequenceEqual(new byte[32]), "derived secret is not zero");

    await host.CloseSessionAsync(created.SessionId, timeout.Token).ContinueWith(_ => { }, TaskScheduler.Default);
    timeout.Cancel();
    try
    {
        await relayTask;
    }
    catch (Exception)
    {
    }
}

