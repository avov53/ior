namespace HappBox;

public static class NativeProtocolComponents
{
    public const string AwgVersion = "3.1.20260828";
    public const string AwgSha256 = "9a39a3f98ab96af22c58727945759d2d5faf8db5792083ea49214214cc0abe85";
    public static string AwgPath => Path.Combine(AppContext.BaseDirectory,"core","ior-core","ior-awg-adapter.exe");
    public static bool VerifyAwg(){try{using var file=File.OpenRead(AwgPath);return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)).Equals(AwgSha256,StringComparison.OrdinalIgnoreCase);}catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}}
}
