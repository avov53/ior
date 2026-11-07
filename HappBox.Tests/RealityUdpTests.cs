using HappBox;
using Xunit;
namespace HappBox.Tests;
public sealed class RealityUdpTests
{
    [Theory]
    [InlineData("xtls-rprx-vision",false,"xtls-rprx-vision")]
    [InlineData("xtls-rprx-vision",true,"xtls-rprx-vision-udp443")]
    [InlineData("xtls-rprx-vision-udp443",false,"xtls-rprx-vision-udp443")]
    [InlineData("xtls-rprx-vision-udp443",true,"xtls-rprx-vision-udp443")]
    public void ImportedFlowSurvivesSettingsAndPersistence(string flow,bool enabled,string expected)
    {
        var profile=ProfileParser.ParseMany($"vless://11111111-1111-1111-1111-111111111111@edge.example:443?security=reality&pbk=synthetic&sni=cover.example&flow={flow}").Single();
        profile=System.Text.Json.JsonSerializer.Deserialize<ProxyProfile>(System.Text.Json.JsonSerializer.Serialize(profile))!;
        var state=new AppState{SelectedCore=CoreKind.IorCore,RealityUdp443Enabled=enabled};
        Assert.True(CoreRuntimeContract.TryCreateStart(state,profile,out var request,out var reason),reason);
        Assert.Equal(expected,request!.Profile.Flow);Assert.Equal(flow,profile.Flow);
    }
    [Fact]public void MetricsCannotReplacePrimaryFailure()
    {
        Assert.Null(NativeFailureReason.TransportCode("REALITY_TCP_CONNECTED, REALITY_TLS_COMPLETE, REALITY_BRIDGE_METRICS"));
        Assert.Equal("REALITY_RELAY_FAILED",NativeFailureReason.TransportCode("REALITY_RELAY_FAILED, REALITY_BRIDGE_METRICS"));
    }
}
