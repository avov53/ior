using System.Text.Json;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class WindowsPackagePolicyTests
{
    [Theory]
    [InlineData(19045, false, true)]
    [InlineData(19045, true, true)]
    [InlineData(22631, false, false)]
    [InlineData(22631, true, true)]
    public void DetectsWindows10WithoutDependingOnFolderName(int build, bool marked, bool compatible) =>
        Assert.Equal(compatible, WindowsPackagePolicy.UseWindows10Compatibility(new Version(10, 0, build), marked));

    [Theory]
    [InlineData(19045, 1500, "gvisor", false)]
    [InlineData(22631, 9000, "system", true)]
    public void TunCompatibilityUsesActualPlatformAndKeepsWindows11Settings(int build, int mtu, string stack, bool strict)
    {
        var state = new AppState { TunEnabled = true, TunMtu = 9000, StackSystem = true, StrictRoute = true,
            Windows10TunCompatibilityMode = WindowsPackagePolicy.UseWindows10Compatibility(new Version(10, 0, build), false) };
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Hysteria2, Server = "192.0.2.1", Port = 443, Password = "synthetic", TlsEnabled = true };
        using var json = JsonDocument.Parse(SingBoxConfigBuilder.Build(state, profile));
        var tun = json.RootElement.GetProperty("inbounds").EnumerateArray().Single(item => item.GetProperty("type").GetString() == "tun");
        Assert.Equal(mtu, tun.GetProperty("mtu").GetInt32());
        Assert.Equal(stack, tun.GetProperty("stack").GetString());
        Assert.Equal(strict, tun.GetProperty("strict_route").GetBoolean());
    }
}
