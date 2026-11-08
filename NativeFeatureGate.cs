using System.Security.Cryptography;
namespace HappBox;
public static class NativeFeatureGate
{
    public static bool Matches(string manifest, string executable)
    {
        try
        {
            if (!File.Exists(manifest) || new FileInfo(manifest).Length > 80) return false;
            var expected = File.ReadAllText(manifest).Trim();
            if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) return false;
            using var input = File.OpenRead(executable);
            return string.Equals(expected, Convert.ToHexString(SHA256.HashData(input)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
