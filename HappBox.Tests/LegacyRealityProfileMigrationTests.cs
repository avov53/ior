using HappBox;
using Xunit;
namespace HappBox.Tests;
public sealed class LegacyRealityProfileMigrationTests
{
    [Fact]public void LegacySubscriptionIsDueWithoutInventingDiscardedFlag()
    {
        var now=DateTime.UtcNow;var sub=new ProxySubscription{Id="old",NextRefreshUtc=now.AddHours(1)};
        var profile=new ProxyProfile{Protocol=ProxyProtocol.Vless,RealityEnabled=true,SubscriptionId=sub.Id,UnsupportedParameters=["support-x25519mlkem768"]};
        var state=new AppState{Subscriptions=[sub],Profiles=[profile]};
        Assert.Equal(1,LegacyRealityProfileMigration.ScheduleRefresh(state,now));
        Assert.True(SubscriptionRefreshSchedule.IsDue(sub,now));Assert.Null(profile.SupportX25519Mlkem768);
        Assert.Contains("support-x25519mlkem768",profile.UnsupportedParameters);
        Assert.False(CoreRuntimeContract.TryCreateStart(state,profile,out _,out var reason));Assert.Contains("Обновите",reason);
    }
    [Fact]public void DisabledAndHealthySubscriptionsKeepTheirSchedule()
    {
        var now=DateTime.UtcNow;var disabled=new ProxySubscription{Id="off",AutoRefreshEnabled=false,NextRefreshUtc=now.AddHours(2)};
        var healthy=new ProxySubscription{Id="ok",NextRefreshUtc=now.AddHours(3)};
        var profile=new ProxyProfile{Protocol=ProxyProtocol.Vless,RealityEnabled=true,SubscriptionId=disabled.Id,UnsupportedParameters=["support-x25519mlkem768"]};
        Assert.Equal(0,LegacyRealityProfileMigration.ScheduleRefresh(new AppState{Subscriptions=[disabled,healthy],Profiles=[profile]},now));
        Assert.Equal(now.AddHours(2),disabled.NextRefreshUtc);Assert.Equal(now.AddHours(3),healthy.NextRefreshUtc);
    }
    [Fact]public void CurrentImportedFlagAndOtherUnknownParametersAreNotMigrated()
    {
        var profile=ProfileParser.ParseMany("vless://11111111-1111-1111-1111-111111111111@edge.example:443?security=reality&fp=chrome&support-x25519mlkem768=true").Single();
        Assert.False(LegacyRealityProfileMigration.NeedsRefresh(profile));Assert.Equal("true",profile.SupportX25519Mlkem768);
        profile.SupportX25519Mlkem768=null;profile.UnsupportedParameters=["another-unknown"];
        Assert.False(LegacyRealityProfileMigration.NeedsRefresh(profile));
    }
}
