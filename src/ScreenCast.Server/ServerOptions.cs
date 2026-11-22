using System.Net;

namespace ScreenCast.Server;

/// <summary>Monotonic millisecond clock. Injected so lease and scan behaviour is testable without sleeping.</summary>
public interface IMonotonicClock
{
    long Milliseconds { get; }
}

/// <summary>Default clock backed by <see cref="Environment.TickCount64"/>.</summary>
public sealed class SystemMonotonicClock : IMonotonicClock
{
    public static SystemMonotonicClock Instance { get; } = new();

    public long Milliseconds => Environment.TickCount64;
}

/// <summary>Tunables and hard bounds for the signaling service. Every value is a hard limit, not a hint.</summary>
public sealed class SignalingOptions
{
    public const int MaxMessageBytes = 8192;

    public IPAddress BindAddress { get; set; } = IPAddress.Loopback;
    public int Port { get; set; } = 3479;

    /// <summary>A device without any accepted message for this long is removed.</summary>
    public int DeviceLeaseMs { get; set; } = 45_000;

    /// <summary>A session without any participant activity for this long is closed.</summary>
    public int SessionIdleMs { get; set; } = 45_000;

    /// <summary>An unjoined invite (Created state) expires after this long regardless of activity.</summary>
    public int InviteTimeoutMs { get; set; } = 30_000;

    public int ScanIntervalMs { get; set; } = 5_000;
    public int MaxSessionsPerDevice { get; set; } = 2;
    public int MaxSessionsPerHost { get; set; } = 4;
    public int MaxSessions { get; set; } = 256;
    public int MaxDevices { get; set; } = 4096;
    public int GlobalRequestsPerSecond { get; set; } = 200;
    public int PerAddressRequestsPerSecond { get; set; } = 20;

    /// <summary>Delay handed to both peers in PunchStartNotify so they start punching together.</summary>
    public int PunchStartDelayMs { get; set; } = 100;

    public int HeartbeatIntervalMs { get; set; } = 15_000;

    /// <summary>How long a requestId/response pair is remembered for idempotent retries.</summary>
    public int ReplyCacheMs { get; set; } = 30_000;

    public int ReplyCacheCapacity { get; set; } = 2048;

    public void Validate()
    {
        if (Port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (DeviceLeaseMs < 1) throw new ArgumentOutOfRangeException(nameof(DeviceLeaseMs));
        if (SessionIdleMs < 1) throw new ArgumentOutOfRangeException(nameof(SessionIdleMs));
        if (InviteTimeoutMs < 1) throw new ArgumentOutOfRangeException(nameof(InviteTimeoutMs));
        if (ScanIntervalMs < 1) throw new ArgumentOutOfRangeException(nameof(ScanIntervalMs));
        if (MaxSessionsPerDevice < 1) throw new ArgumentOutOfRangeException(nameof(MaxSessionsPerDevice));
        if (MaxSessionsPerHost < 1) throw new ArgumentOutOfRangeException(nameof(MaxSessionsPerHost));
        if (MaxSessions < 1) throw new ArgumentOutOfRangeException(nameof(MaxSessions));
        if (MaxDevices < 1) throw new ArgumentOutOfRangeException(nameof(MaxDevices));
        if (GlobalRequestsPerSecond < 1) throw new ArgumentOutOfRangeException(nameof(GlobalRequestsPerSecond));
        if (PerAddressRequestsPerSecond < 1) throw new ArgumentOutOfRangeException(nameof(PerAddressRequestsPerSecond));
        if (PerAddressRequestsPerSecond > GlobalRequestsPerSecond) throw new ArgumentOutOfRangeException(nameof(PerAddressRequestsPerSecond));
        if (PunchStartDelayMs is < 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(PunchStartDelayMs));
        if (HeartbeatIntervalMs < 1000) throw new ArgumentOutOfRangeException(nameof(HeartbeatIntervalMs));
        if (ReplyCacheMs < 1) throw new ArgumentOutOfRangeException(nameof(ReplyCacheMs));
        if (ReplyCacheCapacity < 1) throw new ArgumentOutOfRangeException(nameof(ReplyCacheCapacity));
        if (BindAddress.AddressFamily is not (System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6))
            throw new ArgumentOutOfRangeException(nameof(BindAddress));
    }
}

/// <summary>Whole-executable options: one STUN socket and one signaling socket, both UDP.</summary>
public sealed class ServerOptions
{
    public IPAddress BindAddress { get; set; } = IPAddress.Loopback;
    public int StunPort { get; set; } = 3478;
    public bool StunEnabled { get; set; } = true;
    public bool SignalingEnabled { get; set; } = true;
    public SignalingOptions Signaling { get; } = new();

