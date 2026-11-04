using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HappBox;

public enum SubscriptionHwidMode { Auto, Allow, Block }

public sealed class SubscriptionHwidException(string code, string origin, string message) : IOException(message)
{
    public string Code { get; } = code;
    public string Origin { get; } = origin;
}

public sealed class SubscriptionIdentity(string directory)
{
    private readonly object _gate = new();
    private string? _seed;
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);
    public string[] Secrets { get { lock (_gate) return _used.ToArray(); } }
    public string ForOrigin(string origin)
    {
        lock (_gate)
        {
            if (_seed is null)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "subscription-device-id.txt");
                if (File.Exists(path))
                {
                    if (!Guid.TryParse(File.ReadAllText(path).Trim(), out var existing))
                        throw new IOException("Не удалось прочитать локальный идентификатор ior. Он не будет заменён автоматически.");
                    _seed = existing.ToString("D");
                }
                else
                {
                    _seed = Guid.NewGuid().ToString("D");
                    PrivateRuntimeFile.Write(path, _seed);
                }
            }
            var id = Derive(_seed, origin); _used.Add(id); _used.Add(id.ToUpperInvariant()); return id;
        }
    }
    public static string Derive(string seed, string origin)
    {
        var bytes = HMACSHA256.HashData(Encoding.UTF8.GetBytes(seed), Encoding.UTF8.GetBytes("ior-hwid-v1:" + origin));
        var hex = Convert.ToHexString(bytes.AsSpan(0, 16)).ToLowerInvariant().ToCharArray();
        hex[12] = '4'; hex[16] = "89ab"[bytes[8] & 3];
        return Guid.ParseExact(new string(hex), "N").ToString("D");
    }
}

public sealed record SubscriptionRequestIdentity(string SourceOrigin, bool Enabled, IReadOnlyCollection<string> ApprovedOrigins, Func<string, string> Resolve, bool AllowSource = true)
{
    public bool Allows(string origin) => Enabled && origin.StartsWith("https://", StringComparison.Ordinal) &&
        (AllowSource && origin == SourceOrigin || ApprovedOrigins.Contains(origin, StringComparer.Ordinal));
}

public static class SubscriptionHwid
{
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false });
    public static string Origin(Uri uri)
    {
        var host = uri.IdnHost.ToLowerInvariant();
        if (host.Contains(':')) host = "[" + host.Trim('[', ']') + "]";
        return uri.Scheme.ToLowerInvariant() + "://" + host + (uri.IsDefaultPort ? "" : ":" + uri.Port);
    }
    public static bool IsValid(string value) => Regex.IsMatch(value, @"\A[A-Za-z0-9=\-]{10,64}\z");
    public static SubscriptionRequestIdentity Context(AppState state, ProxySubscription subscription, Func<string, string> resolve)
    {
        var enabled = Enum.IsDefined(subscription.HwidMode) && subscription.HwidMode != SubscriptionHwidMode.Block &&
            (subscription.HwidMode == SubscriptionHwidMode.Allow || state.SendSubscriptionHwid);
        var source = Origin(new Uri(SubscriptionService.NormalizeUrl(subscription.Url)));
        var origins = (subscription.HwidApprovedOrigins ?? []).ToArray();
        var manual = subscription.HwidOverride?.Trim();
        return new(source, enabled, origins,
            origin => string.IsNullOrWhiteSpace(manual) ? resolve(origin) : manual,
            state.SendSubscriptionHwid || origins.Contains(source, StringComparer.Ordinal));
    }
    public static bool Flag(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && values.Any(value => value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || value.Trim() == "1");
    public static bool BodyRequires(string body)
    {
        var text = body.Trim();
        if (!text.StartsWith('#'))
            try { text = Encoding.UTF8.GetString(Convert.FromBase64String(text)); } catch (FormatException) { }
        return text.Split('\n').Take(40).Any(line => Regex.IsMatch(line.Trim(), @"^#subscription-always-hwid-enable\s*:\s*(true|1)\s*$", RegexOptions.IgnoreCase));
    }
    public static string ContentWithoutDirective(string body)
    {
        var text = body;
        if (!text.TrimStart().StartsWith('#'))
            try { text = Encoding.UTF8.GetString(Convert.FromBase64String(text.Trim())); } catch (FormatException) { }
        var lines = text.Split('\n');
        bool Directive(string line) => Regex.IsMatch(line.Trim(), @"^#subscription-always-hwid-enable\s*:", RegexOptions.IgnoreCase);
        return lines.Any(Directive) ? string.Join('\n', lines.Where(line => !Directive(line))) : body;
    }
    public static void ClearImportedPermissions(AppState state)
    {
        state.SendSubscriptionHwid = false;
        foreach (var sub in state.Subscriptions)
        { sub.HwidMode = SubscriptionHwidMode.Auto; sub.HwidApprovedOrigins = []; sub.HwidOverride = null; }
    }
    public static async Task<(HttpResponseMessage Response, bool Sent, string Origin)> SendAsync(HttpClient client, string url,
        SubscriptionRequestIdentity? identity, CancellationToken token)
    {
        var uri = new Uri(SubscriptionService.NormalizeUrl(url));
        for (var hop = 0; hop <= 5; hop++)
        {
            var origin = Origin(uri);
            var sent = identity?.Allows(origin) == true;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("ior/" + UpdateReleasePolicy.InstalledVersion(typeof(SubscriptionService).Assembly));
            if (sent)
            {
                var id = identity!.Resolve(origin);
                if (!IsValid(id)) throw new FormatException("Некорректный HWID: нужны 10–64 символа, латинские буквы, цифры, = или -.");
                request.Headers.Add("x-hwid", id);
                request.Headers.Add("x-device-os", "Windows");
                request.Headers.Add("x-ver-os", Environment.OSVersion.Version.ToString());
                request.Headers.Add("x-device-model", "Windows PC");
            }
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                response.Dispose();
                if (next.Scheme is not ("http" or "https")) throw new IOException("Недопустимое перенаправление подписки.");
                if (sent && next.Scheme == "http") throw new IOException("Перенаправление подписки с HWID на HTTP отклонено.");
                uri = next; continue;
            }
            return (response, sent, origin);
        }
        throw new IOException("Слишком много перенаправлений подписки.");
    }
}
