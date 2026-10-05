using HappBox;
using Xunit;
namespace HappBox.Tests;
public sealed class NativeTunBenchmarkTests {
    [Fact]public void FailureDetailsKeepStageAndNetworkCodesWithoutExceptionMessages(){
        var socket=new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset);
        var error=new HttpRequestException(System.Net.Http.HttpRequestError.ConnectionError,"https://private.example/path?token=secret",socket,System.Net.HttpStatusCode.BadGateway);
        error.Data["iorProbeStage"]="youtube_http_headers_cold";
        var failure=NativeTunBenchmark.DescribeFailure(1,"WireGuard","current",error);
        Assert.Equal("youtube_http_headers_cold",failure.Stage);Assert.Equal("ConnectionError",failure.HttpError);
        Assert.Equal(502,failure.HttpStatus);Assert.Equal("ConnectionReset",failure.SocketError);
        var json=System.Text.Json.JsonSerializer.Serialize(failure);Assert.DoesNotContain("private.example",json);Assert.DoesNotContain("secret",json);
        error.Data["iorProbeStage"]="https://secret.example";
        Assert.Null(NativeTunBenchmark.DescribeFailure(1,"WireGuard","current",error).Stage);
    }
    [Fact]public void ImprovementsRequireAllSamplesAndNoFailedLoads(){
        var samples=Enumerable.Range(0,10).SelectMany(run=>new[]{
            new NativeTunBenchmark.Measurement(run,"WireGuard","baseline","ready",false,1000),
            new NativeTunBenchmark.Measurement(run,"WireGuard","current","ready",false,800)
        }).ToArray();
        Assert.True(NativeTunBenchmark.Compare(samples,[]).Single(item=>item.Protocol=="WireGuard"&&item.Stage=="ready").Accepted);
        Assert.False(NativeTunBenchmark.Compare(samples,[new(0,"WireGuard","current","PAGE_FAILED")]).Single(item=>item.Protocol=="WireGuard"&&item.Stage=="ready").Accepted);
        Assert.False(NativeTunBenchmark.Compare(samples.Skip(2),[]).Single(item=>item.Protocol=="WireGuard"&&item.Stage=="ready").Accepted);
    }
    [Fact]public void ImprovedMedianDoesNotExcuseWorseTail(){
        var samples=Enumerable.Range(0,10).SelectMany(run=>new[]{
            new NativeTunBenchmark.Measurement(run,"Hysteria2","baseline","ready",false,1000),
            new NativeTunBenchmark.Measurement(run,"Hysteria2","current","ready",false,run==9?1100:700)
        });
        Assert.False(NativeTunBenchmark.Compare(samples,[]).Single(item=>item.Protocol=="Hysteria2"&&item.Stage=="ready").Accepted);
    }
    [Fact]public void MissingCyclesCannotConfirmAcceleration(){
        Assert.All(NativeTunBenchmark.Compare([],[]), item=>Assert.False(item.Accepted));
    }
    [Fact]public void TailAndMedianDoNotHideASlowStartup(){
        long[] values=[676,689,690,714,714,724,760,1013,1434,10966];
        Assert.Equal(719,NativeTunBenchmark.Median(values));Assert.Equal(10966,NativeTunBenchmark.Percentile95(values));
    }
    [Fact]public void HysteriaTunIsPromotedButStillRequiresWindows11(){
        var state=new AppState{SelectedCore=CoreKind.IorCore,ExperimentalCoreTesting=false,TunEnabled=true,MixedProxyEnabled=true,IorCorePath=Environment.ProcessPath};
        var profile=new ProxyProfile{Protocol=ProxyProtocol.Hysteria2,Server="server.example",Port=443,ServerName="server.example",Password="synthetic",TlsEnabled=true};
        Assert.Equal(OperatingSystem.IsWindowsVersionAtLeast(10,0,22000),CoreEngineService.CanRun(state,profile,out _));
    }
}