    /// <summary>Parses the documented command line. Throws <see cref="ArgumentException"/> on unknown or incomplete input.</summary>
    public static ServerOptions Parse(string[] args)
    {
        var options = new ServerOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            switch (name)
            {
                case "--bind" when i + 1 < args.Length:
                    options.BindAddress = IPAddress.Parse(args[++i]);
                    options.Signaling.BindAddress = options.BindAddress;
                    break;
                case "--port" when i + 1 < args.Length:
                case "--stun-port" when i + 1 < args.Length:
                    options.StunPort = ParsePort(args[++i]);
                    break;
                case "--signal-port" when i + 1 < args.Length:
                    options.Signaling.Port = ParsePort(args[++i]);
                    break;
                case "--no-stun":
                    options.StunEnabled = false;
                    break;
                case "--no-signal":
                    options.SignalingEnabled = false;
                    break;
                case "--device-lease-seconds" when i + 1 < args.Length:
                    options.Signaling.DeviceLeaseMs = checked(ParsePositive(args[++i]) * 1000);
                    break;
                case "--session-idle-seconds" when i + 1 < args.Length:
                    options.Signaling.SessionIdleMs = checked(ParsePositive(args[++i]) * 1000);
                    break;
                case "--invite-seconds" when i + 1 < args.Length:
                    options.Signaling.InviteTimeoutMs = checked(ParsePositive(args[++i]) * 1000);
                    break;
                case "--scan-seconds" when i + 1 < args.Length:
                    options.Signaling.ScanIntervalMs = checked(ParsePositive(args[++i]) * 1000);
                    break;
                case "--max-sessions-per-device" when i + 1 < args.Length:
                    options.Signaling.MaxSessionsPerDevice = ParsePositive(args[++i]);
                    break;
                case "--max-sessions-per-host" when i + 1 < args.Length:
                    options.Signaling.MaxSessionsPerHost = ParsePositive(args[++i]);
                    break;
                case "--max-sessions" when i + 1 < args.Length:
                    options.Signaling.MaxSessions = ParsePositive(args[++i]);
                    break;
                case "--max-devices" when i + 1 < args.Length:
                    options.Signaling.MaxDevices = ParsePositive(args[++i]);
                    break;
                case "--global-rate" when i + 1 < args.Length:
                    options.Signaling.GlobalRequestsPerSecond = ParsePositive(args[++i]);
                    break;
                case "--per-ip-rate" when i + 1 < args.Length:
                    options.Signaling.PerAddressRequestsPerSecond = ParsePositive(args[++i]);
                    break;
                case "--punch-delay-ms" when i + 1 < args.Length:
                    options.Signaling.PunchStartDelayMs = ParsePositive(args[++i]);
                    break;
                case "--heartbeat-seconds" when i + 1 < args.Length:
                    options.Signaling.HeartbeatIntervalMs = checked(ParsePositive(args[++i]) * 1000);
                    break;
                case "--help":
                    options.HelpRequested = true;
                    return options;
                case "--self-test":
                    options.SelfTestRequested = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete argument: {name}");
            }
        }
        options.Validate();
        return options;
    }

    public bool HelpRequested { get; private set; }
    public bool SelfTestRequested { get; private set; }

    public void Validate()
    {
        if (BindAddress.AddressFamily is not (System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6))
            throw new ArgumentException("--bind must be an IPv4 or IPv6 address.");
        optionsPort(StunPort);
        if (!StunEnabled && !SignalingEnabled) throw new ArgumentException("Both --no-stun and --no-signal leave nothing to run.");
        Signaling.BindAddress = BindAddress;
        Signaling.Validate();
    }

    private static void optionsPort(int port)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "port must be 0..65535");
    }

    private static int ParsePort(string value) => int.TryParse(value, out int port) && port is >= 0 and <= 65535
        ? port
        : throw new ArgumentException($"Invalid port: {value}");

    private static int ParsePositive(string value) => int.TryParse(value, out int number) && number > 0
        ? number
        : throw new ArgumentException($"Expected a positive integer, got: {value}");

    public static string Usage =>
        """
        ScreenCast.Server - standalone UDP server (Windows Server 2022, net8.0)

        Usage: ScreenCast.Server [options]

          --bind <ip>                     Bind address for both sockets (default 127.0.0.1)
          --port <n>                      STUN Binding port, alias of --stun-port (default 3478)
          --stun-port <n>                 STUN Binding port (default 3478)
          --signal-port <n>               Signaling port (default 3479)
          --no-stun                       Do not open the STUN socket
          --no-signal                     Do not open the signaling socket
          --device-lease-seconds <n>      Device lease, default 45
          --session-idle-seconds <n>      Session idle timeout, default 45
          --invite-seconds <n>            Created-state invite window, default 30
          --scan-seconds <n>              Cleanup scan interval, default 5
          --heartbeat-seconds <n>         Heartbeat interval reported to devices, default 15
          --max-sessions-per-device <n>   Default 2
          --max-sessions-per-host <n>     Default 4
          --max-sessions <n>              Global session cap, default 256
          --max-devices <n>               Device table cap, default 4096
          --global-rate <n>               Requests per second, all sources, default 200
          --per-ip-rate <n>               Requests per second per source IP, default 20
          --punch-delay-ms <n>            Punch start delay sent to peers, default 100
          --self-test                     Run a socket level self check and exit
          --help                          Show this text

        Signaling is a hand-written binary protocol (magic SCSG, 24 byte header, max 8192 bytes).
        PeerKeyOffer/PeerKeyAnswer payloads are forwarded as opaque bytes; the server never
        parses or logs media key material. TURN relay and identity signatures are not implemented.
        """;
}