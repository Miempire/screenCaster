using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using ScreenCast.Windows.Capture;
using ScreenCast.Windows.Media;
using ScreenCast.Windows.Session;

namespace ScreenCast.Windows;

/// <summary>
/// Headless host and viewer entry points, so two separate processes can be paired on loopback (or
/// across two machines) without a window and without a signalling server.
/// </summary>
/// <remarks>
/// <para>
/// <c>--host-listen &lt;videoPort&gt; &lt;controlPort&gt; &lt;seconds&gt; &lt;dir&gt;</c> binds the
/// host on fixed ports and waits for a viewer to send the first datagram, then streams to that
/// address. Learning the peer from the first authenticated datagram is exactly what a signalling
/// server would otherwise provide.
/// </para>
/// <para>
/// <c>--viewer-connect &lt;host&gt; &lt;videoPort&gt; &lt;controlPort&gt; &lt;seconds&gt; &lt;dir&gt;</c>
/// connects, renders into memory and writes the last frame plus a report.
/// </para>
/// </remarks>
public static class CrossProcessSmoke
{
    private static readonly byte[] SharedSecret = Convert.FromHexString(
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

    private static readonly byte[] SessionId = Convert.FromHexString("A1A2A3A4A5A6A7A8A9AAABACADAEAFB0");

    public static int RunHost(int videoPort, int controlPort, int peerVideoPort, int seconds, string outputDirectory)
    {
        var directory = Prepare(outputDirectory);

        // A static desktop produces no DDA frames, so the host paints its own moving content and
        // the received picture is unmistakably this session's output rather than a blank screen.
        var application = new System.Windows.Application
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
        };
        var activity = CreateActivityWindow();
        activity.Loaded += (_, _) =>
        {
            _ = Task.Run(() =>
            {
                int exitCode;
                try
                {
                    exitCode = HostCore(videoPort, controlPort, peerVideoPort, seconds, directory);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
                    exitCode = 1;
                }

                application.Dispatcher.Invoke(() => application.Shutdown(exitCode));
            });
        };
        activity.Show();
        return application.Run();
    }

