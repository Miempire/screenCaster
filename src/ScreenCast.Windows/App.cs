using System.IO;
using System.Text.Json;
using System.Windows;

namespace ScreenCast.Windows;

public sealed class App : Application
{
    [STAThread]
    public static int Main(string[] args)
    {
        string mode = "host";
        string? diagnosticFile = null;
        string? mediaSmokeDirectory = null;
        string? sessionSmokeDirectory = null;
        string? decodeSmokeDirectory = null;
        string? decodeInput = null;
        string? decodeProbeInput = null;
        string? decodeMatrixInput = null;
        string? liveSmokeDirectory = null;
        (int Video, int Control, int PeerVideo, int Seconds, string Directory)? hostListen = null;
        (string Address, int Video, int Control, int LocalVideo, int LocalControl, int Seconds, string Directory)? viewerConnect = null;
        int seconds = 8;
        int mediaSeconds = 5;
        int mediaFrames = 60;
        bool capture = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--mode" && i + 1 < args.Length && args[i + 1] is "host" or "viewer")
                mode = args[++i];
            else if (args[i] == "--diagnostics" && i + 1 < args.Length)
                diagnosticFile = args[++i];
            else if (args[i] == "--capture-once")
                capture = true;
            else if (args[i] == "--media-smoke" && i + 1 < args.Length)
                mediaSmokeDirectory = args[++i];
            else if (args[i] == "--session-smoke" && i + 1 < args.Length)
                sessionSmokeDirectory = args[++i];
            else if (args[i] == "--decode-smoke" && i + 1 < args.Length)
                decodeSmokeDirectory = args[++i];
            else if (args[i] == "--host-listen" && i + 5 < args.Length
                && int.TryParse(args[i + 1], out int listenVideo) && int.TryParse(args[i + 2], out int listenControl)
                && int.TryParse(args[i + 3], out int listenPeerVideo)
                && int.TryParse(args[i + 4], out int listenSeconds) && listenSeconds > 0)
            {
                hostListen = (listenVideo, listenControl, listenPeerVideo, listenSeconds, args[i + 5]);
                i += 5;
            }
            else if (args[i] == "--viewer-connect" && i + 7 < args.Length
                && int.TryParse(args[i + 2], out int connectVideo) && int.TryParse(args[i + 3], out int connectControl)
                && int.TryParse(args[i + 4], out int localVideo) && int.TryParse(args[i + 5], out int localControl)
                && int.TryParse(args[i + 6], out int connectSeconds) && connectSeconds > 0)
            {
                viewerConnect = (args[i + 1], connectVideo, connectControl, localVideo, localControl, connectSeconds, args[i + 7]);
                i += 7;
            }
            else if (args[i] == "--live-smoke" && i + 1 < args.Length)
            {
                liveSmokeDirectory = args[++i];
            }
            else if (args[i] == "--live-seconds" && i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedLiveSeconds) && parsedLiveSeconds > 0)
            {
                seconds = parsedLiveSeconds;
                i++;
            }
            else if (args[i] == "--decode-matrix" && i + 1 < args.Length)
            {
                decodeMatrixInput = args[++i];
            }
            else if (args[i] == "--decode-probe" && i + 1 < args.Length)
            {
                decodeProbeInput = args[++i];
            }
            else if (args[i] == "--decode-input" && i + 1 < args.Length)
                decodeInput = args[++i];
            else if (args[i] == "--media-seconds" && i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedMediaSeconds) && parsedMediaSeconds > 0)
            {
                mediaSeconds = parsedMediaSeconds;
                i++;
            }
            else if (args[i] == "--media-frames" && i + 1 < args.Length && int.TryParse(args[i + 1], out int frames) && frames > 0)
            {
                mediaFrames = frames;
                i++;
            }
            else
            {
                MessageBox.Show("参数错误。用法：ScreenCast.Windows.exe [--mode host|viewer] [--diagnostics output.json [--capture-once]] [--media-smoke outputDir] [--session-smoke outputDir] [--media-seconds N] [--media-frames N]", "ScreenCast");
                return 2;
            }
        }

        if (hostListen is { } hostSpec)
            return CrossProcessSmoke.RunHost(hostSpec.Video, hostSpec.Control, hostSpec.PeerVideo, hostSpec.Seconds, hostSpec.Directory);
        if (viewerConnect is { } viewerSpec)
            return CrossProcessSmoke.RunViewer(viewerSpec.Address, viewerSpec.Video, viewerSpec.Control,
                viewerSpec.LocalVideo, viewerSpec.LocalControl, viewerSpec.Seconds, viewerSpec.Directory);
        if (liveSmokeDirectory is not null)
            return LiveSmoke.Run(liveSmokeDirectory, seconds);
        if (decodeMatrixInput is not null)
            return DecodeMatrix.Run(decodeMatrixInput);
        if (decodeProbeInput is not null)
            return DecodeProbe.Run(decodeProbeInput);
        if (decodeSmokeDirectory is not null)
            return DecodeSmoke.Run(decodeSmokeDirectory, decodeInput, mediaFrames);
        if (sessionSmokeDirectory is not null)
            return SessionSmoke.Run(sessionSmokeDirectory, mediaSeconds, mediaFrames);
        if (mediaSmokeDirectory is not null)
            return MediaSmoke.Run(mediaSmokeDirectory, mediaSeconds, mediaFrames);
        if (capture && diagnosticFile is null)
        {
            MessageBox.Show("--capture-once 需配合 --diagnostics output.json", "ScreenCast");
            return 2;
        }
        if (diagnosticFile is not null)
        {
            object report;
            int exitCode;
            try
            {
                var displays = CaptureDiagnostics.EnumerateDisplays();
                FrameDiagnostic? frame = capture ? CaptureDiagnostics.CaptureOneFrame(displays[0]) : null;
                report = new { status = "diagnostic-completed", streamingImplemented = false, mode, displays, frame };
                exitCode = 0;
            }
            catch (Exception ex)
            {
                report = new { status = "diagnostic-failed", streamingImplemented = false, mode, error = ex.Message, hresult = $"0x{ex.HResult:X8}" };
                exitCode = 1;
            }
            try
            {
                File.WriteAllText(diagnosticFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception)
            {
                return 3;
            }
            return exitCode;
        }
        return new App().Run(new MainWindow(mode));
    }
}
