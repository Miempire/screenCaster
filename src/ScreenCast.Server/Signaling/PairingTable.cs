using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenCast.Server.Signaling;

/// <summary>
/// Remembers one stable invite code per host device, so a viewer that paired once keeps working
/// across sessions, host restarts and server restarts. The table is only touched under the
/// signaling gate.
/// </summary>
public sealed class PairingTable
{
    private readonly string? path;
    private readonly Dictionary<string, string> hostByCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> codeByHost = new(StringComparer.Ordinal);

    public PairingTable(string? path)
    {
        this.path = path;
        Load();
    }

    public int Count => hostByCode.Count;

    public string? HostForCode(string code) => hostByCode.TryGetValue(code, out string? host) ? host : null;

    public string? CodeForHost(string hostId) => codeByHost.TryGetValue(hostId, out string? code) ? code : null;

    public bool IsPersistent(string code) => hostByCode.ContainsKey(code);

    public string GetOrCreate(string hostId, Func<string> nextCode)
    {
        if (codeByHost.TryGetValue(hostId, out string? existing)) return existing;
        string code = nextCode();
        hostByCode[code] = hostId;
        codeByHost[hostId] = code;
        Save();
        return code;
    }

    private void Load()
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            PairingFile? file = JsonSerializer.Deserialize<PairingFile>(File.ReadAllText(path));
            if (file?.Pairs is null) return;
            foreach (PairingRecord pair in file.Pairs)
            {
                if (!IsValidCode(pair.Code) || string.IsNullOrEmpty(pair.HostId)) continue;
                if (hostByCode.ContainsKey(pair.Code) || codeByHost.ContainsKey(pair.HostId)) continue;
                hostByCode[pair.Code] = pair.HostId;
                codeByHost[pair.HostId] = pair.Code;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    private void Save()
    {
        if (string.IsNullOrEmpty(path)) return;
        var file = new PairingFile
        {
            Pairs = codeByHost.Select(pair => new PairingRecord { Code = pair.Value, HostId = pair.Key }).ToArray(),
        };
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(file, SerializerOptions));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsValidCode(string? code) =>
        code is { Length: 9 } && code[0] != '0' && code.All(char.IsAsciiDigit);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class PairingFile
    {
        public PairingRecord[]? Pairs { get; set; }
    }

    private sealed class PairingRecord
    {
        public string Code { get; set; } = string.Empty;
        public string HostId { get; set; } = string.Empty;
    }
}
