using System.Text.Json;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class CoreRuntimeContractTests
{
    [Fact]
    public void PacketPathFlagIsOptionalAndExplicitInExperimentalPackage()
    {
        var old = JsonSerializer.Deserialize<CoreWireTun>("{\"name\":\"ior-native-test\",\"mtu\":1280,\"wintunPath\":\"C:/wintun.dll\"}", CoreRuntimeContract.JsonOptions)!;
        Assert.DoesNotContain("awgRawTcp", JsonSerializer.Serialize(old, CoreRuntimeContract.JsonOptions));
    }
    [Fact]
    public void SharedVersionOneFixtureRoundTrips()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "start-v1.json"));
        var request = JsonSerializer.Deserialize<CoreStartRequest>(json, CoreRuntimeContract.JsonOptions)!;
        Assert.Equal(1, request.ApiVersion);
        Assert.Equal("vless_reality", request.Profile.Protocol);
        Assert.Equal(["PROXY", "PROXY"], request.Routes.Select(rule => rule.Target));
        var serialized = JsonSerializer.Serialize(request, CoreRuntimeContract.JsonOptions);
        Assert.Contains("\"apiVersion\":1", serialized);
        Assert.DoesNotContain(request.Profile.Credential, request.Profile.ToString());
    }

    [Fact]
    public void RejectsUnsupportedRuleBeforeCreatingStartRequest()
    {
        var route = ClashProfileParser.Parse("""
            rules:
              - RULE-SET,media,PROXY
              - MATCH,PROXY
            """);
        var state = new AppState { HappRoutingProfiles = [route], SelectedHappRoutingProfileId = route.Id };
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Vless, Server = "203.0.113.10", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111", TlsEnabled = true, ServerName = "example.com" };
        Assert.False(CoreRuntimeContract.TryCreateStart(state, profile, out var request, out var reason));
        Assert.Null(request);
        Assert.Contains("RULE-SET", reason);
    }

    [Theory]
    [InlineData("xtls-rprx-vision", null)]
    [InlineData(null, "grpc")]
    public void RejectsUnsupportedVlessTransportBeforeNetworkChanges(string? flow, string? network)
    {
        var state = new AppState();
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Vless, Server = "203.0.113.10", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111", TlsEnabled = true,
            ServerName = "example.com", Flow = flow, Network = network };
        Assert.False(CoreRuntimeContract.TryCreateStart(state, profile, out var request, out var reason));
        Assert.Null(request);
        Assert.Contains("не поддерживает", reason);
    }
}
