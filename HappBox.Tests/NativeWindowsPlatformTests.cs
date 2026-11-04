using System.Text.Json;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class NativeWindowsPlatformTests
{
    [Theory]
    [InlineData(6, 3, 9600, false)]
    [InlineData(10, 0, 10240, false)]
    [InlineData(10, 0, 14393, true)]
    [InlineData(10, 0, 17763, true)]
    [InlineData(10, 0, 19045, true)]
    [InlineData(10, 0, 22000, true)]
    [InlineData(10, 0, 26100, true)]
    public void NativeTunPlatformBoundary(int major, int minor, int build, bool supported) =>
        Assert.Equal(supported, NativeWindowsPlatform.SupportsTun(new Version(major, minor, build)));

    [Theory]
    [InlineData(19045, false)]
    [InlineData(22631, true)]
    public void Windows10CanRunWithoutClaimingWindows11Verification(int build, bool verified)
    {
        using var matrix = JsonDocument.Parse(JsonSerializer.Serialize(CoreEngineService.NativeCombinations(
            new AppState { ExperimentalCoreTesting = true }, new Version(10, 0, build))));
        foreach (var row in matrix.RootElement.EnumerateArray().Where(row => row.GetProperty("mode").GetString() == "tun"))
        {
            var promoted = row.GetProperty("protocol").GetString() is "VLESS Reality" or "Hysteria2";
            Assert.Equal("supported", row.GetProperty("status").GetString());
            Assert.Equal(promoted && verified, row.GetProperty("verified").GetBoolean());
        }
    }

    [Theory]
    [InlineData(19045, 22631, false)]
    [InlineData(22631, 19045, false)]
    [InlineData(10240, 19045, false)]
    [InlineData(14393, 19045, true)]
    [InlineData(22000, 26100, true)]
    public void QualificationCannotCrossWindowsFamilies(int recorded, int current, bool matches) =>
        Assert.Equal(matches, NativeWindowsPlatform.SameTestedFamily(recorded, current));

    [Fact]
    public void RecordingOnOtherWindowsFamilyDoesNotInheritSuccessfulChecks()
    {
        if (!NativeWindowsPlatform.TunAvailable) return;
        var directory = Path.Combine(Path.GetTempPath(), "ior-family-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var binary = Path.Combine(directory, "native.exe");
        try
        {
            File.WriteAllText(binary, "synthetic qualification binary");
            NativeTunQualification.Record(binary, "xtls-rprx-vision", NativeTunQualification.RequiredChecks);
            var path = NativeTunQualification.ReportPath(binary);
            var proof = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            proof["WindowsBuild"] = NativeWindowsPlatform.IsWindows11(Environment.OSVersion.Version) ? 19045 : 22631;
            File.WriteAllText(path, proof.ToJsonString());
            Assert.False(NativeTunQualification.Inspect(binary, "xtls-rprx-vision").Ready);
            NativeTunQualification.Record(binary, "xtls-rprx-vision", ["tcp"]);
            var status = NativeTunQualification.Inspect(binary, "xtls-rprx-vision");
            Assert.False(status.Ready);
            Assert.Contains("dns", status.Missing);
            Assert.DoesNotContain("tcp", status.Missing);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StandardWireGuardIsAvailableForNativeExperimentWithValidatedProfile(bool tun)
    {
        var state = new AppState { SelectedCore = CoreKind.IorCore, ExperimentalCoreTesting = true,
            MixedProxyEnabled = true, TunEnabled = tun, IorCorePath = Environment.ProcessPath };
        var key = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
        var profile = new ProxyProfile { Protocol = ProxyProtocol.WireGuard, Server = "192.0.2.1", Port = 51820,
            PrivateKey = key, PeerPublicKey = key, LocalAddress = "10.7.0.2/32", AllowedIps = "0.0.0.0/0", Mtu = 1380 };
        Assert.Equal(!tun || NativeWindowsPlatform.TunAvailable, CoreEngineService.CanRun(state, profile, out _));
        Assert.True(CoreRuntimeContract.TryCreateStart(state, profile, out var request, out var reason), reason);
        Assert.Equal("wireguard", request!.Profile.Protocol);
        Assert.Equal(1380, NativeTunnelPolicy.EffectiveMtu(state, profile));
        profile.PeerPublicKey = "invalid";
        Assert.False(CoreEngineService.CanRun(state, profile, out _));
    }
}
