namespace HappBox;

/// <summary>Old saved profiles kept only the unknown parameter name, not its value.</summary>
public static class LegacyRealityProfileMigration
{
    public static bool NeedsRefresh(ProxyProfile profile) => profile.Protocol == ProxyProtocol.Vless &&
        profile.RealityEnabled && profile.SupportX25519Mlkem768 is null &&
        profile.UnsupportedParameters?.Contains("support-x25519mlkem768", StringComparer.OrdinalIgnoreCase) == true;

    // Run only when loading state. Never invent the discarded flag value or drop validation.
    public static int ScheduleRefresh(AppState state, DateTime utcNow)
    {
        var subscriptions = state.Profiles.Where(NeedsRefresh).Select(p => p.SubscriptionId).OfType<string>().ToHashSet();
        var count = 0;
        foreach (var sub in state.Subscriptions.Where(s => subscriptions.Contains(s.Id) && (s.AutoRefreshEnabled || s.ServerRefreshIntervalHours.HasValue)))
        { sub.NextRefreshUtc = utcNow; count++; }
        return count;
    }
}
