using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class ForwardProxyProfileTests
{
    [Theory]
    [InlineData("socks5://user:p%40ss@edge.example:1080#SOCKS", ProxyProtocol.Socks, 1080, false)]
    [InlineData("http://user:p%40ss@edge.example:8080#HTTP", ProxyProtocol.Http, 8080, false)]
    [InlineData("https://user:p%40ss@edge.example#HTTPS", ProxyProtocol.Http, 443, true)]
    public void ImportsCredentialsAndSecurity(string link, ProxyProtocol protocol, int port, bool tls)
    {
        var profile = Assert.Single(ProfileParser.ParseMany(link));
        Assert.Equal(protocol, profile.Protocol);
        Assert.Equal("user", profile.Username);
        Assert.Equal("p@ss", profile.Password);
        Assert.Equal(port, profile.Port);
        Assert.Equal(tls, profile.TlsEnabled);
    }

    [Fact]
    public void SubscriptionUrlIsNotMisinterpretedAsAnHttpServer()
    {
        Assert.Empty(ProfileParser.ParseMany("https://edge.example/subscription/private-token"));
    }

    [Fact]
    public void HttpTunDoesNotPromiseUnsupportedUdpDns()
    {
        var state = new AppState { TunEnabled = true, ExperimentalCoreTesting = true };
        var profile = Assert.Single(ProfileParser.ParseMany("https://edge.example:443"));
        Assert.False(XrayConfigBuilder.CanBuildTun(state, profile, out var reason));
        Assert.Contains("UDP", reason);
    }
}
