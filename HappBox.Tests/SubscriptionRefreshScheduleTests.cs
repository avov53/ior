using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class SubscriptionRefreshScheduleTests
{
    [Fact]
    public void ServerIntervalOverridesDisabledAndRetainsUserPreference()
    {
        var now=DateTime.UtcNow;
        var subscription=new ProxySubscription{AutoRefreshEnabled=false,RefreshIntervalHours=6,ServerRefreshIntervalHours=1,LastUpdatedUtc=now.AddHours(-2)};
        SubscriptionRefreshSchedule.Normalize(subscription,now);
        Assert.True(SubscriptionRefreshSchedule.IsDue(subscription,now));
        SubscriptionRefreshSchedule.ScheduleNext(subscription,now);
        Assert.Equal(now.AddHours(1),subscription.NextRefreshUtc);
        subscription.ServerRefreshIntervalHours=null;
        Assert.False(SubscriptionRefreshSchedule.IsDue(subscription,now.AddDays(1)));
        Assert.Equal(6,subscription.RefreshIntervalHours);
    }
    [Fact]
    public void SubscriptionsBecomeDueIndependently()
    {
        var now = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        var sixHours = new ProxySubscription { AutoRefreshEnabled = true, NextRefreshUtc = now };
        var twelveHours = new ProxySubscription { AutoRefreshEnabled = true, NextRefreshUtc = now.AddHours(6) };

        Assert.True(SubscriptionRefreshSchedule.IsDue(sixHours, now));
        Assert.False(SubscriptionRefreshSchedule.IsDue(twelveHours, now));
    }

    [Fact]
    public void DisabledSubscriptionKeepsIntervalAndIsNotDue()
    {
        var now = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        var subscription = new ProxySubscription { AutoRefreshEnabled = false, RefreshIntervalHours = 6, NextRefreshUtc = now.AddMinutes(-1) };

        Assert.False(SubscriptionRefreshSchedule.IsDue(subscription, now));
        Assert.Equal(6, subscription.RefreshIntervalHours);
    }
}
