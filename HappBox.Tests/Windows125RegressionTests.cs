using System.Text.Json;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class Windows125RegressionTests
{
    [Theory]
    [InlineData("true", "chrome", true)]
    [InlineData("1", "firefox", true)]
    [InlineData("false", "chrome", false)]
    [InlineData("0", "chrome", false)]
    [InlineData("invalid", "chrome", false)]
    [InlineData("true", "safari", false)]
    public void HybridRealityImportAndSavedProfileMatchAndroid(string flag, string fingerprint, bool valid)
    {
        var profile = ProfileParser.ParseMany($"vless://11111111-1111-1111-1111-111111111111@edge.example:443?security=reality&type=tcp&pbk=synthetic&sni=cover.example&fp={fingerprint}&support-x25519mlkem768={flag}").Single();
        Assert.Empty(profile.UnsupportedParameters);
        Assert.Equal(flag, profile.SupportX25519Mlkem768);
        Assert.Equal(valid, CoreRuntimeContract.TryCreateStart(new AppState(), profile, out _, out _));
        var saved = JsonSerializer.Deserialize<ProxyProfile>(JsonSerializer.Serialize(profile))!;
        Assert.Equal(valid, CoreRuntimeContract.TryCreateStart(new AppState(), saved, out _, out _));
        profile.RealityEnabled = false;
        Assert.False(CoreRuntimeContract.TryCreateStart(new AppState(), profile, out _, out _));
    }

    [Fact]
    public void Published124CanUpgradeTo125AndSingBoxIsNotSelectable()
    {
        Assert.True(UpdateReleasePolicy.CanInstall("v1.2.5", "1.2.5", "1.2.4"));
        Assert.False(UpdateReleasePolicy.CanInstall("v1.2.5", "1.2.4", "1.2.4"));
        Assert.False(UpdateReleasePolicy.IsNewer("v1.2.4", "1.2.5"));
        using var cores = JsonDocument.Parse(JsonSerializer.Serialize(CoreEngineService.AvailableCores(new AppState())));
        Assert.DoesNotContain(cores.RootElement.EnumerateArray(), row => row.GetProperty("id").GetString() == "singBox");
    }
}
