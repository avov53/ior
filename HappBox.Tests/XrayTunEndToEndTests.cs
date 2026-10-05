using System.Diagnostics;
using System.Net;
using System.Text.Json;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class XrayTunEndToEndTests
{
    [Fact]
    public async Task OptInXrayTunUsesPhysicalHelperForHappDirectTcpAndUdp()
    {
        if (Environment.GetEnvironmentVariable("IOR_LIVE_XRAY_DIRECT_HELPER") != "1") return;
        var statePath = Environment.GetEnvironmentVariable("IOR_USER_STATE_TEST_PATH")!;
        var binary = Environment.GetEnvironmentVariable("IOR_XRAY_TUN_TEST_BINARY")!;
        Assert.Empty(Process.GetProcessesByName("xray"));
        var state = JsonSerializer.Deserialize<AppState>(await File.ReadAllTextAsync(statePath))!;
        var profile = state.Profiles.First(item => item.Protocol == ProxyProtocol.Vless && item.RealityEnabled);
        state.TunEnabled = true; state.MixedProxyEnabled = true; state.ExperimentalCoreTesting = true;
        state.LogLevel = "warn";
        var server = await BootstrapDnsResolver.ResolveIpv4Async(profile.Server, state.BootstrapDnsServer);
        var physical = PhysicalInterfaceResolver.ForIpv4(server);
        var initialForwarding = await XrayTunRouteLease.PhysicalForwardingEnabledAsync(physical);
        var cloudflare = await System.Net.Dns.GetHostAddressesAsync("www.cloudflare.com");
        var addresses = cloudflare.Where(item => item.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(item => item + "/32").Append("8.8.8.8/32").ToArray();
        var route = new HappRoutingProfile { Id = "test-direct", Name = "local reference route", Source = RoutingProfileSource.Happ,
            Links = [JsonSerializer.Serialize(new { RouteOrder = "direct-block-proxy", GlobalProxy = true,
                DirectSites = new[] { "domain:yandex.ru" }, DirectIp = addresses })] };
        state.HappRoutingProfiles = [route]; state.SelectedHappRoutingProfileId = route.Id;
        Assert.True(XrayConfigBuilder.CanBuildTun(state, profile, out var reason), reason);
        var directory = Path.Combine(Path.GetTempPath(), "ior-xray-helper-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "private-config.json");
        await using var helper = DirectEgressHelper.ForPhysicalInterface(physical, state.BootstrapDnsServer);
        XrayTunRouteLease? lease = null;
        Process? process = null;
        try
        {
            await helper.VerifyPhysicalEgressAsync("before");
            await File.WriteAllTextAsync(path, XrayConfigBuilder.BuildTunCandidateForValidation(state, profile, server.ToString(), physical, helper));
            lease = await XrayTunRouteLease.AcquireAsync(directory, server, BootstrapDnsResolver.ServerAddress(state.BootstrapDnsServer), physical,
                allowTemporaryForwardingDisable: Environment.GetEnvironmentVariable("IOR_ALLOW_FORWARDING_CHANGE") == "1");
            Assert.False(await XrayTunRouteLease.PhysicalForwardingEnabledAsync(physical));
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(binary)! };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(path);
            process = Process.Start(start)!;
            var lines = 0;
            process.OutputDataReceived += (_, args) => { if (args.Data is not null) Interlocked.Increment(ref lines); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is not null) Interlocked.Increment(ref lines); };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            var probe = await XrayTunHealthProbe.CheckAsync(server, physical);
            Assert.True(probe.Ready, probe.Reason);
            await helper.VerifyPhysicalEgressAsync("after");
            Assert.True(helper.TcpConnections > 0, "Xray did not send DIRECT TCP through the helper.");
            Assert.True(helper.UdpDatagrams > 0, "Xray did not send DIRECT UDP through the helper.");
            var beforeRussianRequest = helper.TcpConnections;
            using var handler = new HttpClientHandler { UseProxy = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://yandex.ru");
            using var response = await http.SendAsync(request);
            Assert.True((int)response.StatusCode < 500, "Russian-site DIRECT HTTPS failed.");
            Assert.True(helper.TcpConnections > beforeRussianRequest, "Russian domain did not use DIRECT.");
            Assert.True(lines < 500, "Unexpected repeated TUN connections.");
        }
        finally
        {
            if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            process?.Dispose();
            if (lease is not null) await lease.ReleaseAsync();
            Assert.Equal(initialForwarding, await XrayTunRouteLease.PhysicalForwardingEnabledAsync(physical));
            if (File.Exists(path)) File.Delete(path);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData(ProxyProtocol.Vless)]
    [InlineData(ProxyProtocol.Hysteria2)]
    public async Task OptInXrayTunCarriesHttpsAndUdpAndReleasesRoutes(ProxyProtocol protocol)
    {
        if (Environment.GetEnvironmentVariable("IOR_LIVE_XRAY_TUN_E2E") != "1") return;
        var statePath = Environment.GetEnvironmentVariable("IOR_USER_STATE_TEST_PATH");
        var binary = Environment.GetEnvironmentVariable("IOR_XRAY_TUN_TEST_BINARY");
        Assert.True(File.Exists(statePath));
        Assert.True(File.Exists(binary));
        Assert.Empty(Process.GetProcessesByName("xray"));

        var state = JsonSerializer.Deserialize<AppState>(await File.ReadAllTextAsync(statePath!))!;
        var profile = state.Profiles.First(item => item.Protocol == protocol &&
            (protocol != ProxyProtocol.Vless || item.RealityEnabled));
        state.TunEnabled = true;
        state.MixedProxyEnabled = true;
        state.LogLevel = "warn";
        state.SelectedHappRoutingProfileId = null;
        state.HappRoutingProfiles.Clear();
        var server = await BootstrapDnsResolver.ResolveIpv4Async(profile.Server, state.BootstrapDnsServer);
        var physical = PhysicalInterfaceResolver.ForIpv4(server);
        var directory = Path.Combine(Path.GetTempPath(), $"ior-xray-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.json");
        Process? process = null;
        XrayTunRouteLease? lease = null;
        try
        {
            var config = XrayConfigBuilder.BuildTunCandidateForValidation(state, profile, server.ToString(), physical);
            await File.WriteAllTextAsync(configPath, config);
            lease = await XrayTunRouteLease.AcquireAsync(directory, server,
                BootstrapDnsResolver.ServerAddress(state.BootstrapDnsServer), physical);
            var start = new ProcessStartInfo(binary!) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(binary!)! };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(configPath);
            process = Process.Start(start) ?? throw new InvalidOperationException("Xray did not start.");
            var outputLines = 0;
            process.OutputDataReceived += (_, args) => { if (args.Data is not null) Interlocked.Increment(ref outputLines); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is not null) Interlocked.Increment(ref outputLines); };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            var probe = await XrayTunHealthProbe.CheckAsync(server, physical);
            Assert.True(outputLines < 4000, "Xray emitted an excessive number of TUN log lines.");
            Assert.True(probe.Ready, probe.Reason);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            if (lease is not null) await lease.ReleaseAsync();
            if (File.Exists(configPath)) File.Delete(configPath);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
}
