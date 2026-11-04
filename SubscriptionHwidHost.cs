namespace HappBox;

public sealed partial class MainForm
{
    private readonly HttpClient _subscriptionClient = SubscriptionHwid.CreateClient();
    private SubscriptionIdentity? _subscriptionIdentity;
    private string? _hwidConsentSubscriptionId;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _knownHwidSecrets = new(StringComparer.Ordinal);
    private SubscriptionRequestIdentity SubscriptionIdentityContext(ProxySubscription sub)
    {
        var context = SubscriptionHwid.Context(_state, sub, origin => (_subscriptionIdentity ??= new SubscriptionIdentity(_appDir)).ForOrigin(origin));
        return context with { Resolve = origin =>
        {
            var id = context.Resolve(origin);
            _knownHwidSecrets.TryAdd(id, 0); _knownHwidSecrets.TryAdd(id.ToUpperInvariant(), 0); _knownHwidSecrets.TryAdd(id.ToLowerInvariant(), 0);
            return id;
        } };
    }
    private async Task<SubscriptionFetchResult> FetchSubscriptionAsync(string url, CancellationToken token = default, ProxySubscription? subscription = null)
    {
        var normalized = SubscriptionService.NormalizeUrl(url);
        subscription ??= _state.Subscriptions.FirstOrDefault(item => item.Url.Equals(normalized, StringComparison.Ordinal));
        subscription ??= new ProxySubscription { Url = normalized };
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(Math.Clamp(subscription.RequestTimeoutMs,5000,15000));
        return await SubscriptionService.FetchAsync(_subscriptionClient, normalized, deadline.Token, SubscriptionIdentityContext(subscription), ruleProviderClient: _httpClient);
    }
    private void RememberHwidFailure(ProxySubscription sub, SubscriptionHwidException error, bool prompt)
    {
        sub.LastError = error.Message; sub.HwidFailureCode = error.Code;
        if (error.Code is "required" or "httpsRequired")
        {
            sub.RequiresHwid = true; sub.HwidRequiredOrigin = error.Origin;
            if (prompt && error.Code == "required") _hwidConsentSubscriptionId ??= sub.Id;
        }
        Log("Subscription HWID: " + error.Code + " · " + SubscriptionDisplayHost(sub.Url));
        SaveState(); RefreshProfiles(); PushWebSnapshot();
    }
    private bool AwaitingHwidPermission(ProxySubscription sub)
    {
        if (!sub.RequiresHwid) return false;
        var context = SubscriptionIdentityContext(sub);
        return !context.Allows(sub.HwidRequiredOrigin ?? context.SourceOrigin);
    }
    private async Task ApproveSubscriptionHwidAsync(ProxySubscription sub)
    {
        var origin = sub.HwidRequiredOrigin ?? SubscriptionIdentityContext(sub).SourceOrigin;
        if (!origin.StartsWith("https://", StringComparison.Ordinal)) throw new InvalidOperationException("Для передачи HWID нужен HTTPS-адрес подписки.");
        if (!string.IsNullOrWhiteSpace(sub.HwidOverride) && !SubscriptionHwid.IsValid(sub.HwidOverride.Trim())) throw new FormatException("Некорректный ручной HWID.");
        sub.HwidMode = SubscriptionHwidMode.Allow;
        if (!sub.HwidApprovedOrigins.Contains(origin, StringComparer.Ordinal)) sub.HwidApprovedOrigins.Add(origin);
        _hwidConsentSubscriptionId = null; SaveState();
        await RefreshSubscriptionAsync(sub); PushWebSnapshot();
    }
}
