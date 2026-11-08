using HappBox;
using Xunit;
namespace HappBox.Tests;
public sealed class WindowsLogPolicyTests
{
    [Fact]public void RealityRelayFailureKeepsFlowAndDoesNotImplySessionFailure()
    {
        const string text="ior-core: REALITY_RELAY_FAILED · flow=171 · elapsed=13717ms · stage=relay · kind=Uncategorized";
        Assert.Equal("warning",WindowsLogPolicy.Level(text,true));Assert.Equal("error",WindowsLogPolicy.Level(text,false));
        Assert.Equal("error",WindowsLogPolicy.Level("REALITY_RELAY_FAILED",true));
        Assert.Contains("flow=171",WindowsLogPolicy.Annotate(text,true,false));
    }
    [Fact]public void UdpRejectIsSingleFlowOnBothWindowsPlatforms()
    {
        const string text="ior-core: TUN_UDP_NETWORK_REJECTED · flow=541 · elapsed=1ms · stage=tun";
        Assert.Equal("warning",WindowsLogPolicy.Level(text,true,false,windows10:true));
        Assert.Equal("error",WindowsLogPolicy.Level(text,false,false,windows10:true));
        Assert.Equal("warning",WindowsLogPolicy.Level(text,true,false,windows10:false));
        Assert.Contains("flow=541",WindowsLogPolicy.Annotate(text,true,false,windows10:true));
        Assert.Equal("error",WindowsLogPolicy.Level(text+" NATIVE_TUN_TRAFFIC_FAILED",true,false,windows10:true));
    }
    [Theory]
    [InlineData("recorded","info")]
    [InlineData("no_addresses","info")]
    [InlineData("resolver_response_rejected","warning")]
    [InlineData("timeout","warning")]
    public void AwgSecondaryObservationIsNotPrimaryDnsFailure(string reason,string level)
    {
        Assert.Equal(level,WindowsLogPolicy.Level("ior-core: AWG_FAMILY_DNS_OBSERVATION · queryType=28 · reason="+reason,true));
        Assert.Equal("error",WindowsLogPolicy.Level("ior-core: NATIVE_TUN_TRAFFIC_FAILED",true));
        Assert.Equal("error",WindowsLogPolicy.Level("ior-core: TUN_DNS_FAILED",false));
    }
    [Fact]public void VisionUdp443PolicyKeepsOtherFailuresVisible()
    {
        Assert.Equal("info",WindowsLogPolicy.Level("ior-core: TUN_UDP_POLICY_BLOCKED · reason=VISION_UDP443_BLOCKED",true));
        Assert.Equal("warning",WindowsLogPolicy.Level("ior-core: TUN_UDP_POLICY_BLOCKED · reason=OTHER · classification=FLOW_IO_FAILURE",true));
    }
    [Theory]
    [InlineData("ior-core: TUN_TCP_FAILED · reason=PHYSICAL_IPV6_ADDRESS_MISSING")]
    [InlineData("ior-core: TUN_DNS_FAILED · reason=TUN_DNS_TIMEOUT")]
    [InlineData("proxy/hysteria: endpoint is closed for send")]
    public void IndividualFailureDependsOnReadyConnection(string text)
    {Assert.Equal("warning",WindowsLogPolicy.Level(text,true));Assert.Equal("error",WindowsLogPolicy.Level(text,false));}
    [Theory]
    [InlineData("NATIVE_TUN_TRAFFIC_FAILED")]
    [InlineData("NATIVE_ROLLBACK_PENDING")]
    [InlineData("AG_DNS_RECOVERY_REQUIRED")]
    [InlineData("TUN_EGRESS_RECAPTURED")]
    public void CriticalFailuresStayErrorsWhileConnected(string text)=>Assert.Equal("error",WindowsLogPolicy.Level(text,true));
    [Fact]public void OriginalFlowAndTextSurviveAnnotation()
    {
        const string source="ior-core: TUN_TCP_FAILED · flow=73 · 192.0.2.7:7680 · reason=TimedOut";
        var line=WindowsLogPolicy.Annotate(source,true,false);Assert.Contains(source,line);Assert.Contains("VPN продолжает работать",line);
        Assert.Equal("warning",WindowsLogPolicy.Level(line,false));
    }
    [Fact]public void HttpEofIsNotAssumedToBeExpected()
    {Assert.Equal("warning",WindowsLogPolicy.Level("[Info] proxy/http: failed to read http request > EOF"));Assert.Equal("info",WindowsLogPolicy.Level("LOCAL_PROXY_PROBE_CLOSED"));}
    [Fact]public void UnexpectedExitStaysError()=>Assert.Equal("error",WindowsLogPolicy.Level("Xray exited with code -1.",true,false));
    [Fact]public void DisabledContractFlagKeepsOldCoreCompatibility()
    {var json=System.Text.Json.JsonSerializer.Serialize(new CoreWireNetwork(),CoreRuntimeContract.JsonOptions);Assert.DoesNotContain("directIpv4Fallback",json);}
}
