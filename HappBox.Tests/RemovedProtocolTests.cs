using System.Text;
using System.Text.Json;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class RemovedProtocolTests
{
    private static string Key => Convert.ToBase64String(new byte[32]);
    private static string Config => $"[Interface]\nPrivateKey={Key}\nAddress=10.0.0.2/32\n[Peer]\nPublicKey={Key}\nEndpoint=example.com:443\nAllowedIPs=0.0.0.0/0";
    [Fact]
    public void StandardWireGuardRetainsDualStackDnsCapability()
    {
        var profile = ProfileParser.ParseMany(Config.Replace("AllowedIPs=0.0.0.0/0", "AllowedIPs=0.0.0.0/0, ::/0")).Single();
        Assert.True(CoreRuntimeContract.TryCreateStart(new AppState(), profile, out var request, out var reason), reason);
        Assert.True(request!.Dns.Ipv4Only);
        profile.LocalAddress += ", fd00::2/128";
        Assert.True(CoreRuntimeContract.TryCreateStart(new AppState(), profile, out request, out reason), reason);
        Assert.False(request!.Dns.Ipv4Only);
    }
    [Theory]
    [InlineData("awg")]
    [InlineData("amneziawg")]
    public void ExplicitRemovedLinksNeverBecomeWireGuard(string scheme)
    {
        var result = ProfileParser.ParseManyDetailed(scheme + "://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Config)));
        Assert.Empty(result.Profiles); Assert.Equal(1, result.RemovedProtocolCount);
    }
    [Theory]
    [InlineData("Jc")]
    [InlineData("S4")]
    [InlineData("header_protection_key")]
    [InlineData("DisableCookies")]
    public void DisguisedWireGuardLinksRejectObfuscation(string field)
    {
        var config = Config.Replace("[Peer]", field + "=1\n[Peer]");
        var result = ProfileParser.ParseManyDetailed("vpn://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(config)));
        Assert.Empty(result.Profiles); Assert.Equal(1, result.RemovedProtocolCount);
        Assert.Single(ProfileParser.ParseMany("wireguard://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Config))));
    }
    [Fact]
    public void MixedJsonAndYamlRetainSupportedServersOnly()
    {
        var json = JsonSubscriptionParser.Parse("[{\"type\":\"awg\"},{\"type\":\"http\",\"server\":\"example.com\",\"server_port\":80}]");
        Assert.Single(json.Profiles); Assert.Equal(1, json.RemovedProtocolCount);
        var yaml = "proxies:\n  - {name: removed, type: amneziawg}\n  - {name: kept, type: http, server: example.com, port: 80}\nproxy-groups:\n  - {name: group, type: select, proxies: [removed, kept]}\nrules:\n  - MATCH,group\n";
        var result = ClashSubscriptionParser.Parse(yaml);
        Assert.Single(result.Profiles); Assert.Equal(1, result.RemovedProtocolCount);
        Assert.DoesNotContain("amneziawg", result.Routing!.OriginalClashYaml);
        Assert.DoesNotContain("removed", result.Routing.OriginalClashYaml);
    }
    [Fact]
    public void NumericLegacyStateMigratesWithoutResettingOtherData()
    {
        var state = JsonSerializer.Deserialize<AppState>("{\"DnsServer\":\"9.9.9.9\",\"SelectedProfileId\":\"old\",\"Profiles\":[{\"Id\":\"old\",\"Protocol\":8},{\"Id\":\"kept\",\"Protocol\":5}],\"ConnectionProfiles\":[{\"ConfigIds\":[\"old\",\"kept\"],\"DefaultServerId\":\"old\"}]}" )!;
        Assert.Equal(1, RemovedProtocolPolicy.Migrate(state));
        Assert.Equal("9.9.9.9", state.DnsServer); Assert.Equal("kept", state.SelectedProfileId);
        Assert.Equal(["kept"], state.ConnectionProfiles.Single().ConfigIds);
        Assert.Equal("kept", state.ConnectionProfiles.Single().DefaultServerId);
        Assert.Equal(ProxyProtocol.WireGuard, state.Profiles.Single().Protocol);
    }
    [Fact]
    public void RemovedOnlySubscriptionCleansOldNodesButEmptyFailurePreservesGoodNodes()
    {
        var subscription = new ProxySubscription();
        var removed = new ProxyProfile {Protocol=(ProxyProtocol)8, SubscriptionId=subscription.Id};
        var kept = new ProxyProfile {Protocol=ProxyProtocol.Http};
        var state = new AppState {Profiles=[removed, kept]};
        var result = new SubscriptionFetchResult([], 1, null, 0, 0, 0, null, null, null, RemovedProtocolCount:1);
        var merged = SubscriptionService.Apply(state, subscription, result);
        Assert.Equal(1, merged.Removed); Assert.Equal(kept, state.Profiles.Single()); Assert.Contains("AmneziaWG", merged.Warning);
        Assert.Throws<InvalidOperationException>(() => SubscriptionService.Apply(state, subscription, result with {RemovedProtocolCount=0}));
        Assert.Equal(kept, state.Profiles.Single());
        Assert.Throws<InvalidOperationException>(() => SubscriptionService.Apply(state, subscription, result with {RejectedCount=2}));
        Assert.Equal(kept, state.Profiles.Single());
    }
    [Theory]
    [InlineData(CoreKind.SingBox)]
    [InlineData(CoreKind.IorCore)]
    [InlineData(CoreKind.XrayCore)]
    public void EveryCoreRejectsLegacyProtocol(CoreKind core)
    {
        var profile = new ProxyProfile {Protocol=(ProxyProtocol)8};
        Assert.False(CoreEngineService.CanRun(new AppState {SelectedCore=core}, profile, out _));
        Assert.DoesNotContain((ProxyProtocol)8, CoreEngineService.Capability(core).Protocols);
        Assert.False(CoreRuntimeContract.TryCreateStart(new AppState(), profile, out _, out _));
        Assert.Throws<NotSupportedException>(() => SingBoxConfigBuilder.Build(new AppState(), profile));
    }
}
