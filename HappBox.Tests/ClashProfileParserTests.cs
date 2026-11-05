using HappBox;
using System.Text.Json.Nodes;
using Xunit;

namespace HappBox.Tests;

public sealed class ClashProfileParserTests
{
    [Fact]
    public void SingBoxKeepsSimpleClashRuleOrderInsteadOfSortingIntoBuckets()
    {
        var route = ClashProfileParser.Parse("""
            rules:
              - DOMAIN,first.example,DIRECT
              - DOMAIN,second.example,REJECT
              - DOMAIN,third.example,PROXY
              - MATCH,PROXY
            """);
        var state = new AppState { HappRoutingProfiles = [route], SelectedHappRoutingProfileId = route.Id };
        var server = new ProxyProfile { Protocol = ProxyProtocol.Vless, Server = "edge.example", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111" };

        // The legacy compiler remains usable internally, but its engine is retired.
        var rules = JsonNode.Parse(SingBoxConfigBuilder.Build(state, server))!["route"]!["rules"]!.AsArray();
        var ordered = rules.Where(rule => rule?["domain"] is not null)
            .Select(rule => rule!["domain"]![0]!.GetValue<string>()).ToArray();
        Assert.Equal(["edge.example", "first.example", "second.example", "third.example"], ordered);
    }

    [Fact]
    public void SingBoxRejectsClashGroupBeforeChangingNetwork()
    {
        var route = ClashProfileParser.Parse("""
            proxy-groups:
              - name: Auto
                type: select
            rules:
              - DOMAIN-SUFFIX,example.com,Auto
              - MATCH,DIRECT
            """);
        var state = new AppState { HappRoutingProfiles = [route], SelectedHappRoutingProfileId = route.Id };
        var server = new ProxyProfile { Protocol = ProxyProtocol.Hysteria2, Server = "edge.example", Port = 443 };

        Assert.False(SingBoxClashRuleCompiler.TryCompile(route, out _, out var reason));
        Assert.Contains("Группа", reason);
        Assert.Throws<FormatException>(() => SingBoxConfigBuilder.Build(state, server));
    }

    [Fact]
    public void ImportsSupportedRulesAndPreservesUnmappedRules()
    {
        const string yaml = """
            proxies:
              - name: Europe
                type: vless
            proxy-groups:
              - name: Auto
                type: url-test
            rules:
              - DOMAIN-SUFFIX,example.com,Proxy
              - IP-CIDR,10.0.0.0/8,DIRECT
              - DOMAIN,ads.example,REJECT
              - GEOIP,CN,DIRECT
              - MATCH,Proxy
            """;

        var route = ClashProfileParser.Parse(yaml, "Imported");

        Assert.Equal(RoutingProfileSource.Clash, route.Source);
        Assert.Equal("Imported", route.Name);
        Assert.Contains("example.com", route.ProxySites);
        Assert.Contains("10.0.0.0/8", route.DirectIp);
        Assert.Contains("ads.example", route.BlockDomains);
        Assert.Contains("Auto", route.ClashProxyGroups);
        Assert.Equal(2, route.ClashUnmappedRules.Count);
        Assert.Equal(new[] { "DOMAIN-SUFFIX", "IP-CIDR", "DOMAIN", "GEOIP", "MATCH" }, route.ClashOrderedRules.Select(rule => rule.Kind));
        Assert.Equal("Proxy", route.ClashOrderedRules[0].Target);
        Assert.Equal(yaml, route.OriginalClashYaml);
    }

    [Fact]
    public void PreservesProxyGroupAndNoResolveWithoutSilentlyActivatingThem()
    {
        const string yaml = """
            proxy-groups:
              - name: Auto
            rules:
              - GEOIP,CN,Auto,no-resolve
              - RULE-SET,media,Auto
              - DOMAIN-SUFFIX,example.com,Auto
              - MATCH,DIRECT
            """;

        var route = ClashProfileParser.Parse(yaml);

        Assert.Equal(4, route.ClashOrderedRules.Count);
        Assert.True(route.ClashOrderedRules[0].NoResolve);
        Assert.Equal("Auto", route.ClashOrderedRules[2].Target);
        Assert.Equal(3, route.ClashUnmappedRules.Count);
    }
}
