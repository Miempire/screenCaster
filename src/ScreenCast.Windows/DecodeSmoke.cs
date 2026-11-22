using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using ScreenCast.Windows.Media;

namespace ScreenCast.Windows;

/// <summary>
/// <c>--decode-smoke &lt;dir&gt;</c>: decodes a recorded Annex-B stream and writes the first few
/// pictures as BMP, so the decoder output can be inspected instead of trusted.
/// </summary>
/// <remarks>
/// The input is either an explicit <c>.h264</c> path given with <c>--decode-input</c> or the
/// <c>received.h264</c> produced by <c>--session-smoke</c>. Exit code 0 requires at least one
/// decoded picture with a non-blank histogram.
/// </remarks>
public static class DecodeSmoke
{
    public static int Run(string outputDirectory, string? inputFile, int maxPictures)
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

        if (maxPictures <= 0)
        {
            maxPictures = 6;
        }

        var log = new StringBuilder();
        void Line(string message)
        {
            log.AppendLine(message);
            Console.WriteLine(message);
        }

        var report = new DecodeSmokeReport { Input = inputFile };
        H264Decoder? decoder = null;
        try
        {
            string input = ResolveInput(inputFile, directory);
            report.Input = input;
            byte[] stream = File.ReadAllBytes(input);
            Line($"解码冒烟 -> {directory}");
            Line($"输入：{input} ({stream.Length} 字节)");
            report.InputBytes = stream.Length;

            var accessUnits = SplitAccessUnits(stream);
            Line($"访问单元：{accessUnits.Count} 个");
            report.AccessUnits = accessUnits.Count;
            if (accessUnits.Count == 0)
            {
                report.Status = "failed";
                report.Error = "输入中未找到 H.264 访问单元";
                Write(directory, report, log);
                return 1;
            }

            // The Source Reader owns parsing and type negotiation, but it needs a container format:
            // Windows has no byte-stream handler for a bare .h264 elementary stream. It is therefore
            // opt-in, and the hand-wired MFT path is the default for an Annex-B capture.
            if (Environment.GetEnvironmentVariable("SC_SOURCE_READER") == "1")
            {
                return RunWithSourceReader(directory, input, maxPictures, report, log, Line, Write);
            }

            using var decoderLocal = new H264Decoder();
            decoder = decoderLocal;
            if (Environment.GetEnvironmentVariable("SC_NO_SEQ_HEADER") == "1")
            {
                decoderLocal.SequenceHeaderMode = SequenceHeaderFormat.None;
                Line("序列头：不使用属性（诊断开关）");
            }
            else if (Environment.GetEnvironmentVariable("SC_ANNEXB_SEQ_HEADER") == "1")
            {
                decoderLocal.SequenceHeaderMode = SequenceHeaderFormat.AnnexB;
                Line("序列头：Annex-B（诊断开关）");
            }

            if (Environment.GetEnvironmentVariable("SC_ANNEXB_FEED") == "1")
            {
                decoderLocal.FeedLengthPrefixed = false;
                Line("样本使用 Annex-B 起始码（诊断开关）");
            }
            Line($"解码器：{decoderLocal.DecoderName}，硬件加速：{decoderLocal.HardwareAccelerated}");
            foreach (var entry in decoderLocal.AttemptLog)
            {
                Line($"  {entry}");
            }

            report.Decoder = decoderLocal.DecoderName;
            report.HardwareAccelerated = decoderLocal.HardwareAccelerated;

            var clock = Stopwatch.StartNew();
            int decoded = 0;
            int written = 0;
            double brightestStdDev = 0;
            bool wholeStream = Environment.GetEnvironmentVariable("SC_WHOLE_STREAM") == "1";
            int iterations = wholeStream ? 1 : accessUnits.Count;
            if (wholeStream)
            {
                Line("整段码流单次喂入（诊断开关）");
            }

            for (int index = 0; index < iterations; index++)
            {
                long timestampUs = index * 33_333;
                var unit = wholeStream ? stream : accessUnits[index];
                foreach (var frame in decoder.Decode(unit, timestampUs))
                {
                    decoded++;
                    if (written >= maxPictures)
                    {
                        continue;
                    }

                    byte[] bgra = new byte[Nv12ToBgra.BgraSize(frame.Width, frame.Height)];
                    frame.ToBgra(bgra);
                    var (mean, stdDev) = BitmapWriter.Statistics(bgra, frame.Width, frame.Height);
                    brightestStdDev = Math.Max(brightestStdDev, stdDev);

                    // Raw plane statistics: separates "decoder produced black" from "conversion is wrong".
                    long lumaSum = 0;
                    int lumaCount = Math.Min(frame.Width * frame.Height, frame.Nv12.Length);
                    for (int i = 0; i < lumaCount; i++)
                    {
                        lumaSum += frame.Nv12[i];
                    }

                    double rawLumaMean = lumaCount > 0 ? (double)lumaSum / lumaCount : 0;
                    if (written == 0)
                    {
                        Line($"  原始 NV12：{frame.Nv12.Length} 字节, Y 平面均值={rawLumaMean:F1}, " +
                            $"YStride={frame.YStride}, UVStride={frame.UvStride}");
                    }
                    string path = Path.Combine(directory, $"frame-{written:D3}.bmp");
                    BitmapWriter.WriteBgra(path, bgra, frame.Width, frame.Height);
                    Line($"  帧 {written}: {frame.Width}x{frame.Height}, YStride={frame.YStride}, " +
                        $"UVStride={frame.UvStride}, 均值={mean:F1}, 标准差={stdDev:F1} -> {Path.GetFileName(path)}");
                    written++;
                }
            }

            report.DecodedPictures = decoded;
            report.WrittenBitmaps = written;
            report.OutputWidth = decoderLocal.OutputWidth;
            report.OutputHeight = decoderLocal.OutputHeight;
            report.BrightestStdDev = Math.Round(brightestStdDev, 2);
            report.ElapsedMs = (int)clock.ElapsedMilliseconds;
            report.AttemptLog = [.. decoderLocal.AttemptLog];
            foreach (var entry in decoderLocal.AttemptLog)
            {
                Line($"  {entry}");
            }

            bool ok = decoded > 0 && written > 0 && brightestStdDev > 1.0;
            report.Status = ok ? "ok" : "failed";
            if (!ok)
            {
                report.Error = decoded == 0
                    ? "解码器未输出任何画面"
                    : "解码画面接近纯色，可能是色彩转换或步幅错误";
            }

            Line($"解码画面 {decoded} 张，写出 {written} 张，耗时 {report.ElapsedMs} ms");
            Line($"最后一次输出状态：{decoderLocal.LastOutputResult}");
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
            if (decoder is not null)
            {
                foreach (var entry in decoder.AttemptLog)
                {
                    Line($"  {entry}");
                }

                report.AttemptLog = [.. decoder.AttemptLog];
            }

            Write(directory, report, log);
            return 1;
        }
    }

    private static int RunWithSourceReader(string directory, string input, int maxPictures,
        DecodeSmokeReport report, StringBuilder log, Action<string> line, Action<string, DecodeSmokeReport, StringBuilder> write)
    {
        using var decoder = new H264SourceReaderDecoder(input);
        line($"Source Reader：{decoder.Width}x{decoder.Height}, 输出 {decoder.OutputSubtype}");
        report.Decoder = "MediaFoundation SourceReader";
        report.OutputWidth = decoder.Width;
        report.OutputHeight = decoder.Height;

        var clock = Stopwatch.StartNew();
        int decoded = 0;
        int written = 0;
        double brightestStdDev = 0;
        while (decoded < 240)
        {
            var frame = decoder.ReadNext(decoded * 33_333L);
            if (frame is null)
            {
                break;
            }

            decoded++;
            if (written >= maxPictures || frame.Width <= 0 || frame.Height <= 0)
            {
                continue;
            }

            byte[] bgra = new byte[Nv12ToBgra.BgraSize(frame.Width, frame.Height)];
            frame.ToBgra(bgra);
            var (mean, stdDev) = BitmapWriter.Statistics(bgra, frame.Width, frame.Height);
            brightestStdDev = Math.Max(brightestStdDev, stdDev);
            string path = Path.Combine(directory, $"frame-{written:D3}.bmp");
            BitmapWriter.WriteBgra(path, bgra, frame.Width, frame.Height);
            line($"  帧 {written}: {frame.Width}x{frame.Height}, YStride={frame.YStride}, " +
                $"UVStride={frame.UvStride}, 均值={mean:F1}, 标准差={stdDev:F1} -> {Path.GetFileName(path)}");
            written++;
        }

        report.DecodedPictures = decoded;
        report.WrittenBitmaps = written;
        report.BrightestStdDev = Math.Round(brightestStdDev, 2);
        report.ElapsedMs = (int)clock.ElapsedMilliseconds;

        bool ok = decoded > 0 && written > 0 && brightestStdDev > 1.0;
        report.Status = ok ? "ok" : "failed";
        if (!ok)
        {
            report.Error = decoded == 0 ? "解码器未输出任何画面" : "解码画面接近纯色";
        }

        line($"解码画面 {decoded} 张，写出 {written} 张，耗时 {report.ElapsedMs} ms");
        line($"结果：{report.Status}" + (report.Error is null ? string.Empty : $"（{report.Error}）"));
        write(directory, report, log);
        return ok ? 0 : 1;
    }

    private static string ResolveInput(string? inputFile, string outputDirectory)
    {
        if (!string.IsNullOrWhiteSpace(inputFile))
        {
            if (!File.Exists(inputFile))
            {
                throw new FileNotFoundException("指定的解码输入不存在。", inputFile);
            }

            return Path.GetFullPath(inputFile);
        }

        // Default: the stream produced by a session smoke next to this output directory.
        string sibling = Path.Combine(outputDirectory, "received.h264");
        if (File.Exists(sibling))
        {
            return sibling;
        }

        string parent = Path.GetFullPath(Path.Combine(outputDirectory, "..", "session-final", "received.h264"));
        if (File.Exists(parent))
        {
            return parent;
        }

        throw new FileNotFoundException("未找到解码输入，请用 --decode-input 指定 .h264 文件。");
    }

    /// <summary>Split into access units using the shared rule in <see cref="H264Bitstream"/>.</summary>
    private static List<byte[]> SplitAccessUnits(byte[] stream) => H264Bitstream.SplitAccessUnits(stream);

    private static void Write(string directory, DecodeSmokeReport report, StringBuilder log)
    {
        File.WriteAllText(Path.Combine(directory, "decode-smoke.log"), log.ToString());
        File.WriteAllText(Path.Combine(directory, "decode-smoke-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    public sealed class DecodeSmokeReport
    {
        public string Status { get; set; } = "failed";
        public string? Error { get; set; }
        public string? HResult { get; set; }
        public string? Input { get; set; }
        public int InputBytes { get; set; }
        public int AccessUnits { get; set; }
        public string? Decoder { get; set; }
        public bool HardwareAccelerated { get; set; }
        public int DecodedPictures { get; set; }
        public int WrittenBitmaps { get; set; }
        public int OutputWidth { get; set; }
        public int OutputHeight { get; set; }
        public double BrightestStdDev { get; set; }
        public int ElapsedMs { get; set; }
        public string[] AttemptLog { get; set; } = [];
    }
}