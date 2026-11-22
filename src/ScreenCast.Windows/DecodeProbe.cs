using System.Diagnostics;
using System.IO;
using System.Text;
using ScreenCast.Windows.Media;

namespace ScreenCast.Windows;

/// <summary>
/// <c>--decode-probe &lt;file.h264&gt;</c>: systematic search for the output type the Media
/// Foundation H.264 decoder actually accepts and decodes into.
/// </summary>
/// <remarks>
/// A fresh decoder is created per candidate because a transform may not accept a new output type
/// once streaming has started. Each candidate is fed the same access units and the number of emitted
/// pictures is recorded, so the winning format is found by evidence rather than by assumption.
/// </remarks>
public static class DecodeProbe
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
            // A WinExe started from a script has no reliable console, so the report is always a file.
            string logPath = Path.Combine(Path.GetTempPath(), "screencast-decode-probe.log");
            File.WriteAllText(logPath, log.ToString());
            Console.WriteLine($"报告：{logPath}");
        }
        catch (IOException)
        {
            // Reporting is best effort.
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
        var accessUnits = SplitAccessUnits(stream);
        line($"输入 {stream.Length} 字节，访问单元 {accessUnits.Count} 个");
        if (accessUnits.Count == 0)
        {
            line("未找到访问单元");
            return 1;
        }

        // Discover the advertised output types with one throwaway decoder.
        using (var discovery = H264Decoder.CreateForProbing())
        {
            try
            {
                discovery.ConfigureInputOnly(accessUnits[0]);
            }
            catch (Exception ex)
            {
                line($"配置输入失败：{ex.GetType().Name}: {ex.Message}");
                return 1;
            }

            line("解码器提供的输出类型：");
            foreach (var description in discovery.DescribeOutputTypes())
            {
                line($"  {description}");
            }
        }

        int winners = 0;
        for (int index = 0; index < 8; index++)
        {
            H264Decoder decoder;
            try
            {
                decoder = H264Decoder.CreateForProbing();
            }
            catch (Exception ex)
            {
                line($"创建解码器失败：{ex.Message}");
                return 1;
            }

            using (decoder)
            {
                try
                {
                    decoder.ConfigureInputOnly(accessUnits[0]);
                }
                catch (Exception ex)
                {
                    line($"#{index}: 配置输入失败 {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (!decoder.TrySetOutputTypeByIndex(index, out var detail))
                {
                    line($"#{index}: 设置失败 {detail}");
                    continue;
                }

                var clock = Stopwatch.StartNew();
                int frames = 0;
                string last = "none";
                for (int au = 0; au < accessUnits.Count && frames < 60; au++)
                {
                    try
                    {
                        frames += decoder.Decode(accessUnits[au], au * 33_333L).Count;
                        last = decoder.LastOutputResult;
                    }
                    catch (Exception ex)
                    {
                        line($"#{index}: 解码中断 0x{ex.HResult:X8} {ex.Message}");
                        break;
                    }
                }

                line($"#{index}: {detail} -> 解码 {frames} 帧 ({clock.ElapsedMilliseconds} ms), 末次 {last}");
                if (frames > 0)
                {
                    winners++;
                }
            }
        }

        line(winners > 0 ? $"找到 {winners} 个可用输出类型" : "所有候选输出类型均未产出画面");
        return winners > 0 ? 0 : 1;
    }

    private static List<byte[]> SplitAccessUnits(byte[] stream)
    {
        var units = new List<byte[]>();
        var nalUnits = H264Bitstream.ParseAnnexB(stream);
        if (nalUnits.Count == 0)
        {
            return units;
        }

        int currentStart = -1;
        bool hasSlice = false;
        foreach (var nal in nalUnits)
        {
            bool isDelimiter = nal.Type == H264NalUnitType.AccessUnitDelimiter;
            bool isSlice = nal.Type is H264NalUnitType.IdrSlice or H264NalUnitType.NonIdrSlice;

            if (currentStart < 0)
            {
                currentStart = nal.Offset;
            }
            else if (isDelimiter || (isSlice && hasSlice))
            {
                if (nal.Offset > currentStart)
                {
                    units.Add(stream[currentStart..nal.Offset]);
                }

                currentStart = nal.Offset;
                hasSlice = false;
            }

            if (isSlice)
            {
                hasSlice = true;
            }
        }

        if (currentStart >= 0 && stream.Length > currentStart)
        {
            units.Add(stream[currentStart..]);
        }

        return units;
    }
}