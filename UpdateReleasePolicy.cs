using System.Text.Json;

namespace HappBox;

public sealed record UpdateRelease(string Tag, string ReleaseUrl, string? AssetName, string? AssetDownloadUrl);

public static class UpdateReleasePolicy
{
    public static UpdateRelease? Parse(JsonElement releaseRoot, string fallbackReleaseUrl, bool windows10)
    {
        var tag = releaseRoot.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var releaseUrl = ReadHttpsUrl(releaseRoot, "html_url") ?? fallbackReleaseUrl;
        var asset = FindAsset(releaseRoot, windows10);
        return new UpdateRelease(tag, releaseUrl, asset.Name, asset.Url);
    }

    public static bool IsNewer(string latest, string current) =>
        Version.TryParse(Normalize(latest), out var latestVersion) &&
        Version.TryParse(Normalize(current), out var currentVersion) && latestVersion > currentVersion;

    public static int? ProgressPercent(long receivedBytes, long? totalBytes) => totalBytes is > 0
        ? Math.Clamp((int)(Math.Max(0, receivedBytes) / (double)totalBytes.Value * 100), 0, 100)
        : null;

    private static string Normalize(string value) => value.Trim().TrimStart('v', 'V');

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
