using HappBox;
using System.Text.Json;
using Xunit;
namespace HappBox.Tests;
public sealed class AwgFullProxyTests
{
    private static ProxyProfile Profile()=>ProfileParser.ParseMany($"[Interface]\nPrivateKey={Convert.ToBase64String(new byte[32])}\nAddress=10.7.0.2/32\nDNS=8.8.8.8\nMTU=1280\nJc=4\n[Peer]\nPublicKey={Convert.ToBase64String(new byte[32])}\nEndpoint=192.0.2.1:1234\nAllowedIPs=0.0.0.0/0").Single();
    [Fact] public void RustMigrationRunsOnceAndManualGoIsPreserved()
    {
        Assert.Equal(AwgEngine.Rust,JsonSerializer.Deserialize<AppState>("{}")!.AwgEngine);
        var state=JsonSerializer.Deserialize<AppState>("{\"AwgEngine\":0,\"AwgTunRoutingMode\":1}")!;
        AwgClientPolicy.Migrate(state); Assert.Equal(AwgEngine.Rust,state.AwgEngine);
        state.AwgEngine=AwgEngine.Go;
        var restored=JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(state))!;
        AwgClientPolicy.Migrate(restored); Assert.Equal(AwgEngine.Go,restored.AwgEngine);
    }
    [Fact] public void BothEnginesUseSameAutomaticRoutesAndRustNeedsNoAdapter()
    {
        var state=new AppState{SelectedCore=CoreKind.IorCore,TunEnabled=true}; var profile=Profile();
        Assert.True(CoreRuntimeContract.TryCreateStart(state,profile,out var rust,out var reason),reason);
        Assert.Equal("rust",rust!.Profile.Options.AwgEngine); Assert.Null(rust.Profile.Options.AwgAdapterPath);
        Assert.Equal("fullProxy",rust.Profile.Options.AwgRoutingMode); Assert.Equal("PROXY",Assert.Single(rust.Routes).Target);
        state.AwgEngine=AwgEngine.Go;
        Assert.True(CoreRuntimeContract.TryCreateStart(state,profile,out var go,out reason),reason);
        Assert.NotNull(go!.Profile.Options.AwgAdapterPath);
        Assert.Equal(JsonSerializer.Serialize(rust.Routes),JsonSerializer.Serialize(go.Routes));
    }
    [Theory] [InlineData(AwgTunRoutingMode.Rules)] [InlineData(AwgTunRoutingMode.FullProxy)]
    public void LegacyModeCannotDiscardSelectedRules(AwgTunRoutingMode oldMode)
    {
        var route=ClashProfileParser.Parse("rules:\n - DOMAIN,private.example,DIRECT\n - DOMAIN,blocked.example,REJECT\n - MATCH,DIRECT\n");
        var state=new AppState{SelectedCore=CoreKind.IorCore,TunEnabled=true,AwgTunRoutingMode=oldMode,HappRoutingProfiles=[route],SelectedHappRoutingProfileId=route.Id};
        var profile=Profile(); var before=JsonSerializer.Serialize(new{state.HappRoutingProfiles,profile});
        Assert.True(CoreRuntimeContract.TryCreateStart(state,profile,out var request,out var reason),reason);
        Assert.Contains(request!.Routes,r=>r.Kind=="DOMAIN"&&r.Target=="DIRECT");
        Assert.Contains(request.Routes,r=>r.Target=="REJECT");
        Assert.Equal("PROXY",request.Routes.Last().Target); Assert.Equal("rules",request.Profile.Options.AwgRoutingMode);
        Assert.Equal(before,JsonSerializer.Serialize(new{state.HappRoutingProfiles,profile}));
        Assert.Contains("По правилам",AwgClientPolicy.RoutingSummary(state));
    }
    [Theory] [InlineData(RoutingProfileSource.Happ)] [InlineData(RoutingProfileSource.Clash)]
    public void EmptySelectedProfileUsesFullProxyAndMissingProfileFails(RoutingProfileSource source)
    {
        var route=new HappRoutingProfile{GlobalProxy=false,Source=source};
        var state=new AppState{SelectedCore=CoreKind.IorCore,TunEnabled=true,HappRoutingProfiles=[route],SelectedHappRoutingProfileId=route.Id};
        Assert.True(CoreRuntimeContract.TryCreateStart(state,Profile(),out var request,out var reason),reason);
        Assert.Equal("fullProxy",request!.Profile.Options.AwgRoutingMode); Assert.Equal("PROXY",Assert.Single(request.Routes).Target);
        Assert.Equal("Все сайты через AWG",AwgClientPolicy.RoutingSummary(state));
        state.SelectedHappRoutingProfileId="missing";
        Assert.False(CoreRuntimeContract.TryCreateStart(state,Profile(),out _,out _));
    }
    [Fact] public void OtherProtocolsRetainTheirFinalRoute()
    {
        var route=ClashProfileParser.Parse("rules:\n - DOMAIN,private.example,DIRECT\n - MATCH,DIRECT\n");
        var state=new AppState{SelectedCore=CoreKind.IorCore,TunEnabled=true,HappRoutingProfiles=[route],SelectedHappRoutingProfileId=route.Id};
        var profile=new ProxyProfile{Protocol=ProxyProtocol.Hysteria2,Server="192.0.2.1",Port=443,Password="synthetic",TlsEnabled=true};
        Assert.True(CoreRuntimeContract.TryCreateStart(state,profile,out var request,out var reason),reason);
        Assert.Null(request!.Profile.Options.AwgRoutingMode); Assert.Equal("DIRECT",request.Routes.Last().Target);
    }
}
