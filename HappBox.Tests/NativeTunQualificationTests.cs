using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class NativeTunQualificationTests
{
    [Fact]
    public void PassingOnlySmokeChecksDoesNotOpenTunAndChangedBinaryInvalidatesEvidence()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10,0,22000)) return;
        var directory = Path.Combine(Path.GetTempPath(), "ior-qualification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var binary = Path.Combine(directory,"native.exe");
        try
        {
            File.WriteAllText(binary,"synthetic binary");
            Assert.False(NativeTunQualification.CanRun(binary,"xtls-rprx-vision",out _));
            NativeTunQualification.Record(binary,"xtls-rprx-vision",["tcp","dns","udp"]);
            Assert.False(NativeTunQualification.CanRun(binary,"xtls-rprx-vision",out var reason));
            Assert.Contains("откат после сбоя",reason);
            Assert.DoesNotContain("сон и пробуждение",reason);
            NativeTunQualification.Record(binary,"xtls-rprx-vision",NativeTunQualification.RequiredChecks);
            Assert.True(NativeTunQualification.CanRun(binary,"xtls-rprx-vision",out reason),reason);
            Assert.Empty(NativeTunQualification.Inspect(binary,"xtls-rprx-vision").ExtendedPassed);
            NativeTunQualification.Record(binary,"xtls-rprx-vision",["load30"]);
            Assert.Contains("load30",NativeTunQualification.Inspect(binary,"xtls-rprx-vision").ExtendedPassed);
            Assert.False(NativeTunQualification.CanRun(binary,"xtls-rprx-vision-udp443",out _));
            File.AppendAllText(binary,"different binary");
            Assert.False(NativeTunQualification.CanRun(binary,"xtls-rprx-vision",out _));
        }
        finally
        {
            File.Delete(binary);File.Delete(NativeTunQualification.ReportPath(binary));Directory.Delete(directory);
        }
    }

    [Fact]
    public void ExperimentalRealityTunCanStartWithoutQualificationReport()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10,0,22000)) return;
        var directory = Path.Combine(Path.GetTempPath(), "ior-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var binary = Path.Combine(directory,"native.exe");
        try
        {
            File.WriteAllText(binary,"test fixture, not a transport");
            var state = new AppState { SelectedCore = CoreKind.IorCore, ExperimentalCoreTesting = true,
                TunEnabled = true, MixedProxyEnabled = true, IorCorePath = binary };
            var profile = new ProxyProfile { Protocol = ProxyProtocol.Vless, RealityEnabled = true, TlsEnabled = true,
                Server = "edge.example", ServerName = "cover.example", Port = 443, PublicKey = "synthetic",
                UserId = "11111111-1111-1111-1111-111111111111", Flow = "xtls-rprx-vision" };
            Assert.True(CoreEngineService.CanRun(state,profile,out var reason),reason);
            Assert.False(File.Exists(NativeTunQualification.ReportPath(binary)));
            Assert.True(CoreEngineService.Capability(CoreKind.IorCore,state).Tun);
            state.ExperimentalCoreTesting = false;
            Assert.Equal(OperatingSystem.IsWindowsVersionAtLeast(10,0,22000), CoreEngineService.CanRun(state,profile,out _));
            Assert.Equal(OperatingSystem.IsWindowsVersionAtLeast(10,0,22000), CoreEngineService.Capability(CoreKind.IorCore,state).Tun);
            state.ExperimentalCoreTesting = true;
            profile.Flow = "xtls-rprx-vision-udp443";
            Assert.True(CoreEngineService.CanRun(state,profile,out reason),reason);
            profile.Protocol = ProxyProtocol.Hysteria2; profile.RealityEnabled = false;
            profile.Flow = null; profile.Password = "fixture";
            Assert.True(CoreEngineService.CanRun(state,profile,out reason),reason);
        }
        finally { File.Delete(binary);File.Delete(NativeTunQualification.ReportPath(binary));Directory.Delete(directory); }
    }
}
