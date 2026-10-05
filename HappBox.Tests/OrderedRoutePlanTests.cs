using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class OrderedRoutePlanTests
{
    [Fact]
    public void ClashPreservesRawOrderAndTarget()
    {
        var profile = ClashProfileParser.Parse("""
            rules:
              - DOMAIN,first.example,DIRECT
              - DOMAIN,second.example,REJECT
              - MATCH,PROXY
            """);
        Assert.True(OrderedRoutePlan.TryCreate(profile, out var rules, out var reason), reason);
        Assert.Equal(["DIRECT", "REJECT", "PROXY"], rules.Select(rule => rule.Target));
        Assert.Equal(["DOMAIN", "DOMAIN", "MATCH"], rules.Select(rule => rule.Kind));
    }

    [Fact]
    public void IorLegacyBucketsConvertWithoutChangingEffectiveOrder()
    {
        var profile = new HappRoutingProfile { Source = RoutingProfileSource.Ior,
            BlockDomains = ["ads.example"], ProxySites = ["stream.example"], DirectIp = ["10.0.0.0/8"],
            GlobalProxy = false };
        Assert.True(OrderedRoutePlan.TryCreate(profile, out var rules, out var reason), reason);
        Assert.Equal(["REJECT", "PROXY", "DIRECT", "DIRECT"], rules.Select(rule => rule.Target));
        Assert.Equal("MATCH", rules[^1].Kind);
    }

    [Fact]
    public void HappPreservesConfiguredActionOrder()
    {
        var profile = new HappRoutingProfile { Source = RoutingProfileSource.Happ, Links = ["""
            {"RouteOrder":"direct-block-proxy","GlobalProxy":true,"DirectSites":["local.example"],
             "BlockSites":["ads.example"],"ProxySites":["stream.example"]}
            """] };
        Assert.True(OrderedRoutePlan.TryCreate(profile, out var rules, out var reason), reason);
        Assert.Equal(["DIRECT", "REJECT", "PROXY", "PROXY"], rules.Select(rule => rule.Target));
    }

    [Fact]
    public void UnknownHappActionFailsClosed()
    {
        var profile = new HappRoutingProfile { Source = RoutingProfileSource.Happ,
            Links = ["{\"RouteOrder\":\"direct-magic-proxy\",\"GlobalProxy\":true}"] };
        Assert.False(OrderedRoutePlan.TryCreate(profile, out _, out var reason));
        Assert.Contains("RouteOrder", reason);
    }
}
