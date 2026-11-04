using System.Net;
using HappBox;
using Xunit;
namespace HappBox.Tests;
public sealed class NativeTunProbeEndpointTests
{
    [Fact]public void BootstrapPhysicalRouteUsesAnotherResolverThroughTun()
    {
        var bootstrap=IPAddress.Parse("1.1.1.1");
        Assert.Equal(IPAddress.Parse("8.8.8.8"),NativeTunHealthProbe.RoutedTrafficResolver(
            IPAddress.Parse("172.19.0.2"),bootstrap,ip=>!ip.Equals(bootstrap)));
    }
    [Fact]public void MissingTunRoutesStillFailsRatherThanProbingPhysicalNetwork()
    {
        Assert.Throws<IOException>(()=>NativeTunHealthProbe.RoutedTrafficResolver(
            IPAddress.Parse("172.19.0.2"),IPAddress.Parse("1.1.1.1"),_=>false));
    }
    [Fact]public void RoutedProfileResolverRemainsPreferred()
    {
        var upstream=IPAddress.Parse("77.88.8.8");
        Assert.Equal(upstream,NativeTunHealthProbe.RoutedTrafficResolver(IPAddress.Parse("172.19.0.2"),upstream,_=>true));
    }
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
