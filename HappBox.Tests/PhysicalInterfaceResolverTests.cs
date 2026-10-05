using System.Net;
using System.Net.NetworkInformation;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class PhysicalInterfaceResolverTests
{
    [Fact]
    public void RouteProbeAllowsExactNonPhysicalInterfaceButNotAnUnrelatedName()
    {
        if (!OperatingSystem.IsWindows()) return;
        var loopback = NetworkInterface.GetAllNetworkInterfaces().Single(item =>
            item.NetworkInterfaceType == NetworkInterfaceType.Loopback);
        Assert.True(PhysicalInterfaceResolver.UsesInterfaceForIpv4(IPAddress.Loopback, loopback.Name));
        Assert.True(PhysicalInterfaceResolver.UsesInterfaceForAddress(IPAddress.Loopback,loopback.Name));
        Assert.True(PhysicalInterfaceResolver.UsesInterfaceForAddress(IPAddress.IPv6Loopback,loopback.Name));
        Assert.False(PhysicalInterfaceResolver.UsesInterfaceForAddress(IPAddress.IPv6Loopback,"ior-native-deadbeef"));
        Assert.False(PhysicalInterfaceResolver.UsesInterfaceForIpv4(IPAddress.Loopback, "ior-native-deadbeef"));
        Assert.False(PhysicalInterfaceResolver.UsesInterfaceForIpv4(IPAddress.IPv6Loopback, loopback.Name));
        Assert.Throws<InvalidOperationException>(() => PhysicalInterfaceResolver.ForIpv4(IPAddress.Loopback));
    }
}
