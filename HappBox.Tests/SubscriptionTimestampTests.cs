using System.Text.Json;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class SubscriptionTimestampTests
{
    [Theory]
    [InlineData("2026-10-04T17:42:00", 17)]
    [InlineData("2026-10-04T17:42:00Z", 17)]
    [InlineData("2026-10-04T20:42:00+03:00", 17)]
    public void LegacyAndOffsetTimestampsHaveOneExplicitUtcInstant(string timestamp, int hour)
    {
        var subscription = JsonSerializer.Deserialize<ProxySubscription>($$"""{"LastUpdatedUtc":"{{timestamp}}","NextRefreshUtc":"{{timestamp}}"}""")!;
        Assert.Equal(DateTimeKind.Utc, subscription.LastUpdatedUtc!.Value.Kind);
        Assert.Equal(hour, subscription.LastUpdatedUtc.Value.Hour);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(subscription));
        Assert.EndsWith("Z", json.RootElement.GetProperty("LastUpdatedUtc").GetString());
        Assert.EndsWith("Z", json.RootElement.GetProperty("NextRefreshUtc").GetString());
    }

    [Fact]
    public void FailedAttemptSchedulesNextWithoutChangingSuccessfulUpdateOrServers()
    {
        var last = new DateTime(2026, 10, 4, 17, 12, 0, DateTimeKind.Utc);
        var now = last.AddMinutes(30);
        var subscription = new ProxySubscription { LastUpdatedUtc = last, RefreshIntervalHours = 1, LastError = "request failed" };
        var state = new AppState();
        var server = new ProxyProfile { SubscriptionId = subscription.Id, Name = "Existing" };
        state.Profiles.Add(server);
        SubscriptionRefreshSchedule.ScheduleNext(subscription, now);
        Assert.Equal(last, subscription.LastUpdatedUtc);
        Assert.Equal(now.AddHours(1), subscription.NextRefreshUtc);
        Assert.Equal("request failed", subscription.LastError);
        Assert.Same(server, Assert.Single(state.Profiles));
    }
}
