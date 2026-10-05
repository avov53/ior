using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class XrayTunFlowParserTests
{
    [Fact]
    public void ParsesActualTunSocketTupleWithoutInferringFromVolume()
    {
        Assert.True(XrayTunFlowParser.TryParseTcp("[Info] proxy/tun: processing from tcp:192.168.0.219:50338 to tcp:111.88.96.56:443", out var source, out var destination));
        Assert.Equal(50338, source!.Port);
        Assert.Equal("111.88.96.56", destination!.Address.ToString());
    }

    [Theory]
    [InlineData("proxy/tun: processing from udp:192.168.0.219:1 to udp:1.1.1.1:53")]
    [InlineData("proxy/tun: processing from tcp:not-an-endpoint to tcp:1.1.1.1:443")]
    [InlineData("taking detour [direct] for [tcp:example.ru:443]")]
    public void DoesNotTreatUnrelatedLogsAsOwnSocket(string line) => Assert.False(XrayTunFlowParser.TryParseTcp(line, out _, out _));
}
