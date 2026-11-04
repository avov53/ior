using System.Text.Json;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class MobileShareTests
{
    [Fact]
    public void RealityQrIsStandardVlessAndPreservesAuthenticationAndTransport()
    {
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Vless, Name = "Тест QR", Server = "2001:db8::1", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111", TlsEnabled = true, RealityEnabled = true, PublicKey = "test-public-key",
            ShortId = "abcdef12", ServerName = "cover.example", Flow = "xtls-rprx-vision", Fingerprint = "firefox", Network = "tcp" };
        var qr = MobileShare.Configuration(profile);
        Assert.Equal("happ", qr.Format);
        Assert.StartsWith("vless://", qr.Text);
        var parsed = ProfileParser.ParseManyDetailed(qr.Text).Profiles.Single();
        Assert.Equal(profile.PublicKey, parsed.PublicKey);
        Assert.Equal(profile.ShortId, parsed.ShortId);
        Assert.Equal(profile.UserId, parsed.UserId);
        Assert.Equal(profile.Flow, parsed.Flow);
        Assert.Equal(profile.ServerName, parsed.ServerName);
        Assert.Equal(profile.Fingerprint, parsed.Fingerprint);
        Assert.Equal(profile.Name, parsed.Name);
        Assert.True(parsed.RealityEnabled);
    }

    [Theory]
    [InlineData(ProxyProtocol.Trojan, "trojan://")]
    [InlineData(ProxyProtocol.Hysteria2, "hysteria2://")]
    [InlineData(ProxyProtocol.Shadowsocks, "ss://")]
    [InlineData(ProxyProtocol.Socks, "socks://")]
    public void StandardQrKeepsSpecialCharactersInCredentials(ProxyProtocol protocol, string prefix)
    {
        var profile = new ProxyProfile { Protocol = protocol, Name = "QR тест", Server = "edge.example", Port = 443,
            Username = "user:@", Password = "Pa:ss/@?#日本", Method = "chacha20-ietf-poly1305", ServerName = "cover.example", TlsEnabled = protocol is not (ProxyProtocol.Socks or ProxyProtocol.Shadowsocks) };
        var qr = MobileShare.Configuration(profile);
        Assert.StartsWith(prefix, qr.Text);
        var result=ProfileParser.ParseManyDetailed(qr.Text);if(RemovedProtocolPolicy.IsRemoved(profile)){Assert.Empty(result.Profiles);Assert.Equal(1,result.RemovedProtocolCount);return;}
        var parsed = result.Profiles.Single();
        Assert.Equal(profile.Password, parsed.Password);
        Assert.Equal(profile.Name, parsed.Name);
    }

    [Fact]
    public void VmessQrHasRecognizableBase64JsonPayload()
    {
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Vmess, Server = "edge.example", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111", Name = "VMess", Network = "ws", Path = "/stream", TlsEnabled = true, ServerName = "cover.example" };
        var qr = MobileShare.Configuration(profile);
        Assert.StartsWith("vmess://", qr.Text);
        var result=ProfileParser.ParseManyDetailed(qr.Text);Assert.Empty(result.Profiles);Assert.Equal(1,result.RemovedProtocolCount);
    }

    [Fact]
    public void ClashSubscriptionKeepsEntireUrlInsideEncodedClashScheme()
    {
        var subscription = new ProxySubscription { IsClashYaml = true, Url = "https://example.test/sub?token=a%2Fb&format=clash#name" };
        var qr = MobileShare.Subscription(subscription, new AppState());
        Assert.Equal("clash", qr.Format);
        Assert.StartsWith("clash://install-config?url=", qr.Text);
        Assert.Equal(subscription.Url, Uri.UnescapeDataString(qr.Text.Split("?url=")[1]));
        subscription.IsClashYaml = false;
        Assert.Equal(subscription.Url, MobileShare.Subscription(subscription, new AppState()).Text);
    }

    [Fact]
    public void OldStateCanIdentifyManagedClashSubscriptionWithoutNewField()
    {
        var sub = new ProxySubscription { Url = "https://example.test/sub" };
        var state = new AppState();
        Assert.Equal("url", MobileShare.Subscription(sub, state).Format);
        state.HappRoutingProfiles.Add(new HappRoutingProfile { Source = RoutingProfileSource.Clash, ManagedBySubscriptionId = sub.Id });
        Assert.Equal("clash", MobileShare.Subscription(sub, state).Format);
        sub.IsClashYaml = false;
        Assert.Equal("happ", MobileShare.Subscription(sub, state).Format);
        Assert.Null(JsonSerializer.Deserialize<ProxySubscription>("{}")!.IsClashYaml);
    }

    [Fact]
    public void UnsupportedMobileProtocolIsClearlyMarkedAsIor()
    {
        var profile = new ProxyProfile { Protocol = ProxyProtocol.WireGuard, Server = "192.0.2.1", Port = 51820 };
        Assert.Equal("ior", MobileShare.Configuration(profile).Format);
    }

    [Theory]
    [InlineData(ProxyProtocol.Socks)]
    [InlineData(ProxyProtocol.Shadowsocks)]
    public void ExportDoesNotSilentlyRemoveTls(ProxyProtocol protocol) =>
        Assert.Throws<NotSupportedException>(() => MobileShare.Configuration(new ProxyProfile
        { Protocol = protocol, TlsEnabled = true, Server = "edge.example", Port = 443 }));
}
