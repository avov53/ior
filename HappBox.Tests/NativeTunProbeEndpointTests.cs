using System.Net;
using HappBox;
using Xunit;
namespace HappBox.Tests;
public sealed class NativeTunProbeEndpointTests
{
    [Fact]public void ProbeUsesExternalProfileResolverNotSyntheticAdapterDns()
    {
        var adapter=IPAddress.Parse("172.19.0.2");
        var upstream=IPAddress.Parse("77.88.8.8");
        Assert.Equal(upstream,NativeTunHealthProbe.TrafficResolver(adapter,upstream));
        Assert.Equal(IPAddress.Parse("8.8.8.8"),NativeTunHealthProbe.TrafficResolver(adapter,null));
        Assert.Throws<IOException>(()=>NativeTunHealthProbe.TrafficResolver(adapter,adapter));
    }
    [Fact]public void Ipv6UpstreamIsPreserved()
    {
        var upstream=IPAddress.Parse("2001:4860:4860::8888");
        Assert.Equal(upstream,NativeTunHealthProbe.TrafficResolver(IPAddress.Parse("172.19.0.2"),upstream));
    }
}
