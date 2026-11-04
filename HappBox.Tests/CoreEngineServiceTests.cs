using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class CoreEngineServiceTests
{
    [Fact]
    public void ExperimentalRealityProxyCanAttemptAuthenticationWithoutClaimingSuccess()
    {
        var state=new AppState {SelectedCore=CoreKind.IorCore,ExperimentalCoreTesting=true,TunEnabled=false,MixedProxyEnabled=true,IorCorePath=Environment.ProcessPath};
        var profile=new ProxyProfile {Protocol=ProxyProtocol.Vless,RealityEnabled=true,TlsEnabled=true,Server="edge.example",Port=443,ServerName="cover.example",UserId="11111111-1111-1111-1111-111111111111",PublicKey="synthetic",Flow="xtls-rprx-vision"};
        Assert.True(CoreEngineService.CanRun(state,profile,out var promotedReason),promotedReason);
        state.ExperimentalCoreTesting=false;
        Assert.True(CoreEngineService.CanRun(state,profile,out var reason),reason);
    }

    [Theory]
    [InlineData(ProxyProtocol.Hysteria2, false)]
    [InlineData(ProxyProtocol.Hysteria2, true)]
    [InlineData(ProxyProtocol.Vless, false)]
    [InlineData(ProxyProtocol.Vless, true)]
    public void PromotedIorCoreCombinationsDoNotRequireExperimentalSwitch(ProxyProtocol protocol, bool tun)
    {
        var state = new AppState { SelectedCore = CoreKind.IorCore, ExperimentalCoreTesting = false, TunEnabled = tun, MixedProxyEnabled = true, IorCorePath = Environment.ProcessPath };
        var profile = protocol == ProxyProtocol.Vless
            ? new ProxyProfile { Protocol = protocol, RealityEnabled = true, TlsEnabled = true, Server = "edge.example", Port = 443, ServerName = "cover.example", UserId = "11111111-1111-1111-1111-111111111111", PublicKey = "synthetic", Flow = "xtls-rprx-vision" }
            : new ProxyProfile { Protocol = protocol, Server = "edge.example", Port = 443, ServerName = "cover.example", Password = "synthetic", TlsEnabled = true };
        var supported = !tun || NativeWindowsPlatform.TunAvailable;
        Assert.Equal(supported, CoreEngineService.CanRun(state, profile, out _));
    }

    [Fact]
    public void NonPromotedIorCoreProtocolsRemainBehindExperimentalSwitch()
    {
        var state = new AppState { SelectedCore = CoreKind.IorCore, ExperimentalCoreTesting = false, TunEnabled = false, MixedProxyEnabled = true, IorCorePath = Environment.ProcessPath };
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Vmess, Server = "edge.example", Port = 443, ServerName = "cover.example", UserId = "11111111-1111-1111-1111-111111111111" };
        Assert.False(CoreEngineService.CanRun(state, profile, out _));
        state.ExperimentalCoreTesting = true;
        Assert.False(CoreEngineService.CanRun(state, profile, out var reason));Assert.Contains("пока не поддерживаются",reason);
    }

    [Fact]
    public void NativeMatrixKeepsPromotedRowsAvailableWhenFlagIsOff()
    {
        var state = new AppState { ExperimentalCoreTesting = false };
        using var matrix = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(CoreEngineService.NativeCombinations(state)));
        var rows = matrix.RootElement.EnumerateArray().ToArray();
        foreach (var row in rows.Where(row => row.GetProperty("protocol").GetString() is "VLESS Reality" or "Hysteria2"))
        {
            var tunSupported = row.GetProperty("mode").GetString() != "tun" || NativeWindowsPlatform.TunAvailable;
            Assert.Equal(tunSupported ? "supported" : "unverifiedWindowsVersion", row.GetProperty("status").GetString());
            Assert.Equal(tunSupported && (row.GetProperty("mode").GetString() != "tun" || NativeWindowsPlatform.IsWindows11(Environment.OSVersion.Version)), row.GetProperty("verified").GetBoolean());
        }
        Assert.All(rows.Where(row => row.GetProperty("protocol").GetString() is "Vmess" or "Shadowsocks" or "Trojan" or "WireGuard"),
            row => Assert.Equal("supported", row.GetProperty("status").GetString()));
    }
    [Fact]
    public void NativeBackendCanConnectButProfileCapabilityChecksRemainSeparate()
    {
        Assert.True(CoreEngineService.CanSelect(CoreKind.SingBox));
        Assert.True(CoreEngineService.CanSelect(CoreKind.IorCore));
        Assert.True(CoreEngineService.CanSelect(CoreKind.XrayCore));
        Assert.True(CoreEngineService.CanConnect(CoreKind.SingBox));
        Assert.True(CoreEngineService.CanConnect(CoreKind.IorCore));
        Assert.True(CoreEngineService.CanConnect(CoreKind.XrayCore));
    }

    [Fact]
    public void BackendIsShownOnlyWhileRunning()
    {
        var state = new AppState();
        Assert.Equal(CoreKind.SingBox, state.SelectedCore);
        Assert.Null(CoreEngineService.ActiveBackend(state, false));
        Assert.Equal("sing-box", CoreEngineService.ActiveBackend(state, true));
    }

    [Fact]
    public void SelectedCoreAndActuallyRunningCoreRemainDistinct()
    {
        var state = new AppState { SelectedCore = CoreKind.XrayCore, RunningCore = CoreKind.SingBox };
        Assert.Equal("sing-box", CoreEngineService.ActiveBackend(state, true));
        Assert.Equal(CoreKind.XrayCore, state.SelectedCore);
    }

    [Fact]
    public void XrayCapabilitiesExposeTunOnSupportedWindowsVersions()
    {
        var capabilities = CoreEngineService.Capability(CoreKind.XrayCore);
        Assert.True(capabilities.Proxy);
        Assert.Equal(NativeWindowsPlatform.TunAvailable, capabilities.Tun);
        Assert.True(capabilities.Udp);
        Assert.Contains(ProxyProtocol.Hysteria2, capabilities.Protocols);
    }
}
