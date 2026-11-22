using System.IO;
using System.Security.Cryptography;

namespace ScreenCast.Windows;

internal static class DeviceIdentity
{
    public static string GetOrCreate()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenCast");
        Directory.CreateDirectory(directory);
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
}
