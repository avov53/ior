using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class ProfileFileTests
{
    [Theory][InlineData(ProxyProtocol.WireGuard)][InlineData(ProxyProtocol.AmneziaWG)]
    public void ConfRoundTripPreservesTunnelParameters(ProxyProtocol protocol)
    {
        var key=Convert.ToBase64String(new byte[32]);
        var original=new ProxyProfile { Protocol=protocol,Name="Туннель",Server="2001:db8::1",Port=51820,PrivateKey=key,PeerPublicKey=key,PreSharedKey=key,
            LocalAddress="10.0.0.2/32",AllowedIps="0.0.0.0/0, ::/0",TunnelDns="1.1.1.1",Mtu=1280,PersistentKeepalive=25 };
        if(protocol==ProxyProtocol.AmneziaWG)original.AwgParameters=new(){["jc"]="4",["s1"]="16"};
        var parsed=Assert.Single(ProfileParser.ParseMany(WireGuardFile.Export(original)));
        Assert.Equal(protocol,parsed.Protocol);Assert.Equal(original.Server,parsed.Server);Assert.Equal(original.PrivateKey,parsed.PrivateKey);
        Assert.Equal(original.PreSharedKey,parsed.PreSharedKey);Assert.Equal(original.AllowedIps,parsed.AllowedIps);Assert.Equal(original.Mtu,parsed.Mtu);
        Assert.Equal(original.TunnelDns,parsed.TunnelDns);Assert.Equal(original.PersistentKeepalive,parsed.PersistentKeepalive);
        if(protocol==ProxyProtocol.AmneziaWG)Assert.Equal("4",parsed.AwgParameters["jc"]);
    }

    [Fact]public void LinkProtocolCannotExportAsConf()=>Assert.Throws<InvalidOperationException>(()=>WireGuardFile.Export(new(){Protocol=ProxyProtocol.Vless}));

    [Fact]public void WorkspaceFileRestoresDependenciesWithFreshIdsWithoutSwitchingConnection()
    {
        var config=new ProxyProfile {Id="server",Name="Сервер",Protocol=ProxyProtocol.Vless,Server="example.invalid",Port=443,UserId="secret"};
        var route=new HappRoutingProfile {Id="route",Name="Маршрут",Source=RoutingProfileSource.Ior,GlobalProxy=false};
        var subscription=new ProxySubscription {Id="sub",Name="Подписка",Url="https://example.invalid/list",HwidMode=SubscriptionHwidMode.Allow,HwidOverride="private-value"};
        var workspace=new ConnectionWorkspaceProfile {Id="workspace",Name="Работа",ConfigIds=[config.Id],DefaultServerId=config.Id,SubscriptionId=subscription.Id,RoutingProfileId=route.Id};
        var source=new AppState {Profiles=[config],HappRoutingProfiles=[route],Subscriptions=[subscription],ConnectionProfiles=[workspace]};
        var target=new AppState {SelectedConnectionProfileId="keep",SelectedProfileId="running"};
        var restored=ConnectionProfileFile.Import(target,ConnectionProfileFile.Export(source,workspace.Id));
        Assert.NotEqual(workspace.Id,restored.Id);Assert.NotEqual(config.Id,restored.ConfigIds.Single());
        Assert.Equal(target.Profiles.Single().Id,restored.DefaultServerId);Assert.Equal("secret",target.Profiles.Single().UserId);
        Assert.Equal(target.HappRoutingProfiles.Single().Id,restored.RoutingProfileId);Assert.False(target.HappRoutingProfiles.Single().GlobalProxy);
        Assert.Equal(target.Subscriptions.Single().Id,restored.SubscriptionId);Assert.Equal(SubscriptionHwidMode.Auto,target.Subscriptions.Single().HwidMode);
        Assert.Null(target.Subscriptions.Single().HwidOverride);Assert.Equal("keep",target.SelectedConnectionProfileId);Assert.Equal("running",target.SelectedProfileId);
    }

    [Fact]public void WrongFileLeavesApplicationStateIntact()
    {
        var state=new AppState();
        Assert.Throws<FormatException>(()=>ConnectionProfileFile.Import(state,"{\"Type\":\"ior-visual-prototype\",\"Version\":1}"));
        Assert.Empty(state.Profiles);Assert.Empty(state.ConnectionProfiles);
    }
}
