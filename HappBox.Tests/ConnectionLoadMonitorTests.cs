using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class ConnectionLoadMonitorTests
{
    [Fact]
    public void DetectsXrayTunDnsAndVisionUdp443Rejection()
    {
        const string dns = "[Info] proxy/tun: processing from udp:172.19.0.1:59136 to udp:8.8.8.8:53";
        const string vision = "[Info] proxy/vless/outbound: XTLS rejected UDP/443 traffic";
        Assert.True(ConnectionLoadMonitor.IsTunDnsLine(dns));
        Assert.True(ConnectionLoadMonitor.IsVisionUdp443Rejection(vision));
        var monitor = new ConnectionLoadMonitor();
        monitor.RecordProcessLine(dns);
        monitor.RecordProcessLine(vision);
        monitor.RecordProcessLine("from udp:172.19.0.1:137 accepted udp:172.19.0.3:137 [tun-in -> direct]");
        var sample = monitor.Sample(null, "TUN", false);
        Assert.Equal(1, sample.TunDnsLinesPerSecond);
        Assert.Equal(1, sample.Udp443RejectedLinesPerSecond);
        Assert.Equal(1, sample.DirectPacketLinesPerSecond);
    }
}
