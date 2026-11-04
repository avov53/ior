namespace HappBox;

public static class NativeWindowsPlatform
{
    // Windows 10 1607 is the minimum Windows 10 version for the .NET 8 host.
    public const int MinimumBuild = 14393;
    public static bool TunAvailable => OperatingSystem.IsWindows() && SupportsTun(Environment.OSVersion.Version);
    public static bool SupportsTun(Version version) => version.Major > 10 ||
        version.Major == 10 && (version.Minor > 0 || version.Build >= MinimumBuild);
    public static bool IsWindows11(Version version) => version.Major > 10 ||
        version.Major == 10 && (version.Minor > 0 || version.Build >= 22000);
    public static bool SameTestedFamily(int recordedBuild, int currentBuild) =>
        recordedBuild >= MinimumBuild && currentBuild >= MinimumBuild &&
        (recordedBuild >= 22000) == (currentBuild >= 22000);
}
