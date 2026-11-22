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
/// <c>--live-smoke &lt;dir&gt;</c>: run a real host and a real viewer in one process over loopback,
/// so the live capture, encode, transport, decode and render path is exercised exactly as a session
/// would exercise it, and prove it by writing a decoded frame to disk.
/// </summary>
/// <remarks>
/// Both sides use the production session classes, not a test double. The only thing that differs
/// from two machines is that the addresses are 127.0.0.1 and the process happens to be shared.
/// </remarks>
public static class LiveSmoke
{
    private static readonly byte[] SharedSecret = Convert.FromHexString(
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

    private static readonly byte[] SessionId = Convert.FromHexString("A1A2A3A4A5A6A7A8A9AAABACADAEAFB0");

    public static int Run(string outputDirectory, int seconds)
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
            // No attached console.
        }

        if (seconds <= 0)
        {
            seconds = 8;
        }

        // A static desktop produces no DDA frames, so the smoke paints its own moving content: the
        // decoded picture then contains something unmistakable instead of a uniform background.
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
                    exitCode = RunCore(directory, seconds);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})");
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
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0x30, 0x60)),
        };
        var text = new System.Windows.Controls.TextBlock
        {
            Text = "ScreenCast live",
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 40,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        panel.Child = text;
        var window = new System.Windows.Window
        {
            Title = "ScreenCast live activity",
            Width = 520,
            Height = 360,
            Left = 120,
            Top = 120,
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
            window.Left = 120 + (tick % 400);
            window.Top = 120 + (tick % 260);
        };
        window.Closed += (_, _) => timer.Stop();
        timer.Start();
        return window;
    }

    private static int RunCore(string directory, int seconds)
    {
        var log = new StringBuilder();
        void Line(string message)
        {
            log.AppendLine(message);
            Console.WriteLine(message);
        }

        var report = new LiveSmokeReport();
        HostSession? host = null;
        ViewerSession? viewer = null;
        try
        {
            var displays = CaptureDiagnostics.EnumerateDisplays();
            var display = displays[0];
            report.Display = display.ToString();
            Line($"显示器：{display}");

            var settings = H264EncoderSettings.Default720p30;
            report.EncoderSettings = $"{settings.Width}x{settings.Height}@{settings.FramesPerSecond}fps {settings.BitrateBitsPerSecond}bps";

            viewer = new ViewerSession(SharedSecret, SessionId);
            host = new HostSession(display, settings, SharedSecret, SessionId);
            Line($"host   video={host.VideoPort} control={host.ControlPort} 接收缓冲={host.VideoReceiveBufferBytes}");
            Line($"viewer video={viewer.VideoPort} control={viewer.ControlPort}");

            var hostVideo = new IPEndPoint(IPAddress.Loopback, host.VideoPort);
            var hostControl = new IPEndPoint(IPAddress.Loopback, host.ControlPort);
            var viewerVideo = new IPEndPoint(IPAddress.Loopback, viewer.VideoPort);
            var viewerControl = new IPEndPoint(IPAddress.Loopback, viewer.ControlPort);

            int framesRendered = 0;
            int lastWidth = 0;
            int lastHeight = 0;
            byte[]? lastFrame = null;
            var frameLock = new object();

            viewer.FrameDecoded += (width, height, bgra) =>
            {
                lock (frameLock)
                {
                    framesRendered++;
                    lastWidth = width;
                    lastHeight = height;
                    lastFrame = bgra;
                }
            };

            viewer.Start(hostVideo, hostControl);
            host.Start(viewerVideo, viewerControl);

            // Give the session a moment to negotiate, then ask for a key frame so the viewer has a
            // complete reference to start from.
            Thread.Sleep(300);
            viewer.SendLease(true);
            viewer.RequestKeyFrame();

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(250);
                // Nudge the pointer so the desktop keeps changing and frames keep flowing.
                ushort x = (ushort)(ushort.MaxValue / 2 + (Math.Sin(clock.Elapsed.TotalSeconds) * 8000));
                ushort y = (ushort)(ushort.MaxValue / 2 + (Math.Cos(clock.Elapsed.TotalSeconds) * 8000));
                viewer.SendMouseMove(x, y);
            }

            var hostStats = host.Snapshot();
            var viewerStats = viewer.Snapshot();

            report.HostCaptured = hostStats.CapturedFrames;
            report.HostEncoded = hostStats.EncodedFrames;
            report.HostKeyFrames = hostStats.KeyFrames;
            report.HostSentBytes = hostStats.SentBytes;
            report.ViewerDecoded = viewerStats.DecodedFrames;
            report.ViewerRendered = framesRendered;
            report.ViewerBytes = viewerStats.ReceivedBytes;
            report.ViewerRejected = viewerStats.VideoDatagramsRejected;
            report.ViewerDroppedFrames = viewerStats.DroppedFrames;
            report.Fps = Math.Round(viewerStats.Fps, 1);
            report.DecodeRenderMs = Math.Round(viewerStats.DecodeRenderMs, 2);
            report.InputLease = hostStats.InputLeaseOwner;
            report.Width = lastWidth;
            report.Height = lastHeight;
            report.HostError = hostStats.LastError;
            report.ViewerError = viewerStats.LastError;

            Line($"host  ：采集 {hostStats.CapturedFrames} 帧，编码 {hostStats.EncodedFrames} 帧，" +
                $"关键帧 {hostStats.KeyFrames}，发送 {hostStats.SentBytes} 字节");
            Line($"viewer：解码 {viewerStats.DecodedFrames} 帧，渲染 {framesRendered} 帧，" +
                $"接收 {viewerStats.ReceivedBytes} 字节，拒绝 {viewerStats.VideoDatagramsRejected}，丢帧 {viewerStats.DroppedFrames}");
            Line($"viewer：{lastWidth}x{lastHeight}，约 {report.Fps} fps，解码渲染 {report.DecodeRenderMs} ms，输入租约={report.InputLease}");

            double stdDev = 0;
            double mean = 0;
            if (lastFrame is not null && lastWidth > 0 && lastHeight > 0)
            {
                (mean, stdDev) = BitmapWriter.Statistics(lastFrame, lastWidth, lastHeight);
                report.FrameMean = Math.Round(mean, 2);
                report.FrameStdDev = Math.Round(stdDev, 2);
                string path = Path.Combine(directory, "live-frame.bmp");
                BitmapWriter.WriteBgra(path, lastFrame, lastWidth, lastHeight);
                Line($"末帧画面：均值 {report.FrameMean}，标准差 {report.FrameStdDev} -> live-frame.bmp");
            }

            bool ok = hostStats.EncodedFrames > 0
                && viewerStats.DecodedFrames > 0
                && framesRendered > 0
                && lastWidth > 0
                && stdDev > 1.0
                && string.IsNullOrEmpty(hostStats.LastError)
                && string.IsNullOrEmpty(viewerStats.LastError);

            report.Status = ok ? "ok" : "failed";
            if (!ok)
            {
                report.Error = hostStats.EncodedFrames == 0 ? "主机未编码任何帧"
                    : viewerStats.DecodedFrames == 0 ? "查看端未解码任何帧"
                    : stdDev <= 1.0 ? "解码画面接近纯色"
                    : "存在错误";
            }

            Line($"结果：{report.Status}" + (report.Error is null ? string.Empty : $"（{report.Error}）"));
            Write(directory, report, log);
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            report.Status = "failed";
            report.Error = $"{ex.GetType().Name}: {ex.Message}";
            report.HResult = $"0x{ex.HResult:X8}";
            Line($"异常：{report.Error} ({report.HResult})");
            Write(directory, report, log);
            return 1;
        }
        finally
        {
            if (host is not null)
            {
                host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            if (viewer is not null)
            {
                viewer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private static void Write(string directory, LiveSmokeReport report, StringBuilder log)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, "live-smoke.log"), log.ToString());
            File.WriteAllText(Path.Combine(directory, "live-smoke-report.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // Reporting is best effort; the exit code still carries the result.
        }
    }

    public sealed class LiveSmokeReport
    {
        public string Status { get; set; } = "failed";
        public string? Error { get; set; }
        public string? HResult { get; set; }
        public string? Display { get; set; }
        public string? EncoderSettings { get; set; }
        public int HostCaptured { get; set; }
        public int HostEncoded { get; set; }
        public int HostKeyFrames { get; set; }
        public long HostSentBytes { get; set; }
        public int ViewerDecoded { get; set; }
        public int ViewerRendered { get; set; }
        public long ViewerBytes { get; set; }
        public long ViewerRejected { get; set; }
        public long ViewerDroppedFrames { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
        public double DecodeRenderMs { get; set; }
        public double FrameMean { get; set; }
        public double FrameStdDev { get; set; }
        public string? InputLease { get; set; }
        public string? HostError { get; set; }
        public string? ViewerError { get; set; }
    }
}