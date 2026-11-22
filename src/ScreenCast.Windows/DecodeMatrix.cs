using System.Diagnostics;
using System.IO;
using System.Text;
using ScreenCast.Windows.Media;

namespace ScreenCast.Windows;

/// <summary>
/// <c>--decode-matrix &lt;file.h264&gt;</c>: try every combination of sequence-header form and
/// sample layout against the Media Foundation H.264 decoder, and report which one produces pictures.
/// </summary>
/// <remarks>
/// Two independent choices can each break decoding: how SPS/PPS are published on the input type
/// (avcC, Annex-B, or not at all) and how samples are framed (start codes or 4-byte length prefixes).
/// Guessing wastes more time than measuring all six combinations, so this measures them.
/// </remarks>
public static class DecodeMatrix
{
    public static int Run(string inputFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFile);
        MediaSmoke.AttachParentConsole();
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // No attached console.
        }

        var log = new StringBuilder();
        void Line(string message)
        {
            log.AppendLine(message);
            Console.WriteLine(message);
        }

        int result;
        try
        {
            result = Probe(inputFile, Line);
        }
        catch (Exception ex)
        {
            Line($"未处理异常：{ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})");
            result = 1;
        }

        try
        {
            string logPath = Path.Combine(Path.GetTempPath(), "screencast-decode-matrix.log");
            File.WriteAllText(logPath, log.ToString());
            Console.WriteLine($"报告：{logPath}");
        }
        catch (IOException)
        {
            // Best effort.
        }

        return result;
    }

    private static int Probe(string inputFile, Action<string> line)
    {
        if (!File.Exists(inputFile))
        {
            line($"输入不存在：{inputFile}");
            return 1;
        }

        byte[] stream = File.ReadAllBytes(inputFile);
        var accessUnits = H264Bitstream.SplitAccessUnits(stream);
        line($"输入 {stream.Length} 字节，访问单元 {accessUnits.Count} 个");
        if (accessUnits.Count == 0)
        {
            line("未找到访问单元");
            return 1;
        }

        int winners = 0;
        foreach (var headerMode in new[] { SequenceHeaderFormat.AvcC, SequenceHeaderFormat.AnnexB, SequenceHeaderFormat.None })
        {
            foreach (bool lengthPrefixed in new[] { true, false })
            {
                string label = $"序列头={headerMode}, 样本={(lengthPrefixed ? "长度前缀" : "起始码")}";
                H264Decoder decoder;
                try
                {
                    decoder = new H264Decoder(requestHardwareAcceleration: false)
                    {
                        SequenceHeaderMode = headerMode,
                        FeedLengthPrefixed = lengthPrefixed,
                    };
                }
                catch (Exception ex)
                {
                    line($"{label}: 创建失败 {ex.Message}");
                    continue;
                }

                using (decoder)
                {
                    var clock = Stopwatch.StartNew();
                    int frames = 0;
                    string detail;
                    try
                    {
                        for (int au = 0; au < accessUnits.Count && frames < 90; au++)
                        {
                            frames += decoder.Decode(accessUnits[au], au * 33_333L).Count;
                        }

                        detail = $"输出 {decoder.OutputWidth}x{decoder.OutputHeight}, 末次输入 {decoder.LastInputResult}, 末次输出 {decoder.LastOutputResult}";
                    }
                    catch (Exception ex)
                    {
                        detail = $"中断 0x{ex.HResult:X8} {ex.Message}";
                    }

                    line($"{label}: 解码 {frames} 帧 ({clock.ElapsedMilliseconds} ms) — {detail}");
                    if (frames > 0)
                    {
                        winners++;
                    }
                }
            }
        }

        line(winners > 0 ? $"可用组合 {winners} 个" : "全部 6 种组合均未产出画面");
        return winners > 0 ? 0 : 1;
    }
}