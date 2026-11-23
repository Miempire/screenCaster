using System.IO;
using System.Security.Cryptography;

namespace ScreenCast.Windows;

internal static class DeviceIdentity
{
    private static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenCast");

    public static string GetOrCreate()
    {
        string directory = Directory;
        System.IO.Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "device-id");
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path).Trim();
            if (existing.Length is >= 8 and <= 64 && existing.All(char.IsAsciiLetterOrDigit)) return existing;
        }

        string created = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        File.WriteAllText(path, created);
        return created;
    }

    public static string LastPairedCode
    {
        get
        {
            string path = Path.Combine(Directory, "last-code");
            try
            {
                if (!File.Exists(path)) return string.Empty;
                string code = File.ReadAllText(path).Trim();
                return code.Length == 9 && code.All(char.IsAsciiDigit) ? code : string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }
    }

    public static void SaveLastPairedCode(string code)
    {
        if (code.Length != 9 || !code.All(char.IsAsciiDigit)) return;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path.Combine(Directory, "last-code"), code);
        }
        catch (IOException)
        {
        }
    }
}
