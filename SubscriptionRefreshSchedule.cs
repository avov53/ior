namespace HappBox;

public static class SubscriptionRefreshSchedule
{
    public static bool IsDue(ProxySubscription subscription, DateTime utcNow)
    {
        return (subscription.AutoRefreshEnabled||subscription.ServerRefreshIntervalHours.HasValue) &&
               (subscription.NextRefreshUtc is null || subscription.NextRefreshUtc <= utcNow);
    }

    public static void Normalize(ProxySubscription subscription, DateTime utcNow)
    {
        subscription.RefreshIntervalHours = Math.Clamp(subscription.RefreshIntervalHours <= 0 ? 6 : subscription.RefreshIntervalHours, 1, 168);
        var due=(subscription.LastUpdatedUtc??utcNow).AddHours(subscription.ServerRefreshIntervalHours??subscription.RefreshIntervalHours);
        subscription.NextRefreshUtc ??= due;
        if(subscription.ServerRefreshIntervalHours.HasValue&&subscription.NextRefreshUtc>due)subscription.NextRefreshUtc=due;
    }

    public static void ScheduleNext(ProxySubscription subscription, DateTime utcNow)
    {
        subscription.RefreshIntervalHours = Math.Clamp(subscription.RefreshIntervalHours <= 0 ? 6 : subscription.RefreshIntervalHours, 1, 168);
        subscription.NextRefreshUtc = utcNow.AddHours(subscription.ServerRefreshIntervalHours??subscription.RefreshIntervalHours);
    }
}
