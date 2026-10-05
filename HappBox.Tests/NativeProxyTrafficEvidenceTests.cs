using System.Net;
using System.Text.Json;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class NativeProxyTrafficEvidenceTests
{
    private static void Event(NativeProxyTrafficEvidence evidence,string code,ulong flow,string? target=null,
        string source="172.19.0.1:58957",string destination="104.16.124.96:443")
    {
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(new{code,flowId=flow,target,source,destination}));
        evidence.Observe(json.RootElement,code);
    }

    [Fact]
    public void RemovedPacketPathEventsCannotProveCurrentProxyTraffic()
    {
        var evidence=new NativeProxyTrafficEvidence();
        const string source="172.19.0.1:58957",destination="104.16.124.96:443";
        Event(evidence,"AWG_RAW_TCP_ADMITTED",17,"PROXY");
        Event(evidence,"AWG_RAW_TCP_SYN_ACK",17);
        Assert.False(XrayTunHealthProbe.HasProxyEvidence("www.cloudflare.com",(source,destination),evidence.HasProxiedTraffic,evidence.HasProxiedPacketTraffic));
        Event(evidence,"AWG_RAW_TCP_FIRST_DATA",17);
        Assert.False(XrayTunHealthProbe.HasProxyEvidence("www.cloudflare.com",(source,destination),evidence.HasProxiedTraffic,evidence.HasProxiedPacketTraffic));
        Assert.False(XrayTunHealthProbe.HasProxyEvidence("www.cloudflare.com",("172.19.0.1:58958",destination),evidence.HasProxiedTraffic,evidence.HasProxiedPacketTraffic));
        Assert.False(XrayTunHealthProbe.HasProxyEvidence("www.cloudflare.com",(source,"104.16.125.96:443"),evidence.HasProxiedTraffic,evidence.HasProxiedPacketTraffic));
        Assert.False(XrayTunHealthProbe.HasProxyEvidence("www.cloudflare.com",null,evidence.HasProxiedTraffic,evidence.HasProxiedPacketTraffic));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("DIRECT")]
    [InlineData("REJECT")]
    public void UnconfirmedRouteOrUnrelatedFlowDoesNotProvePacketTraffic(string? target)
    {
        var evidence=new NativeProxyTrafficEvidence();
        Event(evidence,"AWG_RAW_TCP_ADMITTED",17,target);
        Event(evidence,"AWG_RAW_TCP_FIRST_DATA",17);
        Assert.False(evidence.HasProxiedPacketTraffic("172.19.0.1:58957","104.16.124.96:443"));
        Event(evidence,"AWG_RAW_TCP_ADMITTED",18,"PROXY");
        Event(evidence,"AWG_RAW_TCP_FIRST_DATA",19);
        Assert.False(evidence.HasProxiedPacketTraffic("172.19.0.1:58957","104.16.124.96:443"));
    }

    [Fact]
    public void LegacyStreamAndDnsEvidenceRemainIndependent()
    {
        var evidence=new NativeProxyTrafficEvidence();
        Event(evidence,"ROUTE_SELECTED",1,"PROXY",destination:"cloudflare.com:53");
        Event(evidence,"DNS_RESPONSE_READY",1);
        Assert.True(evidence.HasProxiedDnsTraffic("cloudflare.com"));
        Assert.False(evidence.HasProxiedTraffic("www.cloudflare.com"));
        Event(evidence,"ROUTE_SELECTED",2,"PROXY",destination:"www.cloudflare.com:443");
        Assert.False(evidence.HasProxiedTraffic("www.cloudflare.com"));
        Event(evidence,"TUN_TCP_FIRST_REMOTE_BYTE",2);
        Assert.True(XrayTunHealthProbe.HasProxyEvidence("www.cloudflare.com",null,evidence.HasProxiedTraffic,evidence.HasProxiedPacketTraffic));
        Assert.False(evidence.HasProxiedDnsTraffic("www.cloudflare.com"));
    }

    [Fact]
    public void DualModeSocketUsesSameIpv4TupleAsCore()
    {
        Assert.Equal("172.19.0.1:58957",NativeProxyTrafficEvidence.Endpoint(new IPEndPoint(IPAddress.Parse("::ffff:172.19.0.1"),58957)));
        Assert.Equal("[fdfe::2]:58957",NativeProxyTrafficEvidence.Endpoint(new IPEndPoint(IPAddress.Parse("fdfe::2"),58957)));
    }

    [Fact]
    public async Task WrappedProxyConfirmationFailureKeepsActionableReasonAndFailsReadiness()
    {
        var error=new IOException("TUN_PROBE_HTTPS_FAILED: both attempts failed",new IOException("TUN_HTTPS_PROXY_PATH_UNCONFIRMED"));
        var result=await NativeTunReadiness.CheckAsync(_=>Task.FromException(error),_=>Task.CompletedTask,_=>Task.CompletedTask,CancellationToken.None);
        Assert.False(result.Ready);
        Assert.Equal("TUN_HTTPS_PROXY_PATH_UNCONFIRMED",result.Https.Code);
        Assert.Equal("",NativeTunReadiness.ProbeErrorCode(new IOException("unsafe URL https://example.test/private")));
    }

    [Fact]
    public void OldStreamEvidenceIsEvicted()
    {
        var evidence=new NativeProxyTrafficEvidence();
        for(ulong flow=1;flow<=4100;flow++) {Event(evidence,"ROUTE_SELECTED",flow,"PROXY",destination:$"host{flow}.test:443");Event(evidence,"TUN_TCP_FIRST_REMOTE_BYTE",flow);}
        Assert.False(evidence.HasProxiedTraffic("host1.test"));
        Assert.True(evidence.HasProxiedTraffic("host4100.test"));
    }
}
