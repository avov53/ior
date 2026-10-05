using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class ProcessLogLimiterTests
{
    [Fact]
    public void NativeRoutineEventsAreBoundedWithoutHidingFailures()
    {
        var limiter=new ProcessLogLimiter(()=>100);
        for(var index=0;index<60;index++)Assert.True(limiter.TryAccept("ior-core: ROUTE_SELECTED example.com:443 → PROXY",out _));
        Assert.False(limiter.TryAccept("ior-core: TCP_RELAY_READY",out _));
        Assert.True(limiter.TryAccept("ior-core: LOCAL_FLOW_FAILED",out _));
        Assert.True(limiter.TryAccept("ior-core: UDP_RELAY_FAILED",out _));
    }
    [Fact]
    public void RoutinePacketsAreBoundedButErrorsAreKept()
    {
        var second = 100L;
        var limiter = new ProcessLogLimiter(() => second);
        const string packet = "2026-09-26 00:23:52 INFO inbound/tun[tun-in]: inbound packet connection to 172.19.0.2:53";
        var accepted = 0;
        for (var i = 0; i < 200; i++)
            if (limiter.TryAccept(packet, out _)) accepted++;

        Assert.Equal(60, accepted);
        Assert.True(limiter.TryAccept("ERROR dns: context deadline exceeded", out _));
        second++;
        Assert.True(limiter.TryAccept("WARN connection recovered", out var summary));
        Assert.Contains("140", summary);
    }
}
