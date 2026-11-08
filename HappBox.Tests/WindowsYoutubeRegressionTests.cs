using HappBox;
using Xunit;
using System.Security.Cryptography;
namespace HappBox.Tests;
public sealed class WindowsYoutubeRegressionTests
{
    [Fact]public void RotatedAwgParametersUpdatePeerWithoutReplacingItsId()
    {
        var sub=new ProxySubscription{Id="sub"};
        var old=new ProxyProfile{Id="stable",Protocol=ProxyProtocol.AmneziaWG,SubscriptionId=sub.Id,Server="peer.example",Port=443,PrivateKey="synthetic-private",PeerPublicKey="synthetic-peer",AwgParameters=new(){{"jc","6"}}};
        var next=new ProxyProfile{Protocol=ProxyProtocol.AmneziaWG,Server=old.Server,Port=old.Port,PrivateKey=old.PrivateKey,PeerPublicKey=old.PeerPublicKey,AwgParameters=new(){{"jc","7"}}};
        var state=new AppState{Subscriptions=[sub],Profiles=[old],SelectedProfileId=old.Id};
        var result=SubscriptionService.Apply(state,sub,new SubscriptionFetchResult([next],0,null,0,0,0,null,null,null));
        Assert.Equal(0,result.Added);Assert.Equal(1,result.Updated);Assert.Equal(0,result.Removed);
        Assert.Equal("stable",next.Id);Assert.Equal("stable",state.SelectedProfileId);Assert.Equal("7",state.Profiles[0].Awg["jc"]);Assert.Equal("6",old.Awg["jc"]);
    }
    [Fact]public void FeatureFlagRequiresExactBinaryAndIsAbsentForOldCoreByDefault()
    {
        var root=Path.Combine(Path.GetTempPath(),"ior-feature-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var exe=Path.Combine(root,"core");var manifest=Path.Combine(root,"hash");File.WriteAllText(exe,"fixture");
        try{
            Assert.False(NativeFeatureGate.Matches(manifest,exe));
            File.WriteAllText(manifest,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))));Assert.True(NativeFeatureGate.Matches(manifest,exe));
            File.WriteAllText(exe,"another");Assert.False(NativeFeatureGate.Matches(manifest,exe));
            Assert.DoesNotContain("awgNamedIpv4Fallback",System.Text.Json.JsonSerializer.Serialize(new CoreProtocolOptions(),CoreRuntimeContract.JsonOptions));
        }finally{Directory.Delete(root,true);}
    }
    [Theory][InlineData("AWG_PROXY_FAMILY_REJECTED")][InlineData("AWG_PACKET_FAMILY_REJECTED")]
    public void UnavailableFamilyIsAFlowWarningOnlyWhileVpnIsReady(string code)
    {Assert.Equal("warning",WindowsLogPolicy.Level("ior-core: "+code,true));Assert.Equal("error",WindowsLogPolicy.Level("ior-core: "+code,false));}
}
