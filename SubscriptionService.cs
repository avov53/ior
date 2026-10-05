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
    string? RoutingValue,
    string? SupportUrl = null,
    string? ProfileWebPageUrl = null,
    string? Announcement = null,
    bool HasUserInfo = false,
    bool HasTotal = false,
    int? RefreshIntervalHours = null,
    bool IsClashYaml = false,
    HappRoutingProfile? ClashRouting = null,
    string? ImportWarning = null,
    int RemovedProtocolCount = 0);

public sealed record SubscriptionMergeResult(int Added, int Updated, int Removed, string? Warning);

public static class SubscriptionService
{
    public static string NormalizeUrl(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
            throw new FormatException("Некорректный адрес подписки.");
        if (uri.Scheme is "http" or "https") return uri.AbsoluteUri;
        if (!uri.Scheme.Equals("clash", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Подписка должна использовать HTTPS, HTTP или ссылку Clash с параметром url.");
        var pair = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .FirstOrDefault(parts => parts.Length == 2 && parts[0].Equals("url", StringComparison.OrdinalIgnoreCase));
        if (pair is null || !Uri.TryCreate(Uri.UnescapeDataString(pair[1]), UriKind.Absolute, out var target) ||
            target.Scheme is not ("http" or "https"))
            throw new FormatException("В Clash-ссылке нет допустимого HTTP(S)-адреса подписки.");
        return target.AbsoluteUri;
    }

    public static async Task<SubscriptionFetchResult> FetchAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, NormalizeUrl(url));
        request.Headers.TryAddWithoutValidation("User-Agent", "ior/1.2.2");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var isClashYaml = ClashSubscriptionParser.LooksLikeClash(body);
        var clash = isClashYaml ? ClashSubscriptionParser.Parse(body) : null;
        string? importWarning=null;
        if(clash?.Routing is { } embeddedRoute)
            try { await NativeRuleProviderCache.PrepareAsync(embeddedRoute,client,cancellationToken); }
            catch(FormatException error) { importWarning=error.Message+". Маршрут сохранён, но подключение с отсутствующим RULE-SET будет заблокировано."; }
        if(!isClashYaml && body.TrimStart().StartsWith('{'))
        {
            using var json=JsonDocument.Parse(body);
            if(json.RootElement.TryGetProperty("routing",out _) || json.RootElement.TryGetProperty("route",out _))
                importWarning="Серверы JSON импортированы. Встроенная маршрутизация Xray/sing-box не преобразована автоматически; выберите отдельный маршрут ior/Happ/Mihomo.";
        }
        var parsed = isClashYaml
            ? new ProfileParseResult(clash!.Profiles, clash.RejectedCount, clash.RemovedProtocolCount)
            : ProfileParser.ParseManyDetailed(body);
        var userInfoHeader = Header(response, "Subscription-Userinfo");
        var info = ParseUserInfo(userInfoHeader);
        var title = DecodeProfileTitle(Header(response, "Profile-Title"));
        var routingEnabled = ParseOptionalBoolean(Header(response, "Routing-Enable"));
        var routing = Header(response, "Routing");
        var supportUrl = SafeWebUrl(Header(response, "Support-Url"));
        var profileWebPageUrl = SafeWebUrl(Header(response, "Profile-Web-Page-Url"));
        var announcement = DecodeAnnouncement(Header(response, "Announce"));
        var refreshInterval = int.TryParse(Header(response, "Profile-Update-Interval"), out var hours) && hours is >= 1 and <= 168
            ? hours : (int?)null;

        return new SubscriptionFetchResult(
            parsed.Profiles,
            parsed.RejectedCount,
            title,
            info.Upload,
            info.Download,
            info.Total,
            info.ExpireUtc,
            routingEnabled,
            routing,
            supportUrl,
            profileWebPageUrl,
            announcement,
            !string.IsNullOrWhiteSpace(userInfoHeader),
            info.HasTotal,
            refreshInterval,
            isClashYaml,
            clash?.Routing,
            importWarning, parsed.RemovedProtocolCount);
    }