    private static System.Windows.Window CreateActivityWindow()
    {
        var panel = new System.Windows.Controls.Border
        {
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x20, 0x60, 0x40)),
        };
        panel.Child = new System.Windows.Controls.TextBlock
        {
            Text = "ScreenCast host",
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 40,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        var window = new System.Windows.Window
        {
            Title = "ScreenCast host activity",
            Width = 520,
            Height = 360,
            Left = 150,
            Top = 150,
            Topmost = true,
            Content = panel,
        };

        int tick = 0;
        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        timer.Tick += (_, _) =>
        {
            tick++;
            window.Left = 150 + (tick % 380);
            window.Top = 150 + (tick % 240);
        };
        window.Closed += (_, _) => timer.Stop();
        timer.Start();
        return window;
    }

    private static int HostCore(int videoPort, int controlPort, int peerVideoPort, int seconds, string directory)
    {
        var log = new StringBuilder();
        void Line(string message)
        {
            log.AppendLine(message);
            Console.WriteLine(message);
        }

        HostSession? host = null;
        try
        {
            var displays = CaptureDiagnostics.EnumerateDisplays();
            Line($"显示器：{displays[0]}");

            // The peer address is unknown until the viewer speaks; the host is created against the
            // fixed local ports and then pointed at the observed address. The viewer's video port is
            // supplied explicitly because a viewer never sends video, so it cannot be observed.
            host = new HostSession(
                displays[0],
                H264EncoderSettings.Default720p30,
                SharedSecret,
                SessionId,
                new IPEndPoint(IPAddress.Any, videoPort),
                new IPEndPoint(IPAddress.Any, controlPort));
            Line($"host 监听 视频={host.VideoPort} 控制={host.ControlPort}，预期对端视频端口 {peerVideoPort}");

            var (observedVideo, observedControl) = host.WaitForPeerSources(TimeSpan.FromSeconds(Math.Max(5, seconds)), Line);
            if (observedControl is null)
            {
                Line("未等到对端数据报。");
                Write(directory, new { status = "failed", error = "no peer datagram" }, Line);
                return 1;
            }

            var controlPeer = observedControl;
            var videoPeer = observedVideo ?? new IPEndPoint(controlPeer.Address, peerVideoPort);
            Line($"对端地址：视频 {videoPeer}，控制 {controlPeer}");
            host.Start(videoPeer, controlPeer);
            Line($"host 开始推流");

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(500);
            }

            var stats = host.Snapshot();
            Line($"host 采集 {stats.CapturedFrames}，编码 {stats.EncodedFrames}，关键帧 {stats.KeyFrames}，发送 {stats.SentBytes} 字节");
            bool ok = stats.EncodedFrames > 0 && string.IsNullOrEmpty(stats.LastError);
            Write(directory, new
            {
                status = ok ? "ok" : "failed",
                role = "host",
                videoPeer = videoPeer.ToString(),
                controlPeer = controlPeer.ToString(),
                videoPort = host.VideoPort,
                controlPort = host.ControlPort,
                captured = stats.CapturedFrames,
                encoded = stats.EncodedFrames,
                keyFrames = stats.KeyFrames,
                sentBytes = stats.SentBytes,
                error = stats.LastError,
            }, Line);
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Line($"异常：{ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})");
            Write(directory, new { status = "failed", error = ex.Message }, Line);
            return 1;
        }
        finally
        {
            host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static int RunViewer(string hostAddress, int videoPort, int controlPort,
        int localVideoPort, int localControlPort, int seconds, string outputDirectory)
    {
        var directory = Prepare(outputDirectory);
        var log = new StringBuilder();
        void Line(string message)
        {
            log.AppendLine(message);
            Console.WriteLine(message);
        }

        ViewerSession? viewer = null;
        try
        {
            if (!IPAddress.TryParse(hostAddress, out var address))
            {
                Line($"地址无效：{hostAddress}");
                return 1;
            }

            int rendered = 0;
            int width = 0;
            int height = 0;
            byte[]? last = null;
            var gate = new object();

            // Fixed local ports so the host can be told where to send video before any traffic flows.
            viewer = new ViewerSession(SharedSecret, SessionId,
                new IPEndPoint(IPAddress.Any, localVideoPort),
                new IPEndPoint(IPAddress.Any, localControlPort));
            viewer.FrameDecoded += (w, h, bgra) =>
            {
                lock (gate)
                {
                    rendered++;
                    width = w;
                    height = h;
                    last = bgra;
                }
            };

            Line($"viewer 本机 视频={viewer.VideoPort} 控制={viewer.ControlPort}，连接 {address}:{videoPort}/{controlPort}");
            viewer.Start(new IPEndPoint(address, videoPort), new IPEndPoint(address, controlPort));
            viewer.SendLease(true);
            viewer.RequestKeyFrame();

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(300);
                viewer.SendMouseMove((ushort)(ushort.MaxValue / 2), (ushort)(ushort.MaxValue / 2));
            }

            var stats = viewer.Snapshot();
            double mean = 0;
            double stdDev = 0;
            if (last is not null && width > 0 && height > 0)
            {
                (mean, stdDev) = BitmapWriter.Statistics(last, width, height);
                BitmapWriter.WriteBgra(Path.Combine(directory, "viewer-frame.bmp"), last, width, height);
            }

            Line($"viewer 解码 {stats.DecodedFrames}，渲染 {rendered}，接收 {stats.ReceivedBytes} 字节，" +
                $"拒绝 {stats.VideoDatagramsRejected}，丢帧 {stats.DroppedFrames}，{width}x{height}，{stats.Fps:F1} fps");
            Line($"末帧 均值 {mean:F2} 标准差 {stdDev:F2}");

            bool ok = stats.DecodedFrames > 0 && rendered > 0 && stdDev > 1.0;
            Write(directory, new
            {
                status = ok ? "ok" : "failed",
                role = "viewer",
                host = $"{address}:{videoPort}",
                decoded = stats.DecodedFrames,
                rendered,
                receivedBytes = stats.ReceivedBytes,
                rejected = stats.VideoDatagramsRejected,
                dropped = stats.DroppedFrames,
                width,
                height,
                fps = Math.Round(stats.Fps, 1),
                frameMean = Math.Round(mean, 2),
                frameStdDev = Math.Round(stdDev, 2),
                error = stats.LastError,
            }, Line);
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Line($"异常：{ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})");
            Write(directory, new { status = "failed", error = ex.Message }, Line);
            return 1;
        }
        finally
        {
            viewer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static string Prepare(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        MediaSmoke.AttachParentConsole();
        return directory;
    }

    private static void Write(string directory, object report, Action<string> line)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, "cross-process-report.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            line("报告写入失败");
        }
    }

    /// <summary>Append the collected log lines to a file at the end of a run.</summary>
    public static void FlushLog(string directory, StringBuilder log)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, "cross-process.log"), log.ToString());
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
