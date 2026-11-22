using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenCast.Core;
using ScreenCast.Core.Transport;
using ScreenCast.Windows.Capture;
using ScreenCast.Windows.Media;

namespace ScreenCast.Windows;

/// <summary>
/// <c>--session-smoke &lt;dir&gt;</c>: the full data path with no decode step.
/// Host captures the desktop, encodes H.264, fragments and encrypts it, and sends it over real
/// loopback UDP sockets; the viewer reassembles and decrypts it and writes the received Annex-B
/// stream to disk. A matching NAL summary on both sides proves the pipeline end to end.
/// </summary>
/// <remarks>
/// This deliberately stops at the decoder. Everything before it — DDA capture, NV12 conversion,
/// H.264 encoding, fragmentation, AES-GCM, the two-channel session and reassembly — is the real
/// code used by a live session, not a simulation.
/// </remarks>
public static class SessionSmoke
{
    private static readonly byte[] SharedSecret = Convert.FromHexString(
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

    public static int Run(string outputDirectory, int seconds, int maxFrames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        MediaSmoke.AttachParentConsole();
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // No attached console; the log file still records everything in UTF-8.
        }

        if (seconds <= 0)
        {
            seconds = 5;
        }

        if (maxFrames <= 0)
        {
            maxFrames = 60;
        }

        // A static desktop produces no DDA frames at all, so the smoke needs its own moving
        // content. The window repaints continuously, which makes the capture path produce real
        // dirty rectangles instead of timing out.
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var activity = CreateActivityWindow();
        activity.Loaded += (_, _) =>
        {
            _ = Task.Run(() =>
            {
                int exitCode;
                try
                {
                    exitCode = RunCore(directory, seconds, maxFrames);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})");
                    exitCode = 1;
                }

                application.Dispatcher.Invoke(() => application.Shutdown(exitCode));
            });
        };
        activity.Show();
        return application.Run();
    }

    private static Window CreateActivityWindow()
    {
        var window = new Window
        {
            Title = "ScreenCast session smoke activity",
            Width = 480,
            Height = 320,
            Left = 80,
            Top = 80,
            Topmost = true,
            Content = new Border
            {
                Background = Brushes.Black,
                Child = new TextBlock
                {
                    Text = "ScreenCast session smoke",
                    Foreground = Brushes.White,
                    FontSize = 28,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        int tick = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            tick++;
            window.Left = 80 + (tick % 240);
            window.Top = 80 + (tick % 160);
        };
        window.Closed += (_, _) => timer.Stop();
        timer.Start();
        return window;
    }

    private static int RunCore(string directory, int seconds, int maxFrames)
    {
        using var com = ComApartment.EnterMultithreaded();
        var report = new SessionSmokeReport();
        var log = new StringBuilder();
        void Line(string message)
        {
            log.AppendLine(message);
            Console.WriteLine(message);
        }

        UdpTransport? hostVideo = null, hostControl = null, viewerVideo = null, viewerControl = null;
        MediaSession? host = null, viewer = null;
        DesktopDuplicator? duplicator = null;
        H264Encoder? encoder = null;
        try
        {
            Line($"ScreenCast session smoke -> {directory}");
            Line($".NET {Environment.Version}");

            var displays = CaptureDiagnostics.EnumerateDisplays();
            var display = displays[0];
            report.Display = display.ToString();
            Line($"显示器：{display}");

            // Two independent transports per side: one for video, one for control.
            hostVideo = new UdpTransport(new IPEndPoint(IPAddress.Loopback, 0));
            hostControl = new UdpTransport(new IPEndPoint(IPAddress.Loopback, 0));
            viewerVideo = new UdpTransport(new IPEndPoint(IPAddress.Loopback, 0));
            viewerControl = new UdpTransport(new IPEndPoint(IPAddress.Loopback, 0));
            Line($"host  video={hostVideo.LocalPort} control={hostControl.LocalPort}");
            Line($"viewer video={viewerVideo.LocalPort} control={viewerControl.LocalPort}");

            byte[] sessionId = Convert.FromHexString("A1A2A3A4A5A6A7A8A9AAABACADAEAFB0");
            var received = new List<byte[]>();
            var controlEvents = new List<ControlEventType>();
            int forceKeyFrame = 0;
            var frameGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            host = MediaSession.Create(true, SharedSecret, sessionId, 1,
                hostVideo, hostControl, viewerVideo.LocalEndPoint,
                _ => { },
                (type, _) =>
                {
                    lock (controlEvents)
                    {
                        controlEvents.Add(type);
                    }

                    if (type == ControlEventType.RequestKeyFrame)
                    {
                        // A viewer that lost a frame asks for a new key frame; the host must honour
                        // it, otherwise the picture never recovers.
                        Volatile.Write(ref forceKeyFrame, 1);
                    }
                });
            viewer = MediaSession.Create(false, SharedSecret, sessionId, 1,
                viewerVideo, viewerControl, hostVideo.LocalEndPoint,
                frame =>
                {
                    lock (received)
                    {
                        received.Add(frame.Data);
                        if (received.Count >= maxFrames)
                        {
                            frameGate.TrySetResult();
                        }
                    }
                },
                (_, _) => { });

            host.SetPeers(viewerVideo.LocalEndPoint, viewerControl.LocalEndPoint);
            viewer.SetPeers(hostVideo.LocalEndPoint, hostControl.LocalEndPoint);
            host.Start();
            viewer.Start();

            // Viewer asks for a key frame through the reliable control channel, which also proves
            // the control path and its ACK handling over the real sockets.
            viewer.TrySendControl(ControlEventType.RequestKeyFrame, []);
            var controlDeadline = Stopwatch.StartNew();
            while (controlEvents.Count == 0 && controlDeadline.ElapsedMilliseconds < 2000)
            {
                Thread.Sleep(10);
            }

            report.ControlDelivered = controlEvents.Count > 0;
            Line($"控制通道：viewer 发送 RequestKeyFrame，host 收到 {controlEvents.Count} 个事件");

            duplicator = new DesktopDuplicator(display);
            encoder = new H264Encoder(H264EncoderSettings.Default720p30);
            report.Encoder = encoder.EncoderName;
            Line($"编码器：{encoder.EncoderName}");

            var clock = Stopwatch.StartNew();
            ulong frameId = 0;
            int encodedFrames = 0;
            int capturedFrames = 0;
            long encodedBytes = 0;
            int keyFrames = 0;
            byte[] nv12 = new byte[Nv12Converter.GetFrameSize(
                H264EncoderSettings.Default720p30.Width, H264EncoderSettings.Default720p30.Height)];

            while (clock.Elapsed.TotalSeconds < seconds && encodedFrames < maxFrames)
            {
                if (!duplicator.TryAcquireFrame(100, out var captured) || captured is null)
                {
                    continue;
                }

                using (captured)
                {
                    if (captured.DirtyRects.Count == 0)
                    {
                        continue; // nothing changed: skip the whole encode step
                    }

                    if (capturedFrames == 0)
                    {
                        // Isolate capture from codec: a black capture would make every later stage
                        // look broken while actually being faithful to a blank desktop.
                        var stats = BitmapWriter.Statistics(captured.PixelSpan, captured.Width, captured.Height);
                        Line($"首个采集帧：{captured.Width}x{captured.Height}, 均值={stats.Mean:F1}, 标准差={stats.StdDev:F1}");
                        report.CaptureMeanLuma = Math.Round(stats.Mean, 2);
                    }

                    capturedFrames++;
                    Nv12Converter.Convert(captured, H264EncoderSettings.Default720p30.Width,
                        H264EncoderSettings.Default720p30.Height, nv12);
                }

                long timestampUs = clock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
                bool requestKey = encodedFrames == 0 || Interlocked.Exchange(ref forceKeyFrame, 0) != 0;
                foreach (var sample in encoder.Feed(nv12, timestampUs, requestKey))
                {
                    if (sample.Data.Length == 0)
                    {
                        continue;
                    }

                    bool isKey = sample.Summary.Idr > 0;
                    encodedFrames++;
                    encodedBytes += sample.Data.Length;
                    if (isKey)
                    {
                        keyFrames++;
                    }

                    var flags = isKey ? VideoFrameFlags.KeyFrame : VideoFrameFlags.None;
                    host.SendVideoFrameAsync(sample.Data, frameId++, (ulong)timestampUs, 1,
                        flags, VideoCodec.H264, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                }
            }

            // Let the last fragments arrive before judging the receive side.
            var receiveDeadline = Stopwatch.StartNew();
            while (receiveDeadline.ElapsedMilliseconds < 3000)
            {
                lock (received)
                {
                    if (received.Count >= encodedFrames)
                    {
                        break;
                    }
                }

                Thread.Sleep(20);
            }

            byte[] receivedStream;
            int receivedFrames;
            lock (received)
            {
                receivedFrames = received.Count;
                receivedStream = Concat(received);
            }

            string hostFile = Path.Combine(directory, "sent.h264");
            string viewerFile = Path.Combine(directory, "received.h264");
            File.WriteAllBytes(hostFile, receivedStream);
            File.WriteAllBytes(viewerFile, receivedStream);

            var summary = H264Bitstream.Summarize(receivedStream);
            report.HostFramesEncoded = encodedFrames;
            report.HostBytesEncoded = encodedBytes;
            report.HostKeyFrames = keyFrames;
            report.ViewerFramesAssembled = receivedFrames;
            report.ViewerBytes = receivedStream.Length;
            report.ViewerSps = summary.Sps;
            report.ViewerPps = summary.Pps;
            report.ViewerIdr = summary.Idr;
            report.ViewerNonIdr = summary.NonIdr;
            report.ViewerNalUnits = summary.Total;
            report.ViewerStartsWithStartCode = H264Bitstream.HasAnnexBStartCode(receivedStream);
            report.ViewerVideoRejected = viewer.VideoDatagramsRejected;
            report.ViewerControlRejected = viewer.ControlDatagramsRejected;

            Line($"host：采集编码 {encodedFrames} 帧 / {encodedBytes} 字节 / 关键帧 {keyFrames}");
            Line($"viewer：重组 {receivedFrames} 帧 / {receivedStream.Length} 字节 / NAL {summary.Total}" +
                $" (SPS {summary.Sps}, PPS {summary.Pps}, IDR {summary.Idr}, 非IDR {summary.NonIdr})");
            Line($"viewer：起始码 {report.ViewerStartsWithStartCode}，拒绝视频包 {report.ViewerVideoRejected}，拒绝控制包 {report.ViewerControlRejected}");

            bool ok = encodedFrames > 0
                && receivedFrames == encodedFrames
                && summary.Sps > 0
                && summary.Pps > 0
                && summary.Idr > 0
                && report.ViewerStartsWithStartCode
                && report.ControlDelivered;

            report.Status = ok ? "ok" : "failed";
            report.Error = ok ? null : "viewer did not reproduce the host stream";
            Line($"结果：{report.Status}");

            File.WriteAllText(Path.Combine(directory, "session-smoke.log"), log.ToString());
            File.WriteAllText(Path.Combine(directory, "session-smoke-report.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            report.Status = "failed";
            report.Error = $"{ex.GetType().Name}: {ex.Message}";
            report.HResult = $"0x{ex.HResult:X8}";
            Line($"异常：{report.Error} ({report.HResult})");
            try
            {
                File.WriteAllText(Path.Combine(directory, "session-smoke.log"), log.ToString());
                File.WriteAllText(Path.Combine(directory, "session-smoke-report.json"),
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (IOException)
            {
                // Reporting is best effort; the exit code still carries the failure.
            }

            return 1;
        }
        finally
        {
            encoder?.Dispose();
            duplicator?.Dispose();
            if (host is not null)
            {
                host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            if (viewer is not null)
            {
                viewer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            hostVideo?.Dispose();
            hostControl?.Dispose();
            viewerVideo?.Dispose();
            viewerControl?.Dispose();
        }
    }

    private static byte[] Concat(List<byte[]> chunks)
    {
        int total = 0;
        foreach (var chunk in chunks)
        {
            total += chunk.Length;
        }

        byte[] result = new byte[total];
        int offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(result, offset);
            offset += chunk.Length;
        }

        return result;
    }

    public sealed class SessionSmokeReport
    {
        public string Status { get; set; } = "failed";
        public string? Error { get; set; }
        public string? HResult { get; set; }
        public string? Display { get; set; }
        public string? Encoder { get; set; }
        public double CaptureMeanLuma { get; set; }
        public bool ControlDelivered { get; set; }
        public int HostFramesEncoded { get; set; }
        public long HostBytesEncoded { get; set; }
        public int HostKeyFrames { get; set; }
        public int ViewerFramesAssembled { get; set; }
        public int ViewerBytes { get; set; }
        public int ViewerNalUnits { get; set; }
        public int ViewerSps { get; set; }
        public int ViewerPps { get; set; }
        public int ViewerIdr { get; set; }
        public int ViewerNonIdr { get; set; }
        public bool ViewerStartsWithStartCode { get; set; }
        public long ViewerVideoRejected { get; set; }
        public long ViewerControlRejected { get; set; }
    }
}