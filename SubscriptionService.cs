using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HappBox;

public sealed record SubscriptionFetchResult(
    List<ProxyProfile> Profiles,
    int RejectedCount,
    string? SuggestedName,
    long UploadBytes,
    long DownloadBytes,
    long TotalBytes,
    DateTime? ExpireUtc,
    bool? RoutingEnabled,
    string? RoutingValue);

public sealed record SubscriptionMergeResult(int Added, int Updated, int Removed, string? Warning);

public static class SubscriptionService
{
    public static async Task<SubscriptionFetchResult> FetchAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "ior/1.2.2");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var parsed = ProfileParser.ParseManyDetailed(body);
        var info = ParseUserInfo(Header(response, "Subscription-Userinfo"));
        var title = DecodeProfileTitle(Header(response, "Profile-Title"));
        var routingEnabled = ParseOptionalBoolean(Header(response, "Routing-Enable"));
        var routing = Header(response, "Routing");

        return new SubscriptionFetchResult(
            parsed.Profiles,
            parsed.RejectedCount,
            title,
            info.Upload,
            info.Download,
            info.Total,
            info.ExpireUtc,
            routingEnabled,
            routing);
    }

    public static SubscriptionMergeResult Apply(
        AppState state,
        ProxySubscription subscription,
        SubscriptionFetchResult result)
    {
        if (result.Profiles.Count == 0)
        {
            throw new InvalidOperationException("The subscription contains no supported servers.");
        }

        var previous = state.Profiles.Where(x => x.SubscriptionId == subscription.Id).ToList();
        var previousByIdentity = previous
            .GroupBy(ProfileIdentity, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var next = new List<ProxyProfile>();
        var added = 0;
        var updated = 0;

        foreach (var incoming in result.Profiles)
        {
            var identity = ProfileIdentity(incoming);
            if (!unique.Add(identity)) continue;

            incoming.SubscriptionId = subscription.Id;
            ApplyUsage(incoming, result);
            if (previousByIdentity.TryGetValue(identity, out var current))
            {
                incoming.Id = current.Id;
                incoming.CountryCode = current.CountryCode;
                incoming.CountryName = current.CountryName;
                incoming.CountryLookupUtc = current.CountryLookupUtc;
                updated++;
            }
            else
            {
                added++;
            }

            next.Add(incoming);
        }

        var insertIndex = state.Profiles.FindIndex(x => x.SubscriptionId == subscription.Id);
        if (insertIndex < 0) insertIndex = state.Profiles.Count;
        state.Profiles.RemoveAll(x => x.SubscriptionId == subscription.Id);
        state.Profiles.InsertRange(Math.Min(insertIndex, state.Profiles.Count), next);

        var removed = previous.Count(x => next.All(y => y.Id != x.Id));
        if (string.IsNullOrWhiteSpace(state.SelectedProfileId) ||
            state.Profiles.All(x => x.Id != state.SelectedProfileId))
        {
            state.SelectedProfileId = next.FirstOrDefault()?.Id ?? state.Profiles.FirstOrDefault()?.Id;
        }

        if (!subscription.NameIsCustom && !string.IsNullOrWhiteSpace(result.SuggestedName))
        {
            subscription.Name = result.SuggestedName.Trim();
        }

        subscription.UploadBytes = result.UploadBytes;
        subscription.DownloadBytes = result.DownloadBytes;
        subscription.TotalBytes = result.TotalBytes;
        subscription.ExpireUtc = result.ExpireUtc;
        subscription.LastUpdatedUtc = DateTime.UtcNow;
        subscription.LastError = null;

        var warning = SynchronizeRouting(state, subscription, result);
        return new SubscriptionMergeResult(added, updated, removed, warning);
    }

    public static void Remove(AppState state, ProxySubscription subscription)
    {
        state.Profiles.RemoveAll(x => x.SubscriptionId == subscription.Id);
        state.HappRoutingProfiles.RemoveAll(x => x.ManagedBySubscriptionId == subscription.Id);
        state.Subscriptions.RemoveAll(x => x.Id == subscription.Id);
        if (state.SelectedHappRoutingProfileId is not null &&
            state.HappRoutingProfiles.All(x => x.Id != state.SelectedHappRoutingProfileId))
        {
            state.SelectedHappRoutingProfileId = state.HappRoutingProfiles.FirstOrDefault()?.Id;
        }
        if (state.SelectedProfileId is not null && state.Profiles.All(x => x.Id != state.SelectedProfileId))
        {
            state.SelectedProfileId = state.Profiles.FirstOrDefault()?.Id;
        }
    }

    public static string ProfileIdentity(ProxyProfile profile)
    {
        var parts = new string?[]
        {
            profile.Protocol.ToString(), profile.Server.ToLowerInvariant(), profile.Port.ToString(),
            profile.UserId, profile.Password, profile.Method, profile.Flow, profile.TlsEnabled.ToString(),
            profile.RealityEnabled.ToString(), profile.AllowInsecure.ToString(), profile.ServerName,
            profile.PublicKey, profile.ShortId, profile.Fingerprint, profile.Security, profile.AlterId?.ToString(),
            profile.Network, profile.Host, profile.Path, profile.ServiceName, profile.ObfsType,
            profile.ObfsPassword, profile.PrivateKey, profile.PeerPublicKey, profile.PreSharedKey,
            profile.LocalAddress, profile.AllowedIps, profile.PersistentKeepalive?.ToString(), profile.Mtu?.ToString()
        };
        var canonical = string.Join('\n', parts.Select(x => x?.Trim() ?? ""));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string DefaultName(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : "Subscription";
    }

    private static string? SynchronizeRouting(
        AppState state,
        ProxySubscription subscription,
        SubscriptionFetchResult result)
    {
        if (result.RoutingEnabled == false)
        {
            state.HappRoutingProfiles.RemoveAll(x => x.ManagedBySubscriptionId == subscription.Id);
            subscription.RoutingProfileId = null;
            if (state.SelectedHappRoutingProfileId is not null &&
                state.HappRoutingProfiles.All(x => x.Id != state.SelectedHappRoutingProfileId))
            {
                state.SelectedHappRoutingProfileId = state.HappRoutingProfiles.FirstOrDefault()?.Id;
            }
            return null;
        }

        if (result.RoutingEnabled != true) return null;
        var links = NormalizeRoutingItems(result.RoutingValue);
        if (links.Count == 0)
        {
            return "3x-ui enabled routing, but the Routing header is empty or invalid. The previous profile was kept.";
        }

        var profile = state.HappRoutingProfiles.FirstOrDefault(x => x.ManagedBySubscriptionId == subscription.Id);
        if (profile is null)
        {
            profile = new HappRoutingProfile
            {
                Name = RoutingName(links) ?? $"{subscription.Name} routing",
                Links = links,
                ManagedBySubscriptionId = subscription.Id
            };
            state.HappRoutingProfiles.Add(profile);
            if (string.IsNullOrWhiteSpace(state.SelectedHappRoutingProfileId))
            {
                state.SelectedHappRoutingProfileId = profile.Id;
            }
        }
        else
        {
            profile.Links = links;
            if (!profile.NameIsCustom)
            {
                profile.Name = RoutingName(links) ?? $"{subscription.Name} routing";
            }
        }

        subscription.RoutingProfileId = profile.Id;
        return null;
    }

    private static void ApplyUsage(ProxyProfile profile, SubscriptionFetchResult result)
    {
        profile.SubscriptionUploadBytes = result.UploadBytes;
        profile.SubscriptionDownloadBytes = result.DownloadBytes;
        profile.SubscriptionTotalBytes = result.TotalBytes;
        profile.SubscriptionExpireUtc = result.ExpireUtc;
    }

    private static string? Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values) ||
            response.Content.Headers.TryGetValues(name, out values))
        {
            return values.FirstOrDefault();
        }
        return null;
    }

    private static string? DecodeProfileTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (!text.StartsWith("base64:", StringComparison.OrdinalIgnoreCase)) return text;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(text[7..]));
        }
        catch
        {
            return null;
        }
    }

    private static bool? ParseOptionalBoolean(string? value)
    {
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static (long Upload, long Download, long Total, DateTime? ExpireUtc) ParseUserInfo(string? value)
    {
        long upload = 0, download = 0, total = 0;
        DateTime? expireUtc = null;
        foreach (var part in (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !long.TryParse(pair[1], out var number)) continue;
            if (pair[0].Equals("upload", StringComparison.OrdinalIgnoreCase)) upload = Math.Max(0, number);
            else if (pair[0].Equals("download", StringComparison.OrdinalIgnoreCase)) download = Math.Max(0, number);
            else if (pair[0].Equals("total", StringComparison.OrdinalIgnoreCase)) total = Math.Max(0, number);
            else if (pair[0].Equals("expire", StringComparison.OrdinalIgnoreCase) && number > 0)
                expireUtc = DateTimeOffset.FromUnixTimeSeconds(number).UtcDateTime;
        }
        return (upload, download, total, expireUtc);
    }

    private static List<string> NormalizeRoutingItems(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var trimmed = value.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}')) return [trimmed];
        return trimmed
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsRoutingItem)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsRoutingItem(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return true;

        var payload = value;
        if (value.StartsWith("happ://routing/", StringComparison.OrdinalIgnoreCase))
        {
            var marker = value.Contains("/onadd/", StringComparison.OrdinalIgnoreCase) ? "/onadd/" : "/add/";
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            payload = Uri.UnescapeDataString(value[(index + marker.Length)..]);
        }
        foreach (var candidate in DecodeCandidates(payload))
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (document.RootElement.ValueKind == JsonValueKind.Object) return true;
            }
            catch
            {
            }
        }
        return false;
    }

    private static string? RoutingName(IEnumerable<string> links)
    {
        foreach (var link in links)
        {
            var payload = link;
            if (link.StartsWith("happ://routing/", StringComparison.OrdinalIgnoreCase))
            {
                var marker = link.Contains("/onadd/", StringComparison.OrdinalIgnoreCase) ? "/onadd/" : "/add/";
                var index = link.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index >= 0) payload = Uri.UnescapeDataString(link[(index + marker.Length)..]);
            }

            foreach (var candidate in DecodeCandidates(payload))
            {
                try
                {
                    using var doc = JsonDocument.Parse(candidate);
                    if (doc.RootElement.TryGetProperty("Name", out var name) && !string.IsNullOrWhiteSpace(name.GetString()))
                        return name.GetString();
                }
                catch
                {
                }
            }
        }
        return null;
    }

    private static IEnumerable<string> DecodeCandidates(string payload)
    {
        yield return payload;
        var normalized = payload.Replace('-', '+').Replace('_', '/');
        var remainder = normalized.Length % 4;
        if (remainder > 0) normalized = normalized.PadRight(normalized.Length + 4 - remainder, '=');
        byte[] bytes;
        try { bytes = Convert.FromBase64String(normalized); }
        catch { yield break; }
        yield return Encoding.UTF8.GetString(bytes);
    }
}
