using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenCast.Windows.Capture;
using ScreenCast.Windows.Media;

namespace ScreenCast.Windows;

/// <summary>
/// <c>--media-smoke &lt;dir&gt;</c>: real continuous DDA capture, NV12 conversion and Media Foundation
/// H.264 encoding for a few seconds, then an Annex-B NAL check of the written stream.
/// Exit code 0 means a non-empty, verifiable H.264 stream was produced.
/// </summary>
public static class MediaSmoke
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    public static int Run(string outputDirectory, int seconds, int maxFrames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        AttachParentConsole();

        var log = new SmokeLog(directory);
        log.Line($"ScreenCast media smoke -> {directory}");
        log.Line($".NET {Environment.Version}, OS {Environment.OSVersion.VersionString}");

        if (seconds <= 0)
            seconds = 5;
        if (maxFrames <= 0)
            maxFrames = 60;

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var activity = CreateActivityWindow();
        activity.Loaded += (_, _) =>
        {
            _ = Task.Run(async () =>
            {
                var exitCode = 1;
                try
                {
                    exitCode = await RunSmokeAsync(directory, seconds, maxFrames, log).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.Line($"FAILED: {ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})");
                    WriteReport(directory, new SmokeReport
                    {
                        Status = "failed",
                        Timestamp = DateTimeOffset.Now.ToString("O"),
                        ExitCode = 1,
                        Error = $"{ex.GetType().Name}: {ex.Message}",
                        HResult = $"0x{ex.HResult:X8}",
                    });
                }
                finally
                {
                    application.Dispatcher.Invoke(() => application.Shutdown(exitCode));
                }
            });
        };

        activity.Show();
        return application.Run();
    }

    private static async Task<int> RunSmokeAsync(string directory, int seconds, int maxFrames, SmokeLog log)
    {
        using var com = ComApartment.EnterMultithreaded();
        var h264Path = Path.Combine(directory, "smoke.h264");
        var settings = H264EncoderSettings.Default720p30;
        var report = new SmokeReport
        {
            Status = "running",
            Timestamp = DateTimeOffset.Now.ToString("O"),
            H264File = h264Path,
            EncoderSettings = $"{settings.Width}x{settings.Height}@{settings.FramesPerSecond}fps, {settings.BitrateBitsPerSecond} bps, GOP {settings.KeyframeInterval}",
        };

        DisplayInfo display;
        try
        {
            var displays = CaptureDiagnostics.EnumerateDisplays();
            display = displays[0];
        }
        catch (Exception ex)
        {
            log.Line($"显示器枚举失败：{ex.Message} (HRESULT 0x{ex.HResult:X8})");
            report.Status = "failed";
            report.Error = ex.Message;
            report.HResult = $"0x{ex.HResult:X8}";
            report.ExitCode = 1;
            WriteReport(directory, report);
            return 1;
        }

        report.Display = $"{display.DeviceName} {display.Width}x{display.Height} @ ({display.Left},{display.Top}), {display.Rotation}";
        report.Adapter = $"#{display.AdapterIndex} {display.AdapterName}";
        log.Line($"显示器：{report.Display}");
        log.Line($"适配器：{report.Adapter}");

        var hardware = MediaFoundationProbe.EnumerateH264EncoderMfts(
            MediaFoundationProbe.MftEnumHardware | MediaFoundationProbe.MftEnumSortAndFilter);
        var all = MediaFoundationProbe.EnumerateH264EncoderMfts(MediaFoundationProbe.MftEnumSortAndFilter);
        report.Probe = new ProbeReport
        {
            HardwareMfts = hardware.Select(m => Describe(m)).ToArray(),
            SoftwareMfts = all.Select(m => Describe(m)).ToArray(),
        };
        log.Line($"硬件 H.264 编码器 MFT：{(hardware.Count == 0 ? "无（驱动未注册 MF 硬件编码器）" : string.Join(" | ", hardware.Select(Describe)))}");
        log.Line($"全部 H.264 编码器 MFT：{(all.Count == 0 ? "无" : string.Join(" | ", all.Select(Describe)))}");

        var h264Samples = new List<long>();
        long framesCaptured = 0, staticSkips = 0, throttleSkips = 0, encodedFrames = 0, totalBytes = 0, keyFrames = 0;
        long firstTimestampUs = -1;
        var lastEncodedUs = -1L;
        var convertedFromAvcc = false;
        var captureClock = Stopwatch.StartNew();
        DdaStats captureStats;

        using (var capture = new DesktopDuplicator(display))
        {
            var surfaceBufferOk = MediaFoundationProbe.TryCreateSurfaceBuffer(capture.Device, out var surfaceDetail);
            report.Probe.DxgiSurfaceBuffer = surfaceBufferOk;
            report.Probe.DxgiSurfaceBufferDetail = surfaceDetail;
            log.Line($"D3D11→IMFMediaBuffer 探测：{(surfaceBufferOk ? "可用" : "不可用")} — {surfaceDetail}");

            using var encoder = new H264Encoder(settings);
            log.Line($"编码器：{encoder.EncoderName}，输出样本由 MFT 提供：{encoder.OutputProvidesSamples}，序列头 {encoder.SequenceHeaderLength} 字节");
            log.Line($"媒体类型协商：{string.Join(" | ", encoder.AttemptLog)}");

            var nv12 = new byte[settings.Nv12FrameSize];
            var encodeIntervalMs = 1000.0 / settings.FramesPerSecond;
            var nextEncodeMs = 0.0;
            var loopClock = Stopwatch.StartNew();

            await using (var file = new FileStream(h264Path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, useAsync: true))
            {
                while (loopClock.Elapsed.TotalSeconds < seconds && encodedFrames < maxFrames)
                {
                    if (!capture.TryAcquireFrame(100, out var frame) || frame is null)
                        continue;

                    using (frame)
                    {
                        framesCaptured++;
                        var nowMs = loopClock.Elapsed.TotalMilliseconds;
                        if (nowMs < nextEncodeMs)
                        {
                            throttleSkips++;
                            continue;
                        }

                        if (frame.IsStaticChange)
                        {
                            staticSkips++;
                            continue;
                        }

                        Nv12Converter.Convert(
                            frame.PixelSpan, frame.Width, frame.Height, frame.Stride, frame.IsBgra,
                            nv12, settings.Width, settings.Height);

                        var timestampUs = Math.Max(frame.TimestampUs, lastEncodedUs + 1);
                        if (firstTimestampUs < 0)
                            firstTimestampUs = timestampUs;

                        var requestKeyFrame = encodedFrames == 0
                            || (settings.KeyframeInterval > 0 && encodedFrames % settings.KeyframeInterval == 0);
                        var produced = encoder.Feed(nv12, timestampUs, requestKeyFrame);
                        foreach (var sample in produced)
                        {
                            await file.WriteAsync(sample.Data).ConfigureAwait(false);
                            h264Samples.Add(sample.TimestampUs);
                            totalBytes += sample.ByteLength;
                            if (sample.IsKeyFrame)
                                keyFrames++;
                            convertedFromAvcc |= sample.ConvertedFromAvcc;
                        }

                        lastEncodedUs = timestampUs;
                        encodedFrames++;
                        nextEncodeMs = nowMs + encodeIntervalMs;
                    }
                }

                foreach (var sample in encoder.Drain())
                {
                    await file.WriteAsync(sample.Data).ConfigureAwait(false);
                    h264Samples.Add(sample.TimestampUs);
                    totalBytes += sample.ByteLength;
                    if (sample.IsKeyFrame)
                        keyFrames++;
                    convertedFromAvcc |= sample.ConvertedFromAvcc;
                }

                await file.FlushAsync().ConfigureAwait(false);
            }

            captureStats = capture.Stats;
            report.Encode = new EncodeReport
            {
                Encoder = encoder.EncoderName,
                SequenceHeaderLength = encoder.SequenceHeaderLength,
                OutputProvidesSamples = encoder.OutputProvidesSamples,
                Frames = encodedFrames,
                KeyFrames = keyFrames,
                Bytes = totalBytes,
                Seconds = Math.Round(captureClock.Elapsed.TotalSeconds, 3),
                AverageBitrate = captureClock.Elapsed.TotalSeconds > 0
                    ? Math.Round(totalBytes * 8 / captureClock.Elapsed.TotalSeconds, 0)
                    : 0,
                FirstTimestampUs = firstTimestampUs,
                ConvertedFromAvcc = convertedFromAvcc,
            };
        }

        report.Capture = new CaptureReport
        {
            Frames = framesCaptured,
            Timeouts = captureStats.Timeouts,
            Rebuilds = captureStats.Rebuilds,
            SizeChanges = captureStats.SizeChanges,
            ReleaseFailures = captureStats.ReleaseFailures,
            StaticSkips = staticSkips,
            ThrottleSkips = throttleSkips,
            DirtyRects = captureStats.DirtyRectCount,
            MoveRects = captureStats.MoveRectCount,
            DirtyAreaPixels = captureStats.DirtyAreaPixels,
        };

        var streamBytes = await File.ReadAllBytesAsync(h264Path).ConfigureAwait(false);
        var summary = H264Bitstream.Summarize(streamBytes);
        var startsWithStartCode = streamBytes.Length > 0 && H264Bitstream.HasAnnexBStartCode(streamBytes);
        var monotonic = true;
        for (var i = 1; i < h264Samples.Count; i++)
        {
            if (h264Samples[i] <= h264Samples[i - 1])
            {
                monotonic = false;
                break;
            }
        }

        report.Stream = new StreamReport
        {
            Bytes = streamBytes.Length,
            StartsWithStartCode = startsWithStartCode,
            TimestampsMonotonic = monotonic,
            NalUnits = summary.Total,
            Sps = summary.Sps,
            Pps = summary.Pps,
            Idr = summary.Idr,
            NonIdr = summary.NonIdr,
            Sei = summary.Sei,
            Other = summary.Other,
            FirstSampleTimestampsUs = h264Samples.Take(8).ToArray(),
        };

        var success = streamBytes.Length > 0
            && startsWithStartCode
            && summary.Sps > 0
            && summary.Pps > 0
            && summary.Idr > 0
            && encodedFrames > 0
            && totalBytes > 0;
        report.Status = success ? "ok" : "failed";
        report.ExitCode = success ? 0 : 1;
        if (!success)
            report.Error = "H.264 流未通过 Annex-B / NAL 校验（需要起始码 + SPS + PPS + IDR）。";

        WriteReport(directory, report);
        log.Line($"采集帧 {framesCaptured}，编码帧 {encodedFrames}，关键帧 {keyFrames}，输出 {totalBytes} 字节，平均 {report.Encode.AverageBitrate} bps，耗时 {report.Encode.Seconds}s");
        log.Line($"采集统计：超时 {captureStats.Timeouts}，重建 {captureStats.Rebuilds}，尺寸变化 {captureStats.SizeChanges}，静止跳过 {staticSkips}，节流跳过 {throttleSkips}，脏矩形 {captureStats.DirtyRectCount}");
        log.Line($"首帧时间戳 {firstTimestampUs} us；NAL 统计：总数 {summary.Total}，SPS {summary.Sps}，PPS {summary.Pps}，IDR {summary.Idr}，非 IDR {summary.NonIdr}，SEI {summary.Sei}，其他 {summary.Other}，起始码 {startsWithStartCode}，AVCC→Annex-B {convertedFromAvcc}，时间戳单调 {monotonic}");
        log.Line($"结果：{report.Status}（exit {report.ExitCode}），文件 {h264Path}（{streamBytes.Length} 字节）");
        if (report.Error is not null)
            log.Line($"原因：{report.Error}");

        return report.ExitCode;
    }

    private static string Describe(MftInfo info)
    {
        var flags = new List<string>();
        if (info.Async)
            flags.Add("async");
        if (info.D3D11Aware)
            flags.Add("d3d11");
        return flags.Count == 0 ? info.FriendlyName : $"{info.FriendlyName} [{string.Join(",", flags)}]";
    }

    private static void WriteReport(string directory, SmokeReport report)
    {
        try
        {
            var path = Path.Combine(directory, "media-smoke-report.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // The log already carries the numbers; a report write failure must not change the verdict.
        }
    }

    public static void AttachParentConsole()
    {
        try
        {
            if (!AttachConsole(AttachParentProcess))
                return;
            var stdout = Console.OpenStandardOutput();
            var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(writer);
            var stderr = Console.OpenStandardError();
            var errorWriter = new StreamWriter(stderr, new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetError(errorWriter);
        }
        catch (IOException)
        {
            // No console available; the log/report files still record everything.
        }
    }

    private static Window CreateActivityWindow()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x30, 0x80, 0xC0));
        var window = new Window
        {
            Title = "ScreenCast media smoke activity",
            Width = 380,
            Height = 200,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            Left = 80,
            Top = 80,
        };

        var label = new TextBlock
        {
            Text = "ScreenCast\nmedia smoke activity",
            Foreground = Brushes.White,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        window.Content = new Border
        {
            CornerRadius = new CornerRadius(24),
            Background = brush,
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(3),
            Child = label,
        };

        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        var phase = 0.0;
        timer.Tick += (_, _) =>
        {
            phase += 0.09;
            var area = SystemParameters.WorkArea;
            var spanX = Math.Max(1, area.Width - window.Width - 160);
            var spanY = Math.Max(1, area.Height - window.Height - 160);
            window.Left = 80 + ((Math.Sin(phase) * 0.5 + 0.5) * spanX);
            window.Top = 80 + ((Math.Cos(phase * 0.7) * 0.5 + 0.5) * spanY);
            brush.Color = Color.FromRgb(
                (byte)(90 + (80 * ((Math.Sin(phase * 1.7) * 0.5) + 0.5))),
                (byte)(90 + (80 * ((Math.Cos(phase * 1.3) * 0.5) + 0.5))),
                (byte)(170 + (70 * ((Math.Sin(phase * 0.6) * 0.5) + 0.5))));
        };
        window.Closed += (_, _) => timer.Stop();
        timer.Start();
        return window;
    }

    private sealed class SmokeLog(string directory)
    {
        private readonly object _gate = new();
        private readonly string _path = Path.Combine(directory, "media-smoke.log");

        public void Line(string message)
        {
            lock (_gate)
            {
                try
                {
                    Console.WriteLine(message);
                }
                catch (IOException)
                {
                    // Console may be detached (WinExe without an attached console).
                }

                try
                {
                    File.AppendAllText(_path, message + Environment.NewLine);
                }
                catch (IOException)
                {
                    // Log file failures must not abort a capture run.
                }
            }
        }
    }

    private sealed class SmokeReport
    {
        public string Status { get; set; } = "running";

        public string Timestamp { get; set; } = string.Empty;

        public int ExitCode { get; set; }

        public string? Error { get; set; }

        public string? HResult { get; set; }

        public string? Adapter { get; set; }

        public string? Display { get; set; }

        public string? H264File { get; set; }

        public string? EncoderSettings { get; set; }

        public CaptureReport? Capture { get; set; }

        public EncodeReport? Encode { get; set; }

        public StreamReport? Stream { get; set; }

        public ProbeReport? Probe { get; set; }
    }

    private sealed class CaptureReport
    {
        public long Frames { get; set; }

        public long Timeouts { get; set; }

        public long Rebuilds { get; set; }

        public long SizeChanges { get; set; }

        public long ReleaseFailures { get; set; }

        public long StaticSkips { get; set; }

        public long ThrottleSkips { get; set; }

        public long DirtyRects { get; set; }

        public long MoveRects { get; set; }

        public long DirtyAreaPixels { get; set; }
    }

    private sealed class EncodeReport
    {
        public string Encoder { get; set; } = string.Empty;

        public int SequenceHeaderLength { get; set; }

        public bool OutputProvidesSamples { get; set; }

        public long Frames { get; set; }

        public long KeyFrames { get; set; }

        public long Bytes { get; set; }

        public double Seconds { get; set; }

        public double AverageBitrate { get; set; }

        public long FirstTimestampUs { get; set; }

        public bool ConvertedFromAvcc { get; set; }
    }

    private sealed class StreamReport
    {
        public long Bytes { get; set; }

        public bool StartsWithStartCode { get; set; }

        public bool TimestampsMonotonic { get; set; }

        public int NalUnits { get; set; }

        public int Sps { get; set; }

        public int Pps { get; set; }

        public int Idr { get; set; }

        public int NonIdr { get; set; }

        public int Sei { get; set; }

        public int Other { get; set; }

        public long[] FirstSampleTimestampsUs { get; set; } = [];
    }

    private sealed class ProbeReport
    {
        public string[] HardwareMfts { get; set; } = [];

        public string[] SoftwareMfts { get; set; } = [];

        public bool DxgiSurfaceBuffer { get; set; }

        public string DxgiSurfaceBufferDetail { get; set; } = string.Empty;
    }
}
