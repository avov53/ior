using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Text.RegularExpressions;

namespace HappBox;

public sealed record ServicePreset(string Id, string Name, string Url, string? IconUrl = null);

public static class ServiceMonitorCatalog
{
    public static IReadOnlyList<ServicePreset> Presets { get; } =
    [
        new("discord", "Discord", "https://discord.com",
            "https://cdn.prod.website-files.com/6257adef93867e50d84d30e2/62fddf0fde45a8baedcc7ee5_847541504914fd33810e70a0ea73177e%20(2)-1.png"),
        new("youtube", "YouTube", "https://www.youtube.com"),
        new("telegram", "Telegram", "https://telegram.org"),
        new("roblox", "Roblox", "https://www.roblox.com"),
        new("twitch", "Twitch", "https://www.twitch.tv"),
        new("steam", "Steam", "https://store.steampowered.com"),
        new("github", "GitHub", "https://github.com"),
        new("google", "Google", "https://www.google.com"),
        new("vk", "VK", "https://vk.com"),
        new("reddit", "Reddit", "https://www.reddit.com"),
        new("tiktok", "TikTok", "https://www.tiktok.com"),
        new("spotify", "Spotify", "https://open.spotify.com")
    ];

    public static List<ServiceMonitorTarget> Defaults() => Presets.Take(3)
        .Select(ToTarget)
        .ToList();

    public static ServiceMonitorTarget ToTarget(ServicePreset preset) => new()
    {
        Name = preset.Name,
        Url = preset.Url,
        PresetId = preset.Id
    };

    public static void NormalizeTargets(AppState state)
    {
        state.ServiceMonitorTargets ??= [];
        var normalized = new List<ServiceMonitorTarget>(3);
        foreach (var target in state.ServiceMonitorTargets.Take(3))
        {
            if (!TryNormalizeUrl(target.Url, out var uri)) continue;
            target.Id = string.IsNullOrWhiteSpace(target.Id) ? Guid.NewGuid().ToString("N") : target.Id;
            target.Url = uri.AbsoluteUri.TrimEnd('/');
            target.Name = string.IsNullOrWhiteSpace(target.Name) ? DisplayName(uri) : target.Name.Trim();
            normalized.Add(target);
        }

        foreach (var fallback in Defaults())
        {
            if (normalized.Count == 3) break;
            if (normalized.Any(x => x.PresetId == fallback.PresetId)) continue;
            normalized.Add(fallback);
        }
        while (normalized.Count < 3) normalized.Add(Defaults()[normalized.Count]);
        state.ServiceMonitorTargets = normalized;
    }

    public static bool TryCreateCustom(string input, out ServiceMonitorTarget target)
    {
        target = null!;
        if (!TryNormalizeUrl(input, out var uri)) return false;
        target = new ServiceMonitorTarget
        {
            Name = DisplayName(uri),
            Url = uri.AbsoluteUri.TrimEnd('/')
        };
        return true;
    }

    public static bool TryNormalizeUrl(string? input, out Uri uri)
    {
        uri = null!;
        var value = input?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo)) return false;
        uri = parsed;
        return true;
    }

    public static string DisplayName(Uri uri)
    {
        var host = uri.IdnHost;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        var first = host.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? host;
        if (string.IsNullOrWhiteSpace(first)) return host;
        return char.ToUpperInvariant(first[0]) + first[1..];
    }
}

public sealed class ServiceFaviconCache
{
    public const int MaximumBytes = 256 * 1024;
    private readonly string _directory;
    private readonly HttpClient _client;

    public ServiceFaviconCache(string directory, HttpClient client)
    {
        _directory = directory;
        _client = client;
    }

    public async Task<byte[]?> GetAsync(string targetUrl, CancellationToken cancellationToken = default, string? preferredIconUrl = null)
    {
        if (!ServiceMonitorCatalog.TryNormalizeUrl(targetUrl, out var target)) return null;
        Directory.CreateDirectory(_directory);
        var cachePath = Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.IdnHost))).ToLowerInvariant() + ".ico");
        var cached = await ReadValidAsync(cachePath, cancellationToken);
        if (cached is not null) return cached;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            byte[]? bytes = null;
            if (Uri.TryCreate(preferredIconUrl, UriKind.Absolute, out var preferred) && preferred.Scheme is "http" or "https")
                bytes = await DownloadImageAsync(preferred, timeout.Token);
            var favicon = new UriBuilder(target.Scheme, target.Host, target.IsDefaultPort ? -1 : target.Port, "/favicon.ico").Uri;
            bytes ??= await DownloadImageAsync(favicon, timeout.Token);
            if (bytes is null)
            {
                foreach (var candidate in await DiscoverIconsAsync(target, timeout.Token))
                {
                    bytes = await DownloadImageAsync(candidate, timeout.Token);
                    if (bytes is not null) break;
                }
            }
            if (bytes is null) return null;
            await File.WriteAllBytesAsync(cachePath, bytes, timeout.Token);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private async Task<byte[]?> DownloadImageAsync(Uri uri, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", "ior/1.2");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var bytes = await ReadLimitedAsync(stream, MaximumBytes, token);
            return bytes is not null && IsImage(bytes) ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<Uri>> DiscoverIconsAsync(Uri page, CancellationToken token)
    {
        const int maximumHtmlBytes = 2 * 1024 * 1024;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, page);
            request.Headers.TryAddWithoutValidation("User-Agent", "ior/1.2");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maximumHtmlBytes) return [];
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var bytes = await ReadLimitedAsync(stream, maximumHtmlBytes, token);
            if (bytes is null) return [];
            var html = Encoding.UTF8.GetString(bytes);
            var result = new List<Uri>();
            foreach (Match tag in Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase))
            {
                var rel = Attribute(tag.Value, "rel");
                if (rel is null || !rel.Contains("icon", StringComparison.OrdinalIgnoreCase)) continue;
                var href = Attribute(tag.Value, "href");
                if (string.IsNullOrWhiteSpace(href)) continue;
                href = WebUtility.HtmlDecode(href);
                if (Uri.TryCreate(page, href, out var icon) && icon.Scheme is "http" or "https")
                    result.Add(icon);
            }
            return result.Distinct().Take(8).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag,
            $@"\b{Regex.Escape(name)}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value : null;
    }

    private static async Task<byte[]?> ReadLimitedAsync(Stream stream, int maximumBytes, CancellationToken token)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) return memory.ToArray();
            if (memory.Length + read > maximumBytes) return null;
            await memory.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    private static async Task<byte[]?> ReadValidAsync(string path, CancellationToken token)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumBytes) return null;
            var bytes = await File.ReadAllBytesAsync(path, token);
            return IsImage(bytes) ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            return image.Width > 0 && image.Height > 0;
        }
        catch
        {
            return false;
        }
    }
}