    public static SubscriptionMergeResult Apply(
        AppState state,
        ProxySubscription subscription,
        SubscriptionFetchResult result)
    {
        var removedInputs = result.Profiles.Count(RemovedProtocolPolicy.IsRemoved);
        result = result with { Profiles = result.Profiles.Where(profile => !RemovedProtocolPolicy.IsRemoved(profile)).ToList(),
            RemovedProtocolCount = result.RemovedProtocolCount + removedInputs, RejectedCount = result.RejectedCount + removedInputs };
        if (result.Profiles.Count == 0 && (result.RemovedProtocolCount == 0 || result.RejectedCount > result.RemovedProtocolCount))
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

        foreach (var incoming in result.Profiles.Where(profile => !RemovedProtocolPolicy.IsRemoved(profile)))
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

        var hasUserInfo = HasUserInfo(result);
        var hasTotal = result.HasTotal || result.TotalBytes > 0;
        subscription.HasUsageInfo = hasTotal;
        subscription.UploadBytes = hasUserInfo ? result.UploadBytes : null;
        subscription.DownloadBytes = hasUserInfo ? result.DownloadBytes : null;
        subscription.TotalBytes = hasTotal ? result.TotalBytes : null;
        subscription.ExpireUtc = result.ExpireUtc;
        subscription.SupportUrl = result.SupportUrl;
        subscription.ProfileWebPageUrl = result.ProfileWebPageUrl;
        subscription.Announcement = result.Announcement;
        subscription.ServerRefreshIntervalHours = result.RefreshIntervalHours;
        if (!subscription.RefreshIntervalIsCustom && result.RefreshIntervalHours is { } serverHours)
            subscription.RefreshIntervalHours = serverHours;
        subscription.LastUpdatedUtc = DateTime.UtcNow;
        subscription.LastError = null;
        subscription.LastImportedServers = next.Count;
        subscription.LastRejectedServers = result.RejectedCount;

        var warning = string.Join(" ",new[]{SynchronizeRouting(state, subscription, result),result.ImportWarning, result.RemovedProtocolCount > 0 ? RemovedProtocolPolicy.Message : null}.Where(value=>!string.IsNullOrWhiteSpace(value)));
        if(warning.Length==0)warning=null;
        return new SubscriptionMergeResult(added, updated, removed, warning);
    }

