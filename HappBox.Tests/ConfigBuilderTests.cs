using System.Text.Json.Nodes;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class ConfigBuilderTests
{
    [Fact]
    public void TunUsesNormalOptions()
    {
        var state = new AppState
        {
            TunEnabled = true,
            MixedProxyEnabled = false,
            AutoRoute = true,
            StrictRoute = true
        };

        var tun = BuildTun(state);

        Assert.True(tun["auto_route"]!.GetValue<bool>());
        Assert.True(tun["strict_route"]!.GetValue<bool>());
        Assert.Null(tun["stack"]);
        Assert.Null(tun["endpoint_independent_nat"]);
        Assert.Null(tun["route_address"]);
        Assert.Equal(9000, tun["mtu"]!.GetValue<int>());
        Assert.Equal(["91.186.217.186/32"], tun["route_exclude_address"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void HappRoutingWithoutGlobalProxyKeepsUnmatchedTrafficDirect()
    {
        var state = new AppState
        {
            TunEnabled = true,
            HappRoutingProfiles =
            [
                new HappRoutingProfile
                {
                    Id = "managed",
                    Name = "Managed",
                    Links = ["{\"ProxySites\":[\"youtube.com\"],\"GlobalProxy\":false}"]
                }
            ],
            SelectedHappRoutingProfileId = "managed"
        };

        var root = JsonNode.Parse(SingBoxConfigBuilder.Build(state, Vless()))!.AsObject();

        Assert.Equal("direct", root["route"]!["final"]!.GetValue<string>());
    }

    [Fact]
    public void DnsStrategyComesFromStateForTcpProfiles()
    {
        var state = new AppState
        {
            TunEnabled = true,
            DnsStrategy = "prefer_ipv6"
        };

        var root = JsonNode.Parse(SingBoxConfigBuilder.Build(state, Vless()))!.AsObject();

        Assert.Equal("prefer_ipv6", root["dns"]!["strategy"]!.GetValue<string>());
        Assert.Equal("prefer_ipv6", root["route"]!["default_domain_resolver"]!["strategy"]!.GetValue<string>());
        Assert.Empty(root["dns"]!["rules"]!.AsArray());
    }

    [Fact]
    public void Hysteria2DoesNotAddHotspotSpecificOptions()
    {
        var state = new AppState { TunEnabled = true };

        var root = JsonNode.Parse(SingBoxConfigBuilder.Build(state, Hysteria2()))!.AsObject();
        var outbound = root["outbounds"]!.AsArray()
            .Select(x => x!.AsObject())
            .Single(x => x["tag"]!.GetValue<string>() == "proxy");

        Assert.Null(outbound["udp_fragment"]);
        Assert.Null(outbound["connect_timeout"]);
    }

    private static JsonObject BuildTun(AppState state)
    {
        var root = JsonNode.Parse(SingBoxConfigBuilder.Build(state, Vless()))!.AsObject();
        return root["inbounds"]!.AsArray()
            .Select(x => x!.AsObject())
            .Single(x => x["type"]!.GetValue<string>() == "tun");
    }

    private static ProxyProfile Vless() => new()
    {
        Name = "Server",
        Protocol = ProxyProtocol.Vless,
        Server = "91.186.217.186",
        Port = 443,
        UserId = "11111111-1111-1111-1111-111111111111",
        TlsEnabled = true,
        Network = "tcp",
        ServerName = "example.com"
    };

    private static ProxyProfile Hysteria2() => new()
    {
        Name = "Server",
        Protocol = ProxyProtocol.Hysteria2,
        Server = "91.186.217.186",
        Port = 443,
        Password = "password",
        ServerName = "example.com"
    };
}
