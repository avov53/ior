using System.Text.Json;

namespace HappBox;

public sealed record UpdateRelease(string Tag, string ReleaseUrl, string? AssetName, string? AssetDownloadUrl);

public static class UpdateReleasePolicy
{
    public static string InstalledVersion(System.Reflection.Assembly assembly)
    {
        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
    public static UpdateRelease? Parse(JsonElement releaseRoot, string fallbackReleaseUrl, bool windows10)
    {
        if(releaseRoot.TryGetProperty("draft",out var draft)&&draft.ValueKind==JsonValueKind.True || releaseRoot.TryGetProperty("prerelease",out var prerelease)&&prerelease.ValueKind==JsonValueKind.True)return null;
        var tag = releaseRoot.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var releaseUrl = ReadHttpsUrl(releaseRoot, "html_url") ?? fallbackReleaseUrl;
        var asset = FindAsset(releaseRoot, windows10);
        return new UpdateRelease(tag, releaseUrl, asset.Name, asset.Url);
    }

    public static bool IsNewer(string latest, string current) =>
        TryVersion(latest, out var latestVersion) && TryVersion(current, out var currentVersion) && latestVersion > currentVersion;

    public static bool CanInstall(string release, string downloaded, string current) =>
        TryVersion(release, out var expected) && TryVersion(downloaded, out var actual) &&
        expected == actual && IsNewer(downloaded, current);

    private static bool TryVersion(string value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (!Version.TryParse(Normalize(value), out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        return true;
    }

    public static int? ProgressPercent(long receivedBytes, long? totalBytes) => totalBytes is > 0
        ? Math.Clamp((int)(Math.Max(0, receivedBytes) / (double)totalBytes.Value * 100), 0, 100)
        : null;

    private static string Normalize(string value) => value.Trim().TrimStart('v', 'V').Split('+')[0];

    private static (string? Name, string? Url) FindAsset(JsonElement releaseRoot, bool windows10)
    {
        if (!releaseRoot.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (null, null);

        var keywords = windows10 ? new[] { "windows-10", "win10" } : new[] { "windows-11", "win11" };
        foreach (var keyword in keywords)
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            var url = ReadHttpsUrl(asset, "browser_download_url");
            if (!string.IsNullOrWhiteSpace(name) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                name.Contains(keyword, StringComparison.OrdinalIgnoreCase) && url is not null)
                return (name, url);
        }
        return (null, null);
    }

    private static string? ReadHttpsUrl(JsonElement element, string property)
    {
        var text = element.TryGetProperty(property, out var value) ? value.GetString() : null;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
    }
}