    public static void Remove(AppState state, ProxySubscription subscription)
    {
        var removedRouteId = subscription.RoutingProfileId;
        state.Profiles.RemoveAll(x => x.SubscriptionId == subscription.Id);
        state.Subscriptions.RemoveAll(x => x.Id == subscription.Id);
        foreach (var workspace in state.ConnectionProfiles.Where(profile => profile.SubscriptionId == subscription.Id))
        {
            workspace.SubscriptionId = null;
            if (workspace.RoutingProfileId == removedRouteId) workspace.RoutingProfileId = null;
            workspace.DefaultServerId = state.Profiles.FirstOrDefault(profile => workspace.ConfigIds.Contains(profile.Id))?.Id;
        }
        RemoveUnusedManagedRoute(state, subscription.RoutingProfileId);
        if (state.SelectedHappRoutingProfileId is not null &&
            state.HappRoutingProfiles.All(x => x.Id != state.SelectedHappRoutingProfileId))
        {
            state.SelectedHappRoutingProfileId = state.HappRoutingProfiles.FirstOrDefault()?.Id;
        }
        if (state.SelectedProfileId is not null && state.Profiles.All(x => x.Id != state.SelectedProfileId))
        {
            state.SelectedProfileId = state.ConnectionProfiles.FirstOrDefault(profile => profile.Id == state.SelectedConnectionProfileId)?.DefaultServerId;
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
        // Preserve identities of existing subscription protocols across migration.
        if (profile.Protocol is ProxyProtocol.Socks or ProxyProtocol.Http)
            canonical += "\n" + (profile.Username?.Trim() ?? "");
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
        if (result.IsClashYaml)
        {
            var oldId = subscription.RoutingProfileId;
            if (result.ClashRouting is null)
            {
                subscription.RoutingProfileId = null;
                RemoveUnusedManagedRoute(state, oldId);
                return null;
            }

            var incoming = result.ClashRouting;
            var current = state.HappRoutingProfiles.FirstOrDefault(route => route.Id == oldId &&
                route.ManagedBySubscriptionId == subscription.Id);
            if (current is null)
            {
                incoming.ManagedBySubscriptionId = subscription.Id;
                incoming.Name = $"{subscription.Name} · Clash";
                state.HappRoutingProfiles.Add(incoming);
                subscription.RoutingProfileId = incoming.Id;
                RemoveUnusedManagedRoute(state, oldId);
            }
            else
            {
                incoming.Id = current.Id;
                incoming.ManagedBySubscriptionId = subscription.Id;
                incoming.NameIsCustom = current.NameIsCustom;
                if (current.NameIsCustom) incoming.Name = current.Name;
                else incoming.Name = $"{subscription.Name} · Clash";
                state.HappRoutingProfiles[state.HappRoutingProfiles.IndexOf(current)] = incoming;
            }
            return incoming.ClashUnmappedRules.Count > 0
                ? $"Clash-маршрут сохранён вместе с подпиской. {incoming.ClashUnmappedRules.Count} правил требуют проверки совместимости с выбранным ядром; неподдерживаемое сочетание будет заблокировано до подключения."
                : null;
        }

        if (result.RoutingEnabled == false)
        {
            var oldId = subscription.RoutingProfileId;
            subscription.RoutingProfileId = null;
            RemoveUnusedManagedRoute(state, oldId);
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

        var oldRouteId = subscription.RoutingProfileId;
        var profile = state.HappRoutingProfiles.FirstOrDefault(x => x.Source == RoutingProfileSource.Happ && x.Links.SequenceEqual(links, StringComparer.OrdinalIgnoreCase));
        profile ??= state.HappRoutingProfiles.FirstOrDefault(x => x.Id == oldRouteId &&
            !state.Subscriptions.Any(other => other.Id != subscription.Id && other.RoutingProfileId == oldRouteId));
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
        if (oldRouteId != profile.Id) RemoveUnusedManagedRoute(state, oldRouteId);
        return null;
    }

    private static void RemoveUnusedManagedRoute(AppState state, string? routeId)
    {
        if (routeId is null || state.Subscriptions.Any(item => item.RoutingProfileId == routeId)) return;
        state.HappRoutingProfiles.RemoveAll(route => route.Id == routeId && route.ManagedBySubscriptionId is not null);
        if (state.SelectedHappRoutingProfileId == routeId && state.HappRoutingProfiles.All(route => route.Id != routeId))
            state.SelectedHappRoutingProfileId = state.HappRoutingProfiles.FirstOrDefault()?.Id;
    }

    private static void ApplyUsage(ProxyProfile profile, SubscriptionFetchResult result)
    {
        var hasUserInfo = HasUserInfo(result);
        var hasTotal = result.HasTotal || result.TotalBytes > 0;
        profile.SubscriptionUploadBytes = hasUserInfo ? result.UploadBytes : null;
        profile.SubscriptionDownloadBytes = hasUserInfo ? result.DownloadBytes : null;
        profile.SubscriptionTotalBytes = hasTotal ? result.TotalBytes : null;
        profile.SubscriptionExpireUtc = result.ExpireUtc;
    }

    private static bool HasUserInfo(SubscriptionFetchResult result) => result.HasUserInfo ||
        result.UploadBytes > 0 || result.DownloadBytes > 0 || result.TotalBytes > 0;

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

    private static string? SafeWebUrl(string? value)
    {
        var text = value?.Trim();
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? uri.AbsoluteUri : null;
    }

    private static string? DecodeAnnouncement(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                text = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(text[7..]));
            }
            catch (FormatException) { return null; }
            catch (DecoderFallbackException) { return null; }
        }
        text = new string(text.Where(character => !char.IsControl(character) || character is '\n' or '\r' or '\t').ToArray()).Trim();
        return text.Length == 0 ? null : text[..Math.Min(text.Length, 4000)];
    }

    private static bool? ParseOptionalBoolean(string? value)
    {
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static (long Upload, long Download, long Total, bool HasTotal, DateTime? ExpireUtc) ParseUserInfo(string? value)
    {
        long upload = 0, download = 0, total = 0;
        var hasTotal = false;
        DateTime? expireUtc = null;
        foreach (var part in (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !long.TryParse(pair[1], out var number)) continue;
            if (pair[0].Equals("upload", StringComparison.OrdinalIgnoreCase)) upload = Math.Max(0, number);
            else if (pair[0].Equals("download", StringComparison.OrdinalIgnoreCase)) download = Math.Max(0, number);
            else if (pair[0].Equals("total", StringComparison.OrdinalIgnoreCase))
            {
                total = Math.Max(0, number);
                hasTotal = true;
            }
            else if (pair[0].Equals("expire", StringComparison.OrdinalIgnoreCase) && number > 0)
                expireUtc = DateTimeOffset.FromUnixTimeSeconds(number).UtcDateTime;
        }
        return (upload, download, total, hasTotal, expireUtc);
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
