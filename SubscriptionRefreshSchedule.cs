namespace HappBox;

public static class SubscriptionRefreshSchedule
{
    public static bool IsDue(ProxySubscription subscription, DateTime utcNow)
    {
        return subscription.AutoRefreshEnabled &&
               (subscription.NextRefreshUtc is null || subscription.NextRefreshUtc <= utcNow);
    }

    public static void Normalize(ProxySubscription subscription, DateTime utcNow)
    {
        subscription.RefreshIntervalHours = Math.Clamp(subscription.RefreshIntervalHours <= 0 ? 12 : subscription.RefreshIntervalHours, 1, 168);
        subscription.NextRefreshUtc ??= (subscription.LastUpdatedUtc ?? utcNow).AddHours(subscription.RefreshIntervalHours);
    }

    public static void ScheduleNext(ProxySubscription subscription, DateTime utcNow)
    {
        subscription.RefreshIntervalHours = Math.Clamp(subscription.RefreshIntervalHours <= 0 ? 12 : subscription.RefreshIntervalHours, 1, 168);
        subscription.NextRefreshUtc = utcNow.AddHours(subscription.RefreshIntervalHours);
    }
}
