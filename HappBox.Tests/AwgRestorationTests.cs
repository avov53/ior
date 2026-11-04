using System.Text;
using System.Text.Json;
using HappBox;
using Xunit;
namespace HappBox.Tests;
public sealed class AwgRestorationTests
{
    private static string Key=>Convert.ToBase64String(Enumerable.Repeat((byte)7,32).ToArray());
    private static string Config=>$"[Interface]\nPrivateKey={Key}\nAddress=10.7.0.2/32\nDNS=8.8.8.8\nMTU=1280\n[Peer]\nPublicKey={Key}\nEndpoint=example.com:443\nAllowedIPs=0.0.0.0/0";
    [Theory]
    [InlineData("awg")][InlineData("amneziawg")][InlineData("vpn")][InlineData("wireguard")]
    public void ObfuscatedProfilesKeepProtocolAndParameters(string scheme)
    {
        var config=Config.Replace("[Peer]","Jc=4\nS4=16\nHeaderProtectionKey="+Key+"\n[Peer]");
        var result=ProfileParser.ParseManyDetailed(scheme+"://"+Convert.ToBase64String(Encoding.UTF8.GetBytes(config)));
        var profile=Assert.Single(result.Profiles);Assert.Equal(ProxyProtocol.AmneziaWG,profile.Protocol);
        Assert.Equal("4",profile.Awg["jc"]);Assert.Equal(Key,profile.Awg["header_protection_key"]);Assert.Equal(0,result.RemovedProtocolCount);
        Assert.True(CoreRuntimeContract.TryCreateStart(new AppState(),profile,out var request,out var reason),reason);
        Assert.Equal("amneziawg",request!.Profile.Protocol);Assert.Equal(1280,request.Profile.Options.Mtu);Assert.Equal("8.8.8.8",request.Dns.Upstream);
        Assert.Equal(profile.Awg,request.Profile.Options.Awg);Assert.True(request.Dns.Ipv4Only);
        var copy=ProfileShareCodec.Import(ProfileShareCodec.Export(profile));Assert.Equal(profile.Awg,copy.Awg);
    }
    [Fact]public void StandardWireGuardKeepsItsEngineAndDns()
    {
        var profile=Assert.Single(ProfileParser.ParseMany(Config));Assert.Equal(ProxyProtocol.WireGuard,profile.Protocol);
        Assert.True(CoreRuntimeContract.TryCreateStart(new AppState(),profile,out var request,out _));Assert.Null(request!.Profile.Options.Awg);
    }
    [Fact]public void LegacyStateAndWorkspaceArePreserved()
    {
        var state=JsonSerializer.Deserialize<AppState>("{\"SelectedProfileId\":\"old\",\"Profiles\":[{\"Id\":\"old\",\"Protocol\":8}],\"ConnectionProfiles\":[{\"ConfigIds\":[\"old\"],\"DefaultServerId\":\"old\"}]}")!;
        Assert.Equal(0,RemovedProtocolPolicy.Migrate(state));Assert.Equal("old",state.SelectedProfileId);Assert.Equal(ProxyProtocol.AmneziaWG,Assert.Single(state.Profiles).Protocol);Assert.Single(state.ConnectionProfiles[0].ConfigIds);
    }
    [Fact]public void LegacyParameterNamesAndBooleanValuesArePreservedAndNormalized()
    {
        var profile=JsonSerializer.Deserialize<ProxyProfile>("{\"Protocol\":8,\"AwgParameters\":{\"Jc\":\"4\",\"DisableCookies\":\"on\"}}")!;
        Assert.Equal("on",profile.Awg["DisableCookies"]);var normalized=AwgProfileParameters.Normalize(profile.Awg);Assert.Equal("true",normalized["disable_cookies"]);Assert.Equal("4",normalized["jc"]);
        Assert.Throws<FormatException>(()=>AwgProfileParameters.Normalize(new Dictionary<string,string>{{"UnknownRequired","1"}}));
        Assert.Throws<FormatException>(()=>AwgProfileParameters.Normalize(new Dictionary<string,string>{{"jc","4\nprivate_key=bad"}}));
    }
    [Theory][InlineData(CoreKind.SingBox,true)][InlineData(CoreKind.XrayCore,true)][InlineData(CoreKind.IorCore,false)]
    public void UnsupportedCoreOrProxyIsRejected(CoreKind core,bool tun)
    {
        var profile=ProfileParser.ParseMany(Config.Replace("[Peer]","Jc=4\n[Peer]")).Single();
        Assert.False(CoreEngineService.CanRun(new AppState{SelectedCore=core,TunEnabled=tun,ExperimentalCoreTesting=true},profile,out var reason));Assert.Contains("только",reason);
    }
    [Fact]public void AvailabilityIncludesOnlyExperimentalNativeTun()
    {
        Assert.Contains(ProxyProtocol.AmneziaWG,CoreEngineService.Capability(CoreKind.IorCore).Protocols);
        Assert.DoesNotContain(ProxyProtocol.AmneziaWG,CoreEngineService.Capability(CoreKind.SingBox).Protocols);
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(CoreEngineService.NativeCombinations(new AppState(),new Version(10,0,19045))));
        var awg=Assert.Single(json.RootElement.EnumerateArray().Where(item=>item.GetProperty("protocol").GetString()=="AmneziaWG"));Assert.Equal("tun",awg.GetProperty("mode").GetString());Assert.Equal("supported",awg.GetProperty("status").GetString());
    }
    [Fact]public void JsonAndClashKeepAwgAlongsideOtherServers()
    {
        var json=JsonSubscriptionParser.Parse($"[{{\"type\":\"amneziawg\",\"server\":\"example.com\",\"server_port\":443,\"private_key\":\"{Key}\",\"peer_public_key\":\"{Key}\",\"local_address\":[\"10.7.0.2/32\"],\"jc\":4}},{{\"type\":\"http\",\"server\":\"example.com\",\"server_port\":80}}]");
        Assert.Equal(2,json.Profiles.Count);Assert.Equal("4",json.Profiles[0].Awg["jc"]);
        var yaml=ClashSubscriptionParser.Parse($"proxies:\n  - {{name: awg, type: amneziawg, server: example.com, port: 443, private-key: {Key}, public-key: {Key}, ip: 10.7.0.2, Jc: 4}}\n  - {{name: other, type: http, server: example.com, port: 80}}\n");
        Assert.Equal(2,yaml.Profiles.Count);Assert.Equal(ProxyProtocol.AmneziaWG,yaml.Profiles[0].Protocol);
    }
    [Fact]public void FailedRefreshKeepsAwgAndLastSuccessfulTime()
    {
        var subscription=new ProxySubscription{LastUpdatedUtc=DateTime.UtcNow};var profile=ProfileParser.ParseMany(Config.Replace("[Peer]","Jc=4\n[Peer]")).Single();profile.SubscriptionId=subscription.Id;
        var state=new AppState{Profiles=[profile]};var before=subscription.LastUpdatedUtc;
        Assert.Throws<InvalidOperationException>(()=>SubscriptionService.Apply(state,subscription,new SubscriptionFetchResult([],1,null,0,0,0,null,null,null)));
        Assert.Same(profile,Assert.Single(state.Profiles));Assert.Equal(before,subscription.LastUpdatedUtc);
    }
}
