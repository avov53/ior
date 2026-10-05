using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class NativeImportAndRoutingTests
{
    [Fact]
    public void JsonSingBoxSubscriptionKeepsRealityAndHysteriaCredentials()
    {
        var result=ProfileParser.ParseManyDetailed("""
            {"outbounds":[{"type":"direct","tag":"direct"},{"type":"vless","tag":"one","server":"edge.example","server_port":443,"uuid":"11111111-1111-1111-1111-111111111111","flow":"xtls-rprx-vision","tls":{"enabled":true,"server_name":"cover.example","reality":{"enabled":true,"public_key":"public","short_id":"aa"}}},{"type":"hysteria2","server":"hy.example","server_port":443,"password":"synthetic","tls":{"enabled":true,"server_name":"hy.example"}}]}
            """);
        Assert.Equal(2,result.Profiles.Count);Assert.Equal(0,result.RejectedCount);
        Assert.True(result.Profiles[0].RealityEnabled);Assert.Equal("cover.example",result.Profiles[0].ServerName);
        Assert.Equal(ProxyProtocol.Hysteria2,result.Profiles[1].Protocol);Assert.Equal("synthetic",result.Profiles[1].Password);
    }
    [Fact]
    public void JsonMihomoImportsOrderedRoutingWithServers()
    {
        var body="""
            {"proxies":[{"name":"one","type":"hysteria2","server":"hy.example","port":443,"password":"synthetic","sni":"hy.example"}],"rules":["DOMAIN-SUFFIX,example.com,DIRECT","GEOIP,ru,DIRECT","MATCH,PROXY"]}
            """;
        Assert.True(ClashSubscriptionParser.LooksLikeClash(body));
        var parsed=ClashSubscriptionParser.Parse(body);
        Assert.Single(parsed.Profiles);Assert.NotNull(parsed.Routing);
        Assert.True(OrderedRoutePlan.TryCreate(parsed.Routing!,out var rules,out var reason),reason);
        Assert.Equal(new[]{"DOMAIN-SUFFIX","GEOIP","MATCH"},rules.Select(rule=>rule.Kind));
    }
    [Fact]
    public void XrayJsonPreservesTransportAndDoesNotInventAGroup()
    {
        var parsed=JsonSubscriptionParser.Parse("""
            {"outbounds":[{"protocol":"vless","tag":"one","settings":{"vnext":[{"address":"edge.example","port":443,"users":[{"id":"11111111-1111-1111-1111-111111111111","flow":"xtls-rprx-vision"}]}]},"streamSettings":{"network":"tcp","security":"reality","realitySettings":{"serverName":"cover.example","publicKey":"public","shortId":"aa","fingerprint":"chrome"}}}]}
            """);
        var profile=Assert.Single(parsed.Profiles);Assert.Equal("xtls-rprx-vision",profile.Flow);Assert.Equal("chrome",profile.Fingerprint);
        Assert.True(profile.RealityEnabled);
    }
    [Fact]
    public void PackagedRussianGeoipExpandsWithoutChangingTarget()
    {
        var rule=new OrderedRouteRule {Kind="GEOIP",Value="ru",Target="DIRECT",Raw="GEOIP,ru,DIRECT",NoResolve=true};
        var expanded=NativeGeoData.Expand(rule,Path.Combine(AppContext.BaseDirectory,"core","xray"));
        var set=Assert.Single(expanded);Assert.Equal("IP-SET",set.Kind);Assert.Equal("DIRECT",set.Target);Assert.True(set.NoResolve);Assert.Contains('/',set.Value);
    }
    [Fact]
    public void ClassicalRuleSetKeepsOrderAndNoResolve()
    {
        var provider=new RouteProviderDefinition {Name="test",Format="yaml",Behavior="classical"};
        var rules=NativeRuleProviderCache.Parse(provider,"payload:\n  - DOMAIN-SUFFIX,example.com\n  - IP-CIDR,203.0.113.0/24,no-resolve\n","DIRECT",false);
        Assert.Equal("DOMAIN-SUFFIX",rules[0].Kind);Assert.True(rules[1].NoResolve);Assert.All(rules,rule=>Assert.Equal("DIRECT",rule.Target));
    }
}
