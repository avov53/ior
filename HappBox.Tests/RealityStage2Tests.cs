using System.Text.Json;
using HappBox;
using Xunit;
namespace HappBox.Tests;
public sealed class RealityStage2Tests
{
    [Fact]public void StartupTargetRequiresTenStartsAndNoFailures() {
        Assert.True(NativeRealityBenchmark.TargetMet(Enumerable.Repeat(1000L,9).Append(5000L),0));
        Assert.False(NativeRealityBenchmark.TargetMet(Enumerable.Repeat(1000L,8).Concat([5000L,5000L]),0));
        Assert.False(NativeRealityBenchmark.TargetMet(Enumerable.Repeat(1000L,9),0));
        Assert.False(NativeRealityBenchmark.TargetMet(Enumerable.Repeat(1000L,10),1));
    }
    [Theory]
    [InlineData(false,CoreKind.IorCore,"xtls-rprx-vision")]
    [InlineData(true,CoreKind.IorCore,"xtls-rprx-vision-udp443")]
    [InlineData(true,CoreKind.XrayCore,"xtls-rprx-vision")]
    public void Udp443OnlyChangesNativeRuntime(bool enabled,CoreKind core,string flow)
    {
        var state=new AppState{SelectedCore=core,RealityUdp443Enabled=enabled};
        var profile=new ProxyProfile{Protocol=ProxyProtocol.Vless,RealityEnabled=true,TlsEnabled=true,Server="203.0.113.10",Port=443,ServerName="cover.example",UserId="11111111-1111-1111-1111-111111111111",PublicKey="synthetic",Flow="xtls-rprx-vision"};
        Assert.True(CoreRuntimeContract.TryCreateStart(state,profile,out var request,out var reason),reason);
        Assert.Equal(flow,request!.Profile.Flow);Assert.Equal("xtls-rprx-vision",profile.Flow);
    }
    [Fact]public void OldStateKeepsUdp443Disabled()=>Assert.False(JsonSerializer.Deserialize<AppState>("{}")!.RealityUdp443Enabled);
    [Fact]public async Task SuccessfulHedgeAwaitsLoserCleanup()
    {
        var cleaned=false;
        await XrayTunHealthProbe.CheckTrafficStageAsync("HTTPS",async (attempt,token)=>{
            if(attempt==1)return;
            try{await Task.Delay(Timeout.Infinite,token);}finally{await Task.Delay(30);cleaned=true;}
        },default,hedgeDelay:TimeSpan.FromMilliseconds(10));
        Assert.True(cleaned);
    }
    [Fact]public async Task ExplicitFailureStartsBackupWithoutWaitingForHedge()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await XrayTunHealthProbe.CheckTrafficStageAsync("HTTPS",(attempt,_)=>attempt==0?Task.FromException(new IOException()):Task.CompletedTask,deadline.Token,hedgeDelay:TimeSpan.FromSeconds(10));
    }
}
