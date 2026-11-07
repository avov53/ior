using HappBox;
using Xunit;
namespace HappBox.Tests;
public sealed class WindowsLogPolicyTests
{
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
