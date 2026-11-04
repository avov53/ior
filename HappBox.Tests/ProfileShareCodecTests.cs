using System.Text.Json;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class ProfileShareCodecTests
{
    [Fact]
    public void SharePreservesCredentialsButCreatesIndependentImportedProfile()
    {
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Vless, Name = "Тест", Server = "edge.example", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111", RealityEnabled = true, PublicKey = "synthetic", SubscriptionId = "sub",
            ServerName = "cover.example", Flow = "xtls-rprx-vision", Fingerprint = "chrome" };
        var link = ProfileShareCodec.Export(profile);
        var imported = ProfileParser.ParseManyDetailed(link).Profiles.Single();
        Assert.NotEqual(profile.Id, imported.Id);
        Assert.Null(imported.SubscriptionId);
        Assert.Equal(profile.UserId, imported.UserId);
        Assert.Equal(profile.PublicKey, imported.PublicKey);
        Assert.Equal(profile.Flow, imported.Flow);
        Assert.Equal(profile.Name, imported.Name);
        Assert.True(imported.RealityEnabled);
        Assert.Equal("sub", profile.SubscriptionId);
        Assert.Single(JsonSubscriptionParser.Parse(JsonSerializer.Serialize(new { profiles = new[] { link } })).Profiles);
    }

    [Fact]
    public void WireGuardSecretsAndNonDefaultFlagsSurviveShare()
    {
        var profile = new ProxyProfile { Protocol = ProxyProtocol.WireGuard, Server = "192.0.2.1", Port = 51820,
            PrivateKey = Convert.ToBase64String(new byte[32]), PeerPublicKey = Convert.ToBase64String(new byte[32]),
            LocalAddress = "10.0.0.2/32", AllowedIps = "0.0.0.0/0", Mtu = 1280 };
        var imported = ProfileShareCodec.Import(ProfileShareCodec.Export(profile));
        Assert.Equal(profile.Protocol, imported.Protocol);
        Assert.Equal(profile.PrivateKey, imported.PrivateKey);
        Assert.Equal(profile.Mtu, imported.Mtu);
        Assert.Equal(profile.LocalAddress, imported.LocalAddress);
        Assert.Empty(ProfileParser.ParseManyDetailed("ior-profile://not-base64").Profiles);
    }
}
