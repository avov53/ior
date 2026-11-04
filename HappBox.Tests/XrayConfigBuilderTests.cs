using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class XrayConfigBuilderTests
{
    [Fact]
    public async Task OptInPrepareCurrentXrayTunForElevatedTrafficTest()
    {
        if (Environment.GetEnvironmentVariable("IOR_PREPARE_LIVE_XRAY_TUN") != "1") return;
        var statePath = Environment.GetEnvironmentVariable("IOR_USER_STATE_TEST_PATH");
        var outputPath = Environment.GetEnvironmentVariable("IOR_XRAY_TUN_OUTPUT");
        var binary = Environment.GetEnvironmentVariable("IOR_XRAY_TUN_TEST_BINARY");
        Assert.True(File.Exists(statePath));
        Assert.True(File.Exists(binary));
        Assert.False(string.IsNullOrWhiteSpace(outputPath));
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(outputPath!), StringComparison.OrdinalIgnoreCase);
        var state = JsonSerializer.Deserialize<AppState>(await File.ReadAllTextAsync(statePath!))!;
        var protocol = Environment.GetEnvironmentVariable("IOR_LIVE_PROTOCOL") == "hysteria2"
            ? ProxyProtocol.Hysteria2 : ProxyProtocol.Vless;
        var profile = state.Profiles.First(item => item.Protocol == protocol && (protocol != ProxyProtocol.Vless || item.RealityEnabled));
        state.TunEnabled = true;
        state.MixedProxyEnabled = true;
        state.LogLevel = "warn";
        // Isolate the transport test: arbitrary external direct-routing rules need separate validation.
        state.SelectedHappRoutingProfileId = null;
        state.HappRoutingProfiles.Clear();
        var endpoint = await BootstrapDnsResolver.ResolveIpv4Async(profile.Server, state.BootstrapDnsServer);
        var physical = PhysicalInterfaceResolver.ForIpv4(endpoint);
        var config = XrayConfigBuilder.BuildTunCandidateForValidation(state, profile, endpoint.ToString(), physical);
        await File.WriteAllTextAsync(outputPath!, config);
        var start = new ProcessStartInfo(binary!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("run"); start.ArgumentList.Add("-test"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(outputPath!);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10_000), "Xray TUN live-profile config check timed out.");
        Assert.True(process.ExitCode == 0, stdout + Environment.NewLine + stderr);
    }

    [Fact]
    public async Task OptInCurrentRoutingIsSafeForXrayTun()
    {
        if (Environment.GetEnvironmentVariable("IOR_CHECK_CURRENT_XRAY_TUN_ROUTE") != "1") return;
        var statePath = Environment.GetEnvironmentVariable("IOR_USER_STATE_TEST_PATH");
        Assert.True(File.Exists(statePath));
        var state = JsonSerializer.Deserialize<AppState>(await File.ReadAllTextAsync(statePath!))!;
        state.TunEnabled = true;
        var profile = state.Profiles.First(item => item.Protocol == ProxyProtocol.Vless && item.RealityEnabled);
        Assert.True(XrayConfigBuilder.CanBuildTun(state, profile, out var reason), reason);
    }

    [Theory]
    [InlineData(ProxyProtocol.Vless)]
    [InlineData(ProxyProtocol.Vmess)]
    [InlineData(ProxyProtocol.Trojan)]
    [InlineData(ProxyProtocol.Shadowsocks)]
    [InlineData(ProxyProtocol.Hysteria2)]
    [InlineData(ProxyProtocol.WireGuard)]
    [InlineData(ProxyProtocol.Socks)]
    [InlineData(ProxyProtocol.Http)]
    public void ProxyConfigIsValidJsonAndPassesInstalledXrayCheck(ProxyProtocol protocol)
    {
        var state = new AppState { TunEnabled = false, MixedProxyEnabled = true, ExperimentalCoreTesting = true };
        var profile = new ProxyProfile
        {
            Protocol = protocol, Server = "203.0.113.10", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111", Username = "test-user", Password = "example-password",
            Method = "aes-128-gcm", TlsEnabled = protocol is not ProxyProtocol.Shadowsocks,
            ServerName = "example.com", PrivateKey = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()),
            PeerPublicKey = Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray()),
            LocalAddress = "10.10.0.2/32", AllowedIps = "0.0.0.0/0, ::/0", PersistentKeepalive = 25
        };
        if(RemovedProtocolPolicy.IsRemoved(profile)){Assert.False(XrayConfigBuilder.CanBuild(state,profile,out _));return;}
        var json = XrayConfigBuilder.Build(state, profile);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(2, document.RootElement.GetProperty("inbounds").GetArrayLength());

        var binary = Environment.GetEnvironmentVariable("IOR_XRAY_TEST_BINARY");
        if (string.IsNullOrWhiteSpace(binary) || !File.Exists(binary)) return;
        var configPath = Path.Combine(Path.GetTempPath(), $"ior-xray-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(configPath, json);
            var start = new ProcessStartInfo(binary)
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-test");
            start.ArgumentList.Add("-c"); start.ArgumentList.Add(configPath);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10000), "Xray config check timed out.");
            Assert.True(process.ExitCode == 0, stdout + Environment.NewLine + stderr);
        }
        finally { File.Delete(configPath); }
    }

    [Fact]
    public void TunIsRejectedInsteadOfSilentlyIgnored()
    {
        var state = new AppState { TunEnabled = true };
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Vless, Server = "example.com", Port = 443 };
        Assert.False(XrayConfigBuilder.CanBuild(state, profile, out var reason));
        Assert.Contains("Proxy", reason);
    }

    [Fact]
    public void TunValidatesExplicitRoutesWithoutExperimentalFlag()
    {
        var state = new AppState { TunEnabled = true, MixedProxyEnabled = true };
        var vless = new ProxyProfile { Protocol = ProxyProtocol.Vless, Server = "203.0.113.10", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111", RealityEnabled = true,
            ServerName = "example.com", PublicKey = "test-key" };
        Assert.True(XrayConfigBuilder.CanBuildTun(state, vless, out var reason), reason);
        state.HappRoutingProfiles.Add(new HappRoutingProfile { Id = "direct", Source = RoutingProfileSource.Ior,
            GlobalProxy = true, DirectSites = ["example.org"] });
        state.SelectedHappRoutingProfileId = "direct";
        Assert.True(XrayConfigBuilder.CanBuildTun(state, vless, out reason),reason);
    }

    [Fact]
    public void HappProxyRulesKeepConfiguredOrderAndTunValidatesDirect()
    {
        var route = new HappRoutingProfile { Id = "happ-ordered", Source = RoutingProfileSource.Happ,
            Links = ["""
                {"RouteOrder":"direct-block-proxy","GlobalProxy":true,
                 "DirectSites":["local.example"],"BlockSites":["ads.example"],
                 "ProxySites":["stream.example"]}
                """] };
        var state = new AppState { TunEnabled = false, MixedProxyEnabled = true, HappRoutingProfiles = [route],
            SelectedHappRoutingProfileId = route.Id };
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Vless, Server = "203.0.113.10", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111", TlsEnabled = true,
            ServerName = "example.com" };
        var rules = JsonNode.Parse(XrayConfigBuilder.Build(state, profile))!["routing"]!["rules"]!.AsArray();
        var ordered = rules.Where(rule => rule?["domain"] is not null)
            .Select(rule => rule!["domain"]![0]!.GetValue<string>()).ToArray();
        Assert.Equal(["domain:local.example", "domain:ads.example", "domain:stream.example"], ordered);
        state.TunEnabled = true;
        profile.RealityEnabled = true;
        profile.PublicKey = "test-key";
        Assert.True(XrayConfigBuilder.CanBuildTun(state, profile, out var reason),reason);
    }

    [Fact]
    public void TunRejectsMissingSelectedRouteInsteadOfSilentlyUsingDefault()
    {
        var state = new AppState { TunEnabled = true, MixedProxyEnabled = true,
            SelectedHappRoutingProfileId = "deleted-route" };
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Vless, Server = "203.0.113.10", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111", RealityEnabled = true,
            ServerName = "example.com", PublicKey = "test-key" };
        Assert.False(XrayConfigBuilder.CanBuildTun(state, profile, out var reason));
        Assert.Contains("не найден", reason);
    }

    [Fact]
    public void Hysteria2UsesVersionTwoTransportAndAuthentication()
    {
        var state = new AppState { TunEnabled = false, MixedProxyEnabled = true };
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Hysteria2, Server = "203.0.113.10", Port = 443,
            Password = "test-secret", ServerName = "example.com", TlsEnabled = true };
        using var document = JsonDocument.Parse(XrayConfigBuilder.Build(state, profile));
        var outbound = document.RootElement.GetProperty("outbounds")[0];
        Assert.Equal("hysteria", outbound.GetProperty("protocol").GetString());
        Assert.Equal(2, outbound.GetProperty("settings").GetProperty("version").GetInt32());
        Assert.Equal("hysteria", outbound.GetProperty("streamSettings").GetProperty("method").GetString());
        Assert.Equal("test-secret", outbound.GetProperty("streamSettings").GetProperty("hysteriaSettings").GetProperty("auth").GetString());
    }

    [Fact]
    public void TunCandidateContainsWindowsAutoRoutesAndPassesOfflineXrayCheck()
    {
        var state = new AppState { TunEnabled = true, MixedProxyEnabled = true, TunMtu = 9000 };
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Vless, Server = "203.0.113.10", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111", TlsEnabled = true, ServerName = "example.com" };
        var json = XrayConfigBuilder.BuildTunCandidateForValidation(state, profile);
        using var document = JsonDocument.Parse(json);
        var tun = document.RootElement.GetProperty("inbounds")[2];
        Assert.Equal("tun", tun.GetProperty("protocol").GetString());
        Assert.Equal(1500, tun.GetProperty("settings").GetProperty("mtu").GetInt32());
        Assert.Equal("auto", tun.GetProperty("settings").GetProperty("autoOutboundsInterface").GetString());
        var binary = Environment.GetEnvironmentVariable("IOR_XRAY_TUN_TEST_BINARY");
        if (string.IsNullOrWhiteSpace(binary) || !File.Exists(binary)) return;
        var path = Path.Combine(Path.GetTempPath(), $"ior-xray-tun-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-test"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10000), "Xray TUN config check timed out.");
            Assert.True(process.ExitCode == 0, stdout + Environment.NewLine + stderr);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TunDnsDoesNotUseTheDirectBootstrapRoute()
    {
        var state = new AppState { TunEnabled = true, MixedProxyEnabled = true,
            DnsServer = "1.1.1.1", BootstrapDnsServer = "1.1.1.1" };
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Hysteria2, Server = "203.0.113.10",
            Port = 443, Password = "test", ServerName = "example.com" };
        using var document = JsonDocument.Parse(XrayConfigBuilder.BuildTunCandidateForValidation(state, profile));
        Assert.Equal("8.8.8.8", document.RootElement.GetProperty("inbounds")[2]
            .GetProperty("settings").GetProperty("dns")[0].GetString());
        Assert.Equal("1.1.1.1", document.RootElement.GetProperty("dns").GetProperty("servers")[0].GetString());
    }

    [Fact]
    public void TunCandidateUsesResolvedIpButKeepsRealitySni()
    {
        var state = new AppState { TunEnabled = true, MixedProxyEnabled = true };
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Vless, Server = "node.sslip.io", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111", TlsEnabled = true,
            RealityEnabled = true, ServerName = "cover.example", PublicKey = "test-key" };

        Assert.Throws<ArgumentException>(() => XrayConfigBuilder.BuildTunCandidateForValidation(state, profile));
        using var document = JsonDocument.Parse(XrayConfigBuilder.BuildTunCandidateForValidation(state, profile, "203.0.113.9", "Wi-Fi"));
        var outbound = document.RootElement.GetProperty("outbounds")[0];
        Assert.Equal("203.0.113.9", outbound.GetProperty("settings").GetProperty("vnext")[0].GetProperty("address").GetString());
        Assert.Equal("cover.example", outbound.GetProperty("streamSettings").GetProperty("realitySettings").GetProperty("serverName").GetString());
        Assert.Equal("Wi-Fi", document.RootElement.GetProperty("inbounds")[2].GetProperty("settings").GetProperty("autoOutboundsInterface").GetString());
        Assert.Equal("Wi-Fi", outbound.GetProperty("streamSettings").GetProperty("sockopt").GetProperty("interface").GetString());
        Assert.Equal("Wi-Fi", document.RootElement.GetProperty("outbounds")[1].GetProperty("streamSettings").GetProperty("sockopt").GetProperty("interface").GetString());
        Assert.Equal("node.sslip.io", profile.Server);
    }

    [Theory]
    [InlineData(ProxyProtocol.Vless)]
    [InlineData(ProxyProtocol.Vmess)]
    [InlineData(ProxyProtocol.Trojan)]
    [InlineData(ProxyProtocol.Shadowsocks)]
    [InlineData(ProxyProtocol.Hysteria2)]
    [InlineData(ProxyProtocol.WireGuard)]
    public void PhysicalBoundTunCandidatePassesBundledXrayOfflineCheck(ProxyProtocol protocol)
    {
        var binary = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "core", "xray", "xray.exe"));
        if (!File.Exists(binary)) return;
        var state = new AppState { TunEnabled = true, MixedProxyEnabled = true, ExperimentalCoreTesting = true };
        var profile = new ProxyProfile { Protocol = protocol, Server = "203.0.113.10", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111", Password = "example-password",
            Method = "aes-128-gcm", TlsEnabled = protocol is not ProxyProtocol.Shadowsocks,
            ServerName = "example.com", PrivateKey = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()),
            PeerPublicKey = Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray()),
            LocalAddress = "10.10.0.2/32", AllowedIps = "0.0.0.0/0, ::/0", PersistentKeepalive = 25 };
        var route = new HappRoutingProfile { Id = "direct", Source = RoutingProfileSource.Ior,
            GlobalProxy = true, DirectIp = ["1.1.1.1/32"] };
        state.HappRoutingProfiles.Add(route);
        state.SelectedHappRoutingProfileId = route.Id;
        if(RemovedProtocolPolicy.IsRemoved(profile)){Assert.False(XrayConfigBuilder.CanBuildTun(state,profile,out _));return;}
        var config = XrayConfigBuilder.BuildTunCandidateForValidation(state, profile,
            outboundInterface: "Wi-Fi");
        using var document = JsonDocument.Parse(config);
        Assert.Equal("Wi-Fi", document.RootElement.GetProperty("outbounds")[1]
            .GetProperty("streamSettings").GetProperty("sockopt").GetProperty("interface").GetString());
        Assert.Contains(document.RootElement.GetProperty("routing").GetProperty("rules").EnumerateArray(),
            rule => rule.TryGetProperty("ip", out var ips) && ips.EnumerateArray().Any(ip => ip.GetString() == "1.1.1.1/32") &&
                    rule.GetProperty("outboundTag").GetString() == "direct");
        var path = Path.Combine(Path.GetTempPath(), $"ior-xray-physical-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, config);
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(binary)! };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-test"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10_000), "Xray TUN config check timed out.");
            Assert.True(process.ExitCode == 0, output);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(ProxyProtocol.Vless)]
    [InlineData(ProxyProtocol.Vmess)]
    [InlineData(ProxyProtocol.Trojan)]
    [InlineData(ProxyProtocol.Shadowsocks)]
    public void AdditionalTunProtocolsRespectAvailabilityWithoutExperimentalFlag(ProxyProtocol protocol)
    {
        var state = new AppState { TunEnabled = true };
        var profile = new ProxyProfile { Protocol = protocol, Server = "203.0.113.10", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111", Password = "test-password",
            Method = "aes-128-gcm", TlsEnabled = protocol != ProxyProtocol.Shadowsocks, ServerName = "example.com" };
        var expected=!RemovedProtocolPolicy.IsRemoved(profile);Assert.Equal(expected,XrayConfigBuilder.CanBuildTun(state, profile, out var reason));
        state.ExperimentalCoreTesting = true;Assert.Equal(expected,XrayConfigBuilder.CanBuildTun(state, profile, out reason));
    }

    [Fact]
    public void WireGuardPreservesPeerSettingsAndRejectsInvalidParameters()
    {
        var state = new AppState { TunEnabled = false, ExperimentalCoreTesting = true };
        var profile = new ProxyProfile { Protocol = ProxyProtocol.WireGuard, Server = "2001:db8::1", Port = 51820,
            PrivateKey = Convert.ToBase64String(new byte[32]), PeerPublicKey = Convert.ToBase64String(new byte[32]),
            PreSharedKey = Convert.ToBase64String(new byte[32]), LocalAddress = "10.1.0.2/32, fd00::2/128",
            AllowedIps = "10.0.0.0/8, ::/0", PersistentKeepalive = 25, Mtu = 1380 };
        var settings = JsonNode.Parse(XrayConfigBuilder.Build(state, profile))!["outbounds"]![0]!["settings"]!;
        Assert.Equal("[2001:db8::1]:51820", settings["peers"]![0]!["endpoint"]!.GetValue<string>());
        Assert.Equal(25, settings["peers"]![0]!["keepAlive"]!.GetValue<int>());
        Assert.Equal("fd00::2", settings["address"]![1]!.GetValue<string>());
        Assert.Equal("10.0.0.0/8", settings["peers"]![0]!["allowedIPs"]![0]!.GetValue<string>());
        Assert.True(settings["noKernelTun"]!.GetValue<bool>());
        profile.PrivateKey = "invalid";
        Assert.False(XrayConfigBuilder.CanBuild(state, profile, out var reason));
        Assert.Contains("ключи", reason);
        profile.PrivateKey = Convert.ToBase64String(new byte[32]);
        profile.AllowedIps = "not-a-network";
        Assert.False(XrayConfigBuilder.CanBuild(state, profile, out reason));
        Assert.Contains("AllowedIPs", reason);
    }

    [Fact]
    public void IorRoutesKeepBlockProxyDirectPriority()
    {
        var route = new HappRoutingProfile { Id = "route", Source = RoutingProfileSource.Ior, GlobalProxy = true,
            BlockDomains = ["blocked.example"], ProxySites = ["vpn.example"], DirectIp = ["198.51.100.0/24"] };
        var state = new AppState { TunEnabled = false, MixedProxyEnabled = true,
            SelectedHappRoutingProfileId = route.Id, HappRoutingProfiles = [route] };
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Vless, Server = "203.0.113.10", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111", TlsEnabled = true, ServerName = "example.com" };
        using var document = JsonDocument.Parse(XrayConfigBuilder.Build(state, profile));
        var rules = document.RootElement.GetProperty("routing").GetProperty("rules");
        var tags = rules.EnumerateArray().Select(item => item.GetProperty("outboundTag").GetString()).ToArray();
        Assert.Contains("block", tags);
        Assert.Contains("proxy", tags);
        Assert.True(Array.IndexOf(tags, "block") < Array.IndexOf(tags, "proxy"));
        Assert.True(Array.IndexOf(tags, "proxy") < Array.LastIndexOf(tags, "direct"));
    }

    [Fact]
    public void HappGeoRulesPreserveOrderAndPassXrayCheck()
    {
        var route = new HappRoutingProfile { Id = "happ", Source = RoutingProfileSource.Happ,
            Links = ["{\"GlobalProxy\":true,\"RouteOrder\":\"block-proxy-direct\",\"DomainStrategy\":\"IPIfNonMatch\",\"BlockSites\":[\"geosite:category-ads-all\"],\"ProxySites\":[\"domain:example.com\"],\"DirectIp\":[\"geoip:ru\"]}"] };
        var state = new AppState { TunEnabled = false, MixedProxyEnabled = true,
            SelectedHappRoutingProfileId = route.Id, HappRoutingProfiles = [route] };
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Vless, Server = "203.0.113.10", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111", TlsEnabled = true, ServerName = "example.com" };
        var json = XrayConfigBuilder.Build(state, profile);
        using var document = JsonDocument.Parse(json);
        var routing = document.RootElement.GetProperty("routing");
        Assert.Equal("IPIfNonMatch", routing.GetProperty("domainStrategy").GetString());
        var rules = routing.GetProperty("rules").EnumerateArray().ToArray();
        Assert.Contains(rules, rule => rule.TryGetProperty("domain", out var domains) && domains.EnumerateArray().Any(item => item.GetString() == "geosite:category-ads-all") && rule.GetProperty("outboundTag").GetString() == "block");
        Assert.Contains(rules, rule => rule.TryGetProperty("ip", out var ips) && ips.EnumerateArray().Any(item => item.GetString() == "geoip:ru") && rule.GetProperty("outboundTag").GetString() == "direct");
        var binary = Environment.GetEnvironmentVariable("IOR_XRAY_TEST_BINARY");
        if (string.IsNullOrWhiteSpace(binary) || !File.Exists(binary)) return;
        var path = Path.Combine(Path.GetTempPath(), $"ior-xray-happ-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(binary)! };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-test"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10_000), "Xray Happ config check timed out.");
            Assert.True(process.ExitCode == 0, stdout + Environment.NewLine + stderr);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task OptInCurrentStateHappRoutePassesXrayOfflineCheck()
    {
        var statePath = Environment.GetEnvironmentVariable("IOR_USER_STATE_TEST_PATH");
        var binary = Environment.GetEnvironmentVariable("IOR_XRAY_TEST_BINARY");
        if (string.IsNullOrWhiteSpace(statePath) || !File.Exists(statePath) ||
            string.IsNullOrWhiteSpace(binary) || !File.Exists(binary)) return;
        var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(statePath))!;
        state.TunEnabled = false;
        state.MixedProxyEnabled = true;
        if (Environment.GetEnvironmentVariable("IOR_LIVE_VLESS_TEST") == "1")
            state.SelectedHappRoutingProfileId = null;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        state.MixedProxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        if (state.MixedProxyPort >= 65535) return;
        var profile = state.Profiles.First(item => item.Id == state.SelectedProfileId);
        Assert.True(XrayConfigBuilder.CanBuild(state, profile, out var reason), reason);
        var path = Path.Combine(Path.GetTempPath(), $"ior-xray-current-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, XrayConfigBuilder.Build(state, profile));
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(binary)! };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-test"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10_000), "Xray current-state check timed out.");
            Assert.True(process.ExitCode == 0, stdout + Environment.NewLine + stderr);

            var runtime = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(binary)! };
            runtime.ArgumentList.Add("run"); runtime.ArgumentList.Add("-c"); runtime.ArgumentList.Add(path);
            using var running = Process.Start(runtime)!;
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(8);
                var opened = false;
                while (DateTime.UtcNow < deadline && !running.HasExited)
                {
                    using var client = new TcpClient();
                    try { await client.ConnectAsync(IPAddress.Loopback, state.MixedProxyPort); opened = true; break; }
                    catch (SocketException) { await Task.Delay(100); }
                }
                Assert.True(opened, $"Xray did not open its local proxy; exit code: {(running.HasExited ? running.ExitCode : -1)}.");
                if (Environment.GetEnvironmentVariable("IOR_LIVE_VLESS_TEST") == "1")
                {
                    using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{state.MixedProxyPort}"), UseProxy = true };
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
                    using var response = await client.GetAsync("https://www.cloudflare.com/cdn-cgi/trace");
                    Assert.True(response.IsSuccessStatusCode, $"Xray VLESS returned HTTP {(int)response.StatusCode}.");
                }
            }
            finally
            {
                if (!running.HasExited) { running.Kill(entireProcessTree: true); await running.WaitForExitAsync(); }
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OrderedClashRulesKeepOriginalOrderInXrayProxy()
    {
        var route = new HappRoutingProfile { Id = "clash", Source = RoutingProfileSource.Clash,
            ClashOrderedRules = [
                new ClashRuleEntry { Kind = "DOMAIN-SUFFIX", Value = "example.com", Target = "PROXY", Raw = "DOMAIN-SUFFIX,example.com,PROXY" },
                new ClashRuleEntry { Kind = "DOMAIN", Value = "ads.example", Target = "REJECT", Raw = "DOMAIN,ads.example,REJECT" },
                new ClashRuleEntry { Kind = "MATCH", Target = "DIRECT", Raw = "MATCH,DIRECT" }
            ] };
        var state = new AppState { TunEnabled = false, MixedProxyEnabled = true,
            SelectedHappRoutingProfileId = route.Id, HappRoutingProfiles = [route] };
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Vless, Server = "203.0.113.10", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111" };
        Assert.True(XrayConfigBuilder.CanBuild(state, profile, out var reason), reason);
        var config = XrayConfigBuilder.Build(state, profile);
        using var document = JsonDocument.Parse(config);
        var rules = document.RootElement.GetProperty("routing").GetProperty("rules");
        Assert.Equal("proxy", rules[2].GetProperty("outboundTag").GetString());
        Assert.Equal("block", rules[3].GetProperty("outboundTag").GetString());
        Assert.Equal("direct", rules[4].GetProperty("outboundTag").GetString());
        var binary = Environment.GetEnvironmentVariable("IOR_XRAY_TEST_BINARY");
        if (string.IsNullOrWhiteSpace(binary) || !File.Exists(binary)) return;
        var path = Path.Combine(Path.GetTempPath(), $"ior-clash-xray-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, config);
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(binary)! };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-test"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10_000), "Xray route test timed out.");
            Assert.True(process.ExitCode == 0, output);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void XrayTunAcceptsOrderedProxyBlockAndDirect()
    {
        var route = new HappRoutingProfile { Id = "clash", Source = RoutingProfileSource.Clash,
            ClashOrderedRules = [
                new ClashRuleEntry { Kind = "DOMAIN", Value = "ads.example", Target = "REJECT", Raw = "DOMAIN,ads.example,REJECT" },
                new ClashRuleEntry { Kind = "MATCH", Target = "PROXY", Raw = "MATCH,PROXY" }
            ] };
        var state = new AppState { TunEnabled = true, MixedProxyEnabled = true,
            SelectedHappRoutingProfileId = route.Id, HappRoutingProfiles = [route] };
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Vless, Server = "203.0.113.10", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111", RealityEnabled = true,
            ServerName = "example.com", PublicKey = "test-key" };
        Assert.True(XrayConfigBuilder.CanBuildTun(state, profile, out var reason), reason);
        route.ClashOrderedRules.Insert(0, new ClashRuleEntry { Kind = "IP-CIDR", Value = "8.8.8.8/32", Target = "DIRECT", Raw = "IP-CIDR,8.8.8.8/32,DIRECT" });
        Assert.True(XrayConfigBuilder.CanBuildTun(state, profile, out reason),reason);
    }

    [Fact]
    public void ClashCompilerRejectsRuleDroppedByParserOrUnpreservedNoResolve()
    {
        var route = new HappRoutingProfile { Id = "clash", Source = RoutingProfileSource.Clash,
            ClashOrderedRules = [new ClashRuleEntry { Kind = "MATCH", Target = "PROXY", Raw = "MATCH,PROXY" }],
            ClashUnmappedRules = ["BROKEN"] };
        Assert.False(XrayClashRuleCompiler.TryCompile(route, out _, out _, out var reason));
        Assert.Contains("BROKEN", reason);
        route.ClashUnmappedRules.Clear();
        route.ClashOrderedRules.Insert(0, new ClashRuleEntry { Kind = "GEOIP", Value = "RU", Target = "DIRECT",
            NoResolve = true, Raw = "GEOIP,RU,DIRECT,no-resolve" });
        Assert.False(XrayClashRuleCompiler.TryCompile(route, out _, out _, out reason));
        Assert.Contains("no-resolve", reason);
    }

    [Fact]
    public void ImportedClashRuleOrderCanBeUsedByXrayWithoutReordering()
    {
        const string yaml = "rules:\n  - DOMAIN-SUFFIX,example.com,PROXY\n  - DOMAIN,ads.example,REJECT\n  - MATCH,PROXY\n";
        var route = ClashProfileParser.Parse(yaml);
        Assert.NotEmpty(route.ClashUnmappedRules); // The legacy bucket model cannot preserve this order.
        Assert.True(XrayClashRuleCompiler.TryCompile(route, out var rules, out var hasDirect, out var reason), reason);
        Assert.False(hasDirect);
        Assert.Equal(new[] { "proxy", "block", "proxy" },
            rules.Select(rule => rule!["outboundTag"]!.GetValue<string>()));
    }

    [Fact]
    public async Task InstalledXrayOpensLocalProxyWithoutVpnConnection()
    {
        var binary = Environment.GetEnvironmentVariable("IOR_XRAY_TEST_BINARY");
        if (string.IsNullOrWhiteSpace(binary) || !File.Exists(binary)) return;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var state = new AppState { TunEnabled = false, MixedProxyEnabled = true, MixedProxyPort = port };
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Vless, Server = "203.0.113.10", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111", TlsEnabled = true, ServerName = "example.com" };
        var configPath = Path.Combine(Path.GetTempPath(), $"ior-xray-smoke-{Guid.NewGuid():N}.json");
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(configPath, XrayConfigBuilder.Build(state, profile));
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(configPath);
            process = Process.Start(start);
            Assert.NotNull(process);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            var opened = false;
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                using var client = new TcpClient();
                try { await client.ConnectAsync(IPAddress.Loopback, port); opened = true; break; }
                catch (SocketException) { await Task.Delay(75); }
            }
            Assert.True(opened, "Xray did not open its local HTTP proxy port.");
        }
        finally
        {
            if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            process?.Dispose();
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task Hysteria2ProxyCarriesAnHttpRequestThroughLocalXrayServer()
    {
        var binary = Environment.GetEnvironmentVariable("IOR_XRAY_TEST_BINARY");
        if (string.IsNullOrWhiteSpace(binary) || !File.Exists(binary)) return;
        using var endpointReservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var hysteriaPort = ((IPEndPoint)endpointReservation.Client.LocalEndPoint!).Port;
        endpointReservation.Close();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var destinationPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var udpEcho = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var udpEchoPort = ((IPEndPoint)udpEcho.Client.LocalEndPoint!).Port;
        var proxyReservation = new TcpListener(IPAddress.Loopback, 0);
        proxyReservation.Start();
        var proxyPort = ((IPEndPoint)proxyReservation.LocalEndpoint).Port;
        proxyReservation.Stop();
        var testDir = Path.Combine(Path.GetTempPath(), $"ior-xray-hysteria-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        Process? server = null;
        Process? client = null;
        try
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var certPath = Path.Combine(testDir, "cert.pem");
            var keyPath = Path.Combine(testDir, "key.pem");
            await File.WriteAllTextAsync(certPath, certificate.ExportCertificatePem());
            await File.WriteAllTextAsync(keyPath, rsa.ExportPkcs8PrivateKeyPem());
            var serverConfig = new
            {
                log = new { loglevel = "debug" },
                inbounds = new[] { new
                {
                    listen = "127.0.0.1", port = hysteriaPort, protocol = "hysteria",
                    settings = new { version = 2, users = new[] { new { auth = "local-test-secret" } } },
                    streamSettings = new { method = "hysteria", security = "tls",
                        tlsSettings = new { alpn = new[] { "h3" }, certificates = new[] { new { certificateFile = certPath, keyFile = keyPath } } },
                        hysteriaSettings = new { version = 2 } }
                } },
                outbounds = new[] { new { protocol = "freedom", settings = new { finalRules = new[]
                {
                    new { action = "allow", network = "tcp", port = destinationPort.ToString(), ip = new[] { "127.0.0.1/32" } },
                    new { action = "allow", network = "udp", port = udpEchoPort.ToString(), ip = new[] { "127.0.0.1/32" } }
                } } } }
            };
            var serverConfigPath = Path.Combine(testDir, "server.json");
            await File.WriteAllTextAsync(serverConfigPath, JsonSerializer.Serialize(serverConfig));
            var state = new AppState { TunEnabled = false, MixedProxyEnabled = true, MixedProxyPort = proxyPort };
            var profile = new ProxyProfile { Protocol = ProxyProtocol.Hysteria2, Server = "127.0.0.1", Port = hysteriaPort,
                Password = "local-test-secret", ServerName = "localhost", TlsEnabled = true };
            var clientConfig = JsonNode.Parse(XrayConfigBuilder.Build(state, profile))!;
            clientConfig["log"]!["loglevel"] = "debug";
            clientConfig["outbounds"]![0]!["streamSettings"]!["tlsSettings"]!["pinnedPeerCertSha256"] = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
            clientConfig["routing"]!["rules"] = new JsonArray();
            var clientConfigPath = Path.Combine(testDir, "client.json");
            await File.WriteAllTextAsync(clientConfigPath, clientConfig.ToJsonString());
            server = StartXray(binary, serverConfigPath);
            client = StartXray(binary, clientConfigPath);
            await Task.Delay(250);
            Assert.False(server.HasExited, server.HasExited ? await server.StandardOutput.ReadToEndAsync() + await server.StandardError.ReadToEndAsync() : "Xray server exited.");
            Assert.False(client.HasExited, client.HasExited ? await client.StandardOutput.ReadToEndAsync() + await client.StandardError.ReadToEndAsync() : "Xray client exited.");
            var responseTask = Task.Run(async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync();
                using var stream = connection.GetStream();
                var buffer = new byte[4096];
                await stream.ReadAsync(buffer);
                var response = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\nthrough");
                await stream.WriteAsync(response);
            });
            using var proxyClient = new TcpClient();
            var proxyReady = false;
            for (var attempt = 0; attempt < 20 && !proxyReady; attempt++)
            {
                try { await proxyClient.ConnectAsync(IPAddress.Loopback, proxyPort).WaitAsync(TimeSpan.FromMilliseconds(500)); proxyReady = true; }
                catch (SocketException) { await Task.Delay(100); }
            }
            Assert.True(proxyReady, "Xray did not open the local HTTP proxy port.");
            using var proxyStream = proxyClient.GetStream();
            var proxyRequest = System.Text.Encoding.ASCII.GetBytes($"GET http://127.0.0.1:{destinationPort}/ HTTP/1.1\r\nHost: 127.0.0.1:{destinationPort}\r\nConnection: close\r\n\r\n");
            await proxyStream.WriteAsync(proxyRequest);
            using var responseBytes = new MemoryStream();
            try { await proxyStream.CopyToAsync(responseBytes).WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (TimeoutException)
            {
                if (client is { HasExited: false }) { client.Kill(entireProcessTree: true); await client.WaitForExitAsync(); }
                if (server is { HasExited: false }) { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
                throw new TimeoutException("Hysteria2 request timed out. Client: " + await client!.StandardOutput.ReadToEndAsync() + await client.StandardError.ReadToEndAsync() + " Server: " + await server!.StandardOutput.ReadToEndAsync() + await server.StandardError.ReadToEndAsync());
            }
            var responseText = System.Text.Encoding.ASCII.GetString(responseBytes.ToArray());
            Assert.Contains("through", responseText);
            await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
            var udpEchoTask = Task.Run(async () =>
            {
                var packet = await udpEcho.ReceiveAsync();
                await udpEcho.SendAsync(packet.Buffer, packet.Buffer.Length, packet.RemoteEndPoint);
            });
            using var socks = new TcpClient();
            await socks.ConnectAsync(IPAddress.Loopback, proxyPort + 1);
            using var socksStream = socks.GetStream();
            await socksStream.WriteAsync(new byte[] { 5, 1, 0 });
            var greeting = new byte[2];
            await socksStream.ReadExactlyAsync(greeting);
            Assert.Equal(new byte[] { 5, 0 }, greeting);
            await socksStream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 });
            var association = new byte[10];
            await socksStream.ReadExactlyAsync(association);
            Assert.Equal(0, association[1]);
            var relayPort = (association[8] << 8) | association[9];
            using var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var payload = System.Text.Encoding.ASCII.GetBytes("udp-through");
            var datagram = new byte[10 + payload.Length];
            datagram[3] = 1;
            datagram[4] = 127; datagram[5] = 0; datagram[6] = 0; datagram[7] = 1;
            datagram[8] = (byte)(udpEchoPort >> 8); datagram[9] = (byte)udpEchoPort;
            payload.CopyTo(datagram, 10);
            await udpClient.SendAsync(datagram, datagram.Length, new IPEndPoint(IPAddress.Loopback, relayPort));
            var udpReply = await udpClient.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.EndsWith("udp-through", System.Text.Encoding.ASCII.GetString(udpReply.Buffer));
            await udpEchoTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            listener.Stop();
            foreach (var process in new[] { client, server })
            {
                if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                process?.Dispose();
            }
            Directory.Delete(testDir, recursive: true);
        }
    }

    private static Process StartXray(string binary, string config)
    {
        var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(config);
        return Process.Start(start)!;
    }
}
