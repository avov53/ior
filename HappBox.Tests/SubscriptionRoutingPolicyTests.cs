using HappBox;
using Xunit;
namespace HappBox.Tests;
public sealed class SubscriptionRoutingPolicyTests
{
    [Fact] public void FreshAndExistingStatesKeepTheirRespectiveRoutingDefaults()
    {
        var directory=Path.Combine(Path.GetTempPath(),"ior-defaults-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try{
            var file=Path.Combine(directory,"state.json");var method=typeof(MainForm).GetMethod("LoadState",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
            var fresh=(AppState)method.Invoke(null,[file])!;Assert.Null(fresh.SelectedHappRoutingProfileId);Assert.True(fresh.RoutingEnabled);Assert.Empty(fresh.HappRoutingProfiles);
            var old=new AppState{SelectedHappRoutingProfileId="personal",HappRoutingProfiles=[new HappRoutingProfile{Id="personal",Name="Personal",Source=RoutingProfileSource.Ior,GlobalProxy=true}]};
            File.WriteAllText(file,System.Text.Json.JsonSerializer.Serialize(old));var restored=(AppState)method.Invoke(null,[file])!;
            Assert.Equal("personal",restored.SelectedHappRoutingProfileId);Assert.True(restored.RoutingEnabled);Assert.DoesNotContain(restored.HappRoutingProfiles,p=>p.Id=="none");
        }finally{Directory.Delete(directory,true);}
    }
    [Fact] public void ToggleMigrationAndSelectionNeverEnableDisabledRouting(){
        var state=new AppState{SelectedHappRoutingProfileId="none",HappRoutingProfiles=[new(){Id="none"},new(){Id="a"},new(){Id="b"}]};
        RoutingSelectionPolicy.Migrate(state);Assert.False(state.RoutingEnabled);Assert.DoesNotContain(state.HappRoutingProfiles,r=>r.Id=="none");
        var a=new ProxySubscription{Id="sub-a",RoutingProfileId="a"};var b=new ProxySubscription{Id="sub-b",RoutingProfileId="b"};state.Subscriptions.AddRange([a,b]);
        RoutingSelectionPolicy.ForServer(state,new(){SubscriptionId=a.Id});Assert.Equal("a",state.SelectedHappRoutingProfileId);
        RoutingSelectionPolicy.ForServer(state,new(){SubscriptionId=b.Id});Assert.Equal("b",state.SelectedHappRoutingProfileId);Assert.False(state.RoutingEnabled);Assert.True(RoutingSelectionPolicy.Effective(state)!.GlobalProxy);Assert.Empty(RoutingSelectionPolicy.Effective(state)!.DirectSites);
        RoutingSelectionPolicy.Migrate(state);Assert.False(state.RoutingEnabled);
        RoutingSelectionPolicy.ForServer(state,new());Assert.Null(state.SelectedHappRoutingProfileId);
    }
    private static SubscriptionFetchResult Result(List<ProxyProfile> profiles,HappRoutingProfile? route=null,int skipped=0)=>new(profiles,skipped,null,0,0,0,null,null,null,IsClashYaml:route!=null,ClashRouting:route,RemovedProtocolCount:skipped);
    [Fact] public void NewSubscriptionAddsRouteWithoutChangingSelection()
    {
        var state=new AppState{SelectedHappRoutingProfileId="none"};var sub=new ProxySubscription{RouteConsent="ask"};state.Subscriptions.Add(sub);
        var route=new HappRoutingProfile{Source=RoutingProfileSource.Clash,GlobalProxy=true};
        SubscriptionService.Apply(state,sub,Result([new ProxyProfile{Protocol=ProxyProtocol.Hysteria2,Server="a.test",Port=443}],route));
        Assert.Single(state.HappRoutingProfiles);Assert.NotNull(sub.PendingRoute);Assert.Equal("none",state.SelectedHappRoutingProfileId);
        state.RoutingEnabled=false;RoutingSelectionPolicy.ForServer(state,new ProxyProfile{SubscriptionId=sub.Id});Assert.Equal(sub.RoutingProfileId,state.SelectedHappRoutingProfileId);Assert.False(state.RoutingEnabled);
        RoutingSelectionPolicy.ForServer(state,new ProxyProfile());Assert.Null(state.SelectedHappRoutingProfileId);
    }
    [Fact] public void UnsupportedOnlyRefreshPreservesWorkingAndLegacyServers()
    {
        var state=new AppState();var sub=new ProxySubscription();state.Subscriptions.Add(sub);
        var old=new ProxyProfile{SubscriptionId=sub.Id,Protocol=ProxyProtocol.Vless};state.Profiles.Add(old);
        var merged=SubscriptionService.Apply(state,sub,Result([],skipped:3));Assert.Same(old,Assert.Single(state.Profiles));Assert.Contains("пока не поддерживаются",merged.Warning!);
    }
    [Fact] public void MixedLinksSkipRecognizedUnsupportedEvenIfMalformed()
    {
        var result=ProfileParser.ParseManyDetailed("vmess://invalid\ntrojan://invalid\nss://invalid\nhy2://pass@host.test:443");
        Assert.Single(result.Profiles);Assert.Equal(3,result.RemovedProtocolCount);
    }
}
