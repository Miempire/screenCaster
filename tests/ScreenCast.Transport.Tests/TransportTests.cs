using ScreenCast.Core;
using ScreenCast.Core.Transport;

namespace ScreenCast.Transport.Tests;

internal static class TransportTests
{
    private static byte[] Secret() => Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
    private static byte[] SessionId() => Convert.FromHexString("a1a2a3a4a5a6a7a8a9aaabacadaeafb0");

    private static void Fail(string name, string detail)
    {
        Console.WriteLine($"FAIL {name}: {detail}");
        Environment.ExitCode = 1;
        throw new TestFailed(name);
    }

    private sealed class TestFailed(string name) : Exception($"Test failed: {name}");

    private static void Check(string name, bool condition, string detail)
    {
        if (!condition)
        {
            Fail(name, detail);
        }

        Console.WriteLine($"PASS {name}");
    }

    private static void Run(string name, Action body)
    {
        try
        {
            body();
        }
        catch (TestFailed)
        {
            throw;
        }
        catch (Exception ex)
        {
            Fail(name, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RunAsync(string name, Func<Task> body)
    {
        try
        {
            body().GetAwaiter().GetResult();
        }
        catch (TestFailed)
        {
            throw;
        }
        catch (Exception ex)
        {
            Fail(name, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void All()
    {
        Run("classifier: application magic wins over TURN 0x5343 overlap", () =>
        {
            byte[] application = Convert.FromHexString("53435431" + new string('0', 20));
            Check("classifier application", DatagramClassifier.Classify(application) == DatagramKind.Application,
                "application magic must win");

            byte[] handshake = Convert.FromHexString("53435348" + new string('0', 20));
            Check("classifier handshake", DatagramClassifier.Classify(handshake) == DatagramKind.Handshake,
                "handshake magic");

            byte[] stun = Convert.FromHexString("000100002112A442000000000000000000000000");
            Check("classifier stun", DatagramClassifier.Classify(stun) == DatagramKind.Stun, "STUN cookie");

            byte[] channelData = [0x40, 0x07, 0x00, 0x04, 0xca, 0xfe, 0xba, 0xbe];
            Check("classifier turn channel", DatagramClassifier.Classify(channelData) == DatagramKind.TurnChannelData,
                "0x4007 is a channel number");

            byte[] other = [0x00, 0x09, 0x00, 0x00];
            Check("classifier unknown", DatagramClassifier.Classify(other) == DatagramKind.Unknown, "unrelated bytes");
        });

        Run("control codec: data roundtrip and malformed rejection", () =>
        {
            byte[] body = [1, 2, 3, 4, 5];
            byte[] frame = ControlCodec.EncodeData(7, ulong.MaxValue, 0b101, ControlEventType.Key, body);
            Check("control data length", frame.Length == ControlCodec.HeaderSize + body.Length, "header plus body");
            Check("control data decode", ControlCodec.TryDecode(frame, out var message), "must decode");
            Check("control data fields",
                !message.IsAck && message.Sequence == 7 && message.AckBase == ulong.MaxValue
                && message.AckMask == 0b101 && message.EventType == ControlEventType.Key
                && message.Body.Length == body.Length && message.Body.Span.SequenceEqual(body),
                "field roundtrip");

            byte[] ack = ControlCodec.EncodeAck(3, 0xF);
            Check("control ack length", ack.Length == ControlCodec.HeaderSize, "ACK is header only");
            Check("control ack decode", ControlCodec.TryDecode(ack, out var ackMessage)
                && ackMessage.IsAck && ackMessage.AckBase == 3 && ackMessage.AckMask == 0xF, "ACK fields");

            byte[] corrupt = (byte[])frame.Clone();
            corrupt[30] = 9; // claim a longer body than present
            Check("control reject length lie", !ControlCodec.TryDecode(corrupt, out _), "must reject");

            byte[] badKind = (byte[])frame.Clone();
            badKind[0] = 9;
            Check("control reject kind", !ControlCodec.TryDecode(badKind, out _), "must reject unknown kind");

            byte[] badReserved = (byte[])frame.Clone();
            badReserved[1] = 1;
            Check("control reject reserved", !ControlCodec.TryDecode(badReserved, out _), "must reject reserved bytes");
        });

        Run("control codec: body budget", () =>
        {
            Check("control body budget", ControlCodec.MaxBodyLength == 1200 - 64 - 32, "datagram budget math");
            byte[] tooBig = new byte[ControlCodec.MaxBodyLength + 1];
            bool threw = false;
            try
            {
                ControlCodec.EncodeData(0, ulong.MaxValue, 0, ControlEventType.Key, tooBig);
            }
            catch (ArgumentOutOfRangeException)
            {
                threw = true;
            }

            Check("control reject oversized body", threw, "must throw");
        });

        RunAsync("control channel: ordered delivery, duplicate suppression, ACK repeated", async () =>
        {
            var hostClock = new FakeClock();
            var viewerClock = new FakeClock();
            List<byte[]> hostToViewer = [];
            List<byte[]> viewerToHost = [];
            List<(ControlEventType, byte[])> delivered = [];

            var hostChannel = new ReliableControlChannel(
                frame => hostToViewer.Add(frame.ToArray()),
                (_, _) => { },
                hostClock.Now);

            var viewerChannel = new ReliableControlChannel(
                frame => viewerToHost.Add(frame.ToArray()),
                (type, body) => delivered.Add((type, body.ToArray())),
                viewerClock.Now);

            hostChannel.TryEnqueue(ControlEventType.Key, [10]);
            hostChannel.TryEnqueue(ControlEventType.MouseButton, [20]);
            hostChannel.TryEnqueue(ControlEventType.Wheel, [30]);
            Check("control three queued", hostToViewer.Count == 3 && hostChannel.PendingCount == 3, "queued frames");

            // Deliver the second before the first: the receiver must buffer, not deliver out of order.
            viewerToHost.Clear();
            viewerChannel.OnFrame(hostToViewer[1]);
            Check("control no early delivery", delivered.Count == 0, "gap must block delivery");
            Check("control ack after out of order", viewerToHost.Count == 1, "receiver must ACK");
            hostChannel.OnFrame(viewerToHost[0]);
            Check("control selective ack", hostChannel.PendingCount == 2, "only the acked message leaves");

            // Deliver the missing first message and then the third.
            viewerChannel.OnFrame(hostToViewer[0]);
            Check("control ordered release", delivered.Count == 2 && delivered[0].Item1 == ControlEventType.Key
                && delivered[1].Item1 == ControlEventType.MouseButton, "in order");

            viewerChannel.OnFrame(hostToViewer[2]);
            Check("control complete order", delivered.Count == 3 && delivered[2].Item1 == ControlEventType.Wheel,
                "all delivered");

            // Duplicate of an already delivered message: side effect once, ACK again.
            int ackCountBefore = viewerToHost.Count;
            viewerChannel.OnFrame(hostToViewer[0]);
            Check("control duplicate suppressed", delivered.Count == 3, "no second side effect");
            Check("control duplicate acked", viewerToHost.Count == ackCountBefore + 1, "ACK must repeat");

            await Task.CompletedTask;
        });

        Run("control channel: lost ACK then retransmit still completes", () =>
        {
            var clock = new FakeClock();
            List<byte[]> outbound = [];
            List<(ControlEventType, byte[])> delivered = [];
            var sender = new ReliableControlChannel(frame => outbound.Add(frame.ToArray()),
                (_, _) => { }, clock.Now);
            var receiver = new ReliableControlChannel(_ => { }, (type, body) => delivered.Add((type, body.ToArray())), clock.Now);

            sender.TryEnqueue(ControlEventType.Key, [0xAB]);
            Check("control one pending", sender.PendingCount == 1, "one message in flight");

            // First ACK is lost entirely. Advance past RTO: the sender must retransmit.
            clock.Advance(300_000);
            sender.Tick();
            byte[]? firstData = outbound.Count > 1 ? outbound[1] : null;
            Check("control retransmitted", firstData is not null, "RTO must trigger a resend");
            Check("control retransmit reuses message number",
                firstData is not null && ControlCodec.TryDecode(firstData, out var retried) && retried.Sequence == 0,
                "same control message number");

            receiver.OnFrame(firstData!);
            Check("control delivered after retransmit", delivered.Count == 1 && delivered[0].Item1 == ControlEventType.Key,
                "delivery completes");
        });

        Run("video framing: split and reassemble roundtrip", () =>
        {
            byte[] frameData = new byte[5000];
            for (int i = 0; i < frameData.Length; i++)
            {
                frameData[i] = (byte)(i * 7);
            }

            int count = VideoFragmenter.FragmentCountFor(frameData.Length);
            Check("video fragment count", count == (5000 + VideoFragmentHeader.MaxFragmentPayload - 1) / VideoFragmentHeader.MaxFragmentPayload,
                "fragment math");

            var assembler = new VideoAssembler(new FakeClock().Now);
            AssembledVideoFrame? assembled = null;
            byte[] buffer = new byte[VideoFragmentHeader.MaxFragmentPayload + VideoFragmentHeader.Size];
            for (int index = 0; index < count; index++)
            {
                int length = VideoFragmenter.WriteFragment(frameData, 42, 1234, 7,
                    VideoFrameFlags.KeyFrame, VideoCodec.H264, index, buffer);
                assembled = assembler.Push(buffer.AsSpan(0, length));
            }

            Check("video reassembled", assembled is not null, "frame must complete");
            Check("video payload identical", assembled!.Data.AsSpan().SequenceEqual(frameData), "bytes must match");
            Check("video metadata", assembled.FrameId == 42 && assembled.CaptureTimestampMicroseconds == 1234
                && assembled.ConfigId == 7 && assembled.Flags == VideoFrameFlags.KeyFrame
                && assembled.Codec == VideoCodec.H264, "metadata preserved");
        });

        Run("video framing: missing fragment drops whole frame", () =>
        {
            byte[] frameData = new byte[3000];
            int count = VideoFragmenter.FragmentCountFor(frameData.Length);
            var assembler = new VideoAssembler();
            byte[] buffer = new byte[VideoFragmentHeader.MaxFragmentPayload + VideoFragmentHeader.Size];
            AssembledVideoFrame? assembled = null;
            for (int index = 0; index < count; index++)
            {
                if (index == 1)
                {
                    continue; // lose one fragment
                }

                int length = VideoFragmenter.WriteFragment(frameData, 9, 0, 1,
                    VideoFrameFlags.None, VideoCodec.H264, index, buffer);
                assembled = assembler.Push(buffer.AsSpan(0, length));
            }

            Check("video partial never emitted", assembled is null, "incomplete frame must not reach decoder");
            Check("video partial buffered", assembler.PartialFrameCount == 1, "partial stays bounded");
        });

        Run("video framing: stale partial frame expires", () =>
        {
            var clock = new FakeClock();
            var assembler = new VideoAssembler(clock.Now, frameLifetimeMicroseconds: 100_000);
            byte[] frameData = new byte[5000];
            byte[] buffer = new byte[VideoFragmentHeader.MaxFragmentPayload + VideoFragmentHeader.Size];
            int length = VideoFragmenter.WriteFragment(frameData, 5, 0, 1,
                VideoFrameFlags.None, VideoCodec.H264, 0, buffer);
            assembler.Push(buffer.AsSpan(0, length));
            Check("video partial present", assembler.PartialFrameCount == 1, "one partial");

            clock.Advance(200_000);
            assembler.Push([0, 0, 0, 0]); // garbage triggers the sweep
            Check("video stale dropped", assembler.PartialFrameCount == 0, "sweep removes it");
            Check("video drop counted", assembler.DroppedFrames >= 1 && assembler.BufferedBytes == 0, "accounting");
        });

        Run("probe: request and response roundtrip with echo", () =>
        {
            long sendTime = 1_000_000;
            byte[] request = ProbeMessage.EncodeRequest(0xAABB, sendTime);
            Check("probe request decode", ProbeMessage.TryParse(request, out var parsed)
                && parsed.Kind == ProbeKind.Request && parsed.Token == 0xAABB
                && parsed.SendMonotonicMicroseconds == (ulong)sendTime && parsed.EchoMonotonicMicroseconds == 0,
                "request fields");

            byte[] response = ProbeMessage.EncodeResponse(0xAABB, 2_000_000, (ulong)sendTime);
            Check("probe response decode", ProbeMessage.TryParse(response, out var answered)
                && answered.Kind == ProbeKind.Response && answered.Token == 0xAABB
                && answered.EchoMonotonicMicroseconds == (ulong)sendTime, "response echoes requester clock");

            Check("probe rejects short", !ProbeMessage.TryParse(request.AsSpan(0, 31), out _), "length must be exact");
            byte[] badKind = (byte[])request.Clone();
            badKind[0] = 7;
            Check("probe rejects kind", !ProbeMessage.TryParse(badKind, out _), "unknown kind rejected");
            byte[] badReserved = (byte[])request.Clone();
            badReserved[3] = 1;
            Check("probe rejects reserved", !ProbeMessage.TryParse(badReserved, out _), "reserved bytes rejected");
        });

        Run("path probe: rtt needs a matching token and sending never refreshes liveness", () =>
        {
            var clock = new FakeClock();
            var probe = new PathProbe(clock.Now, missesForDown: 3, keepaliveIntervalMicroseconds: 1_000_000);

            Check("probe first tick sends", probe.Tick(), "keepalive due");
            byte[] request = probe.CreateRequest();

            // Merely sending must not count as liveness.
            Check("probe still probing", probe.Status == PathStatus.Probing, "no response yet");

            clock.Advance(20_000); // 20 ms round trip
            ulong token = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(request.AsSpan(8));
            byte[] response = ProbeMessage.EncodeResponse(token, 0, 0);
            Check("probe response accepted", probe.OnProbe(response) is null, "response carries no reply");
            Check("probe rtt measured", Math.Abs(probe.SmoothedRttMicroseconds - 20_000) < 1,
                $"expected ~20000, got {probe.SmoothedRttMicroseconds}");
            Check("probe alive", probe.Status == PathStatus.Alive && probe.ConsecutiveMisses == 0, "alive");
            Check("probe answered once", probe.ProbesAnswered == 1, "one answer");

            // Unknown token must not be treated as an answer.
            bool ignored = probe.OnProbe(ProbeMessage.EncodeResponse(0xDEAD, 30_000, 0)) is null;
            Check("probe unknown token ignored", ignored && probe.ProbesAnswered == 1, "stale token ignored");
        });

        Run("path probe: three silent intervals mark the path down", () =>
        {
            var clock = new FakeClock();
            var probe = new PathProbe(clock.Now, missesForDown: 3, keepaliveIntervalMicroseconds: 1_000_000);
            probe.Tick();
            probe.CreateRequest();

            for (int i = 0; i < 3; i++)
            {
                clock.Advance(1_000_001);
                probe.Tick();
            }

            Check("probe down after misses", probe.Status == PathStatus.Down, "three misses");
            Check("probe miss count", probe.ConsecutiveMisses >= 3, "misses counted");
        });

        Run("punch coordinator: sends on schedule, exhausts after three attempts", () =>
        {
            var clock = new FakeClock();
            var candidateA = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.0.2.10"), 5000);
            var candidateB = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.0.2.11"), 5000);
            var coordinator = new PunchCoordinator(4, [candidateA, candidateB], clock.Now);
            coordinator.Start();

            var first = coordinator.Tick();
            Check("punch first burst", first.Count == 2 && first.Contains(candidateA) && first.Contains(candidateB),
                "offset 0 hits every candidate");
            Check("punch no duplicate on second tick", coordinator.Tick().Count == 0, "same offset must not repeat");

            clock.Advance(100_001);
            var second = coordinator.Tick();
            Check("punch second burst", second.Count == 2, "offset 100 ms");

            // Skip the rest of attempt 1 and attempts 2 and 3.
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                clock.Advance(PunchCoordinator.AttemptDurationMicroseconds + 1);
                coordinator.Tick();
            }

            Check("punch exhausted", coordinator.Exhausted, "three attempts consumed");
            Check("punch three attempts seen", coordinator.Attempt == PunchCoordinator.MaxAttempts, "attempt counter");
            Check("punch no peer selected", coordinator.SelectedPeer is null && !coordinator.Succeeded, "no false success");
            Check("punch stopped sending", coordinator.Tick().Count == 0, "exhausted coordinator stays quiet");
        });

        Run("punch coordinator: authenticated probe from an unlisted address wins", () =>
        {
            var clock = new FakeClock();
            var candidate = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("203.0.113.5"), 6000);
            var coordinator = new PunchCoordinator(1, [candidate], clock.Now);
            coordinator.Start();

            // A NAT may map the peer to an address the signalling server never saw.
            var reflexive = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("198.51.100.9"), 7777);
            Check("punch completes", coordinator.OnAuthenticatedProbe(reflexive), "authenticated peer accepted");
            Check("punch selected peer", coordinator.SelectedPeer is not null
                && coordinator.SelectedPeer.Equals(reflexive), "selected the observed source");
            Check("punch stays complete", !coordinator.OnAuthenticatedProbe(candidate), "idempotent");
        });

        Run("link controller: symmetric NAT skips punching entirely", () =>
        {
            var candidate = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("203.0.113.5"), 6000);
            var link = new LinkController(NatObservation.Symmetric, new FakeClock().Now);
            link.Begin([candidate], 1);

            Check("link symmetric goes relay", link.State == LinkState.RelayRequired, "no punching");
            Check("link symmetric reason", link.Reason is not null, "reason recorded");
            Check("link symmetric sends nothing", link.TickPunch().Count == 0, "no punch datagrams");
        });

        Run("link controller: unknown NAT tries a real punch first", () =>
        {
            var clock = new FakeClock();
            var candidate = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("203.0.113.5"), 6000);
            var link = new LinkController(NatObservation.Unknown, clock.Now);
            link.Begin([candidate], 7);
            Check("link unknown punches", link.State == LinkState.Punching && link.PunchAttempt == 1, "attempt one");
            Check("link punch burst", link.TickPunch().Count == 1, "candidate probed");
        });

        Run("link controller: three failed attempts request relay", () =>
        {
            var clock = new FakeClock();
            var candidate = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("203.0.113.5"), 6000);
            var link = new LinkController(NatObservation.Unknown, clock.Now);
            link.Begin([candidate], 2);

            for (int attempt = 1; attempt <= LinkController.PunchAttemptsBeforeRelay; attempt++)
            {
                clock.Advance(PunchCoordinator.AttemptDurationMicroseconds + 1);
                link.TickPunch();
            }

            Check("link exhausted", link.State == LinkState.PunchingExhausted, "relay required after three attempts");
            Check("link exhaustion reason", link.Reason is not null, "reason recorded");
            Check("link not falsely direct", link.SelectedPeer is null, "no peer without proof");
        });

        Run("link controller: authenticated probe promotes to direct", () =>
        {
            var clock = new FakeClock();
            var candidate = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("203.0.113.5"), 6000);
            var link = new LinkController(NatObservation.Unknown, clock.Now);
            link.Begin([candidate], 3);
            link.TickPunch();

            var observed = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("198.51.100.7"), 8080);
            Check("link direct", link.OnAuthenticatedProbe(TransportComponent.Video, observed), "promoted");
            Check("link direct state", link.State == LinkState.Direct && link.SelectedPeer!.Equals(observed),
                "peer is the observed source");
            Check("link direct idempotent", !link.OnAuthenticatedProbe(TransportComponent.Control, observed),
                "already direct");
        });

        Run("link controller: keepalive silence marks the link down", () =>
        {
            var clock = new FakeClock();
            var candidate = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("203.0.113.5"), 6000);
            var link = new LinkController(NatObservation.Unknown, clock.Now, keepaliveIntervalMicroseconds: 1_000_000);
            link.Begin([candidate], 4);
            link.TickPunch();
            link.OnAuthenticatedProbe(TransportComponent.Video, candidate);

            // Answer nothing and let the keepalive clock run out.
            for (int i = 0; i < 4; i++)
            {
                clock.Advance(1_000_001);
                link.TickKeepalive(out _, out _);
            }

            Check("link down after silence", link.State == LinkState.Down, "keepalive supervision");
            Check("link down reason", link.Reason is not null, "reason recorded");
        });

        RunAsync("udp transport: real loopback send and receive", async () =>
        {
            using var receiver = new UdpTransport(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            using var sender = new UdpTransport(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            byte[] payload = [9, 8, 7, 6, 5];
            await sender.SendAsync(payload, receiver.LocalEndPoint, default);
            using var lease = await receiver.ReceiveAsync(new CancellationTokenSource(3000).Token);
            Check("udp payload", lease.Payload.Span.SequenceEqual(payload), "bytes must match");
            Check("udp source port", lease.Source.Port == sender.LocalPort, "source port preserved");
            Check("udp source address", lease.Source.Address.Equals(System.Net.IPAddress.Loopback), "loopback");
            await Task.CompletedTask;
        });

        RunAsync("udp transport: cancellation keeps the socket bound and reusable", async () =>
        {
            using var receiver = new UdpTransport(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            int port = receiver.LocalPort;
            using (var cts = new CancellationTokenSource())
            {
                // Cancel a pending receive: the socket must survive.
                Task cancelled = Task.Run(async () =>
                {
                    try
                    {
                        await receiver.ReceiveAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // expected
                    }
                });
                await Task.Delay(50);
                cts.Cancel();
                await cancelled;
            }

            Check("udp port preserved", receiver.LocalPort == port, "port must not change on cancel");
            using var sender = new UdpTransport(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            await sender.SendAsync(new byte[] { 1, 2 }, receiver.LocalEndPoint, default);
            using var lease = await receiver.ReceiveAsync(new CancellationTokenSource(3000).Token);
            Check("udp reusable after cancel", lease.Payload.Length == 2, "socket still works");
        });

        RunAsync("udp transport: second receive owner is rejected", async () =>
        {
            using var transport = new UdpTransport(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            using var cts = new CancellationTokenSource(2000);
            Task first = Task.Run(async () =>
            {
                try
                {
                    await transport.ReceiveAsync(cts.Token);
                }
                catch (Exception)
                {
                    // timeout is fine
                }
            });
            await Task.Delay(50);
            bool threw = false;
            try
            {
                await transport.ReceiveAsync(new CancellationTokenSource(10).Token);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Check("udp rejects second owner", threw, "single-owner contract");
            cts.Cancel();
            await first;
        });

        RunAsync("media session: end to end encrypted video and control over loopback", async () =>
        {
            var hostVideo = new UdpTransport(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            var hostControl = new UdpTransport(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            var viewerVideo = new UdpTransport(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            var viewerControl = new UdpTransport(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));

            var videoReceived = new TaskCompletionSource<AssembledVideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            var controlReceived = new TaskCompletionSource<(ControlEventType, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var host = MediaSession.Create(true, Secret(), SessionId(), 1,
                hostVideo, hostControl, viewerVideo.LocalEndPoint,
                _ => { }, (_, _) => { });
            await using var viewer = MediaSession.Create(false, Secret(), SessionId(), 1,
                viewerVideo, viewerControl, hostVideo.LocalEndPoint,
                frame => videoReceived.TrySetResult(frame),
                (type, body) => controlReceived.TrySetResult((type, body.ToArray())));

            host.SetPeers(viewerVideo.LocalEndPoint, viewerControl.LocalEndPoint);
            viewer.SetPeers(hostVideo.LocalEndPoint, hostControl.LocalEndPoint);
            host.Start();
            viewer.Start();

            byte[] encoded = new byte[4000];
            for (int i = 0; i < encoded.Length; i++)
            {
                encoded[i] = (byte)(i % 251);
            }

            await host.SendVideoFrameAsync(encoded, 77, 999, 3, VideoFrameFlags.KeyFrame, VideoCodec.H264, default);
            var completed = await Task.WhenAny(videoReceived.Task, Task.Delay(5000));
            Check("session video arrived", completed == videoReceived.Task, "frame must traverse both sockets");
            var frame = await videoReceived.Task;
            Check("session video intact", frame.Data.AsSpan().SequenceEqual(encoded) && frame.FrameId == 77,
                "decoded frame matches");

            Check("session control queued", host.TrySendControl(ControlEventType.Key, [0x1E, 0x00, 0x01]),
                "control enqueue");
            var controlDone = await Task.WhenAny(controlReceived.Task, Task.Delay(5000));
            Check("session control arrived", controlDone == controlReceived.Task, "event must arrive");
            var delivered = await controlReceived.Task;
            Check("session control intact", delivered.Item1 == ControlEventType.Key && delivered.Item2.Length == 3,
                "event body matches");

            Check("session counters", host.VideoDatagramsReceived == 0 && viewer.AssembledFrames == 1,
                "viewer assembled one frame");

            await host.DisposeAsync();
            await viewer.DisposeAsync();
            await hostVideo.DisposeAsync();
            await hostControl.DisposeAsync();
            await viewerVideo.DisposeAsync();
            await viewerControl.DisposeAsync();
        });

        RunAsync("media session: forged datagram is rejected, session survives", async () =>
        {
            var hostVideo = new UdpTransport(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            var hostControl = new UdpTransport(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            var viewerVideo = new UdpTransport(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            var viewerControl = new UdpTransport(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));

            bool gotFrame = false;
            await using var host = MediaSession.Create(true, Secret(), SessionId(), 1,
                hostVideo, hostControl, viewerVideo.LocalEndPoint, _ => { }, (_, _) => { });
            await using var viewer = MediaSession.Create(false, Secret(), SessionId(), 1,
                viewerVideo, viewerControl, hostVideo.LocalEndPoint, _ => gotFrame = true, (_, _) => { });
            host.SetPeers(viewerVideo.LocalEndPoint, viewerControl.LocalEndPoint);
            viewer.SetPeers(hostVideo.LocalEndPoint, hostControl.LocalEndPoint);
            host.Start();
            viewer.Start();

            // A datagram with the right magic but a broken tag must be dropped, not fatal.
            byte[] forged = new byte[64 + 32];
            forged[0] = 0x53; forged[1] = 0x43; forged[2] = 0x54; forged[3] = 0x31;
            forged[4] = 1;
            forged[5] = 1;
            forged[42] = 0x00; forged[43] = 0x40;
            await using (var attacker = new UdpTransport(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
            {
                await attacker.SendAsync(forged, viewerVideo.LocalEndPoint, default);
            }

            await Task.Delay(200);
            Check("session forged rejected", viewer.VideoDatagramsRejected >= 1, "tag mismatch counted");
            Check("session forged no frame", !gotFrame, "no frame emitted");

            byte[] real = new byte[100];
            await host.SendVideoFrameAsync(real, 1, 0, 1, VideoFrameFlags.KeyFrame, VideoCodec.H264, default);
            await Task.Delay(500);
            Check("session still alive", gotFrame, "session keeps working after a forged packet");

            await host.DisposeAsync();
            await viewer.DisposeAsync();
            await hostVideo.DisposeAsync();
            await hostControl.DisposeAsync();
            await viewerVideo.DisposeAsync();
            await viewerControl.DisposeAsync();
        });
    }
}

internal sealed class FakeClock
{
    private long _now;

    public long Now() => Interlocked.Read(ref _now);

    public void Advance(long microseconds) => Interlocked.Add(ref _now, microseconds);
}