using System.Text.Json.Nodes;
using System.Text.Json;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class ConfigBuilderTests
{
    [Fact]
    public void OptInPhysicalInterfacePreflightFindsRealAdapter()
    {
        if (Environment.GetEnvironmentVariable("IOR_NETWORK_INTERFACE_TEST") != "1") return;
        var name = PhysicalInterfaceResolver.ForIpv4(IPAddress.Parse("1.1.1.1"));
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.False(name.Equals("ior", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OptInPinnedTunConfigPassesBundledSingBoxCheck()
    {
        if (Environment.GetEnvironmentVariable("IOR_NETWORK_INTERFACE_TEST") != "1") return;
        var binary = Environment.GetEnvironmentVariable("IOR_SINGBOX_TEST_BINARY");
        Assert.True(File.Exists(binary));
        var name = PhysicalInterfaceResolver.ForIpv4(IPAddress.Parse("1.1.1.1"));
        var json = SingBoxConfigBuilder.Build(new AppState { TunEnabled = true, MixedProxyEnabled = false },
            Vless(), outboundInterface: name);
        var path = Path.Combine(Path.GetTempPath(), $"ior-pinned-tun-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            var start = new ProcessStartInfo(binary!) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("check"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10_000), "sing-box check timed out.");
            Assert.True(process.ExitCode == 0, output);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task OptInCurrentVlessRealityTransportWorksThroughLocalProxy()
    {
        if (Environment.GetEnvironmentVariable("IOR_LIVE_VLESS_TEST") != "1") return;
        var statePath = Environment.GetEnvironmentVariable("IOR_USER_STATE_TEST_PATH");
        var binary = Environment.GetEnvironmentVariable("IOR_SINGBOX_TEST_BINARY");
        Assert.True(File.Exists(statePath));
        Assert.True(File.Exists(binary));
        var state = JsonSerializer.Deserialize<AppState>(await File.ReadAllTextAsync(statePath!))!;
        var profile = state.Profiles.First(item => item.Protocol == ProxyProtocol.Vless && item.RealityEnabled);
        if (Environment.GetEnvironmentVariable("IOR_TEST_FINGERPRINT") is { Length: > 0 } fingerprint)
            profile.Fingerprint = fingerprint;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        state.MixedProxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        state.TunEnabled = false;
        state.MixedProxyEnabled = true;
        state.SelectedHappRoutingProfileId = null;
        state.LogLevel = "debug";
        var path = Path.Combine(Path.GetTempPath(), $"ior-sing-vless-{Guid.NewGuid():N}.json");
        Process? process = null;
        try
        {
            var config = SingBoxConfigBuilder.Build(state, profile);
            if (Environment.GetEnvironmentVariable("IOR_TEST_CURVES") == "1")
            {
                var root = JsonNode.Parse(config)!;
                root["outbounds"]![0]!["tls"]!["curve_preferences"] =
                    new JsonArray("X25519MLKEM768", "X25519");
                config = root.ToJsonString();
            }
            await File.WriteAllTextAsync(path, config);
            var start = new ProcessStartInfo(binary!) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(binary!)! };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(path);
            process = Process.Start(start);
            Assert.NotNull(process);
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) lines.Enqueue(eventArgs.Data); };
            process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) lines.Enqueue(eventArgs.Data); };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{state.MixedProxyPort}"), UseProxy = true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                using var probe = new TcpClient();
                try { await probe.ConnectAsync(IPAddress.Loopback, state.MixedProxyPort); break; }
                catch (SocketException) { await Task.Delay(100); }
            }
            try
            {
                using var response = await client.GetAsync("https://www.cloudflare.com/cdn-cgi/trace");
                Assert.True(response.IsSuccessStatusCode, $"VLESS Reality proxy returned HTTP {(int)response.StatusCode}.");
            }
            catch (HttpRequestException)
            {
                var trace = string.Join(" | ", lines.Where(line => line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)));
                var details = string.Join(" | ", lines.Where(line => line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)).TakeLast(3));
                details = System.Text.RegularExpressions.Regex.Replace(details, @"(?:[a-zA-Z0-9-]+\.)+[a-zA-Z]{2,}|(?:\d{1,3}\.){3}\d{1,3}", "[host]");
                Assert.Fail($"VLESS transport failed. Reality verification: {trace.Contains("reality verification failed", StringComparison.OrdinalIgnoreCase)}; DNS error: {trace.Contains("dns", StringComparison.OrdinalIgnoreCase)}; outbound reached: {lines.Any(line => line.Contains("outbound/vless", StringComparison.OrdinalIgnoreCase))}; details: {details}");
            }
        }
        finally
        {
            if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            process?.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void TunUsesNormalOptions()
    {
        var state = new AppState
        {
            TunEnabled = true,
            MixedProxyEnabled = false,
            AutoRoute = true,
            StrictRoute = true
        };

        var tun = BuildTun(state);

        Assert.True(tun["auto_route"]!.GetValue<bool>());
        Assert.True(tun["strict_route"]!.GetValue<bool>());
        Assert.Equal("gvisor", tun["stack"]!.GetValue<string>());
        Assert.Null(tun["endpoint_independent_nat"]);
        Assert.Null(tun["route_address"]);
        Assert.Equal(9000, tun["mtu"]!.GetValue<int>());
        Assert.Equal(["91.186.217.186/32"], tun["route_exclude_address"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void HappRoutingWithoutGlobalProxyKeepsUnmatchedTrafficDirect()
    {
        var state = new AppState
        {
            TunEnabled = true,
            HappRoutingProfiles =
            [
                new HappRoutingProfile
                {
                    Id = "managed",
                    Name = "Managed",
                    Links = ["{\"ProxySites\":[\"youtube.com\"],\"GlobalProxy\":false}"]
                }
            ],
            SelectedHappRoutingProfileId = "managed"
        };

        var root = JsonNode.Parse(SingBoxConfigBuilder.Build(state, Vless()))!.AsObject();

        Assert.Equal("direct", root["route"]!["final"]!.GetValue<string>());
    }

    [Fact]
    public void HappGeoIpIsEmittedAsRuleSetNotInvalidIpCidr()
    {
        var state = new AppState
        {
            TunEnabled = true,
            HappRoutingProfiles = [new HappRoutingProfile
            {
                Id = "happ-geo", Links = ["{\"DirectIp\":[\"geoip:ru\",\"10.0.0.0/8\"],\"ProxySites\":[\"geosite:youtube\"],\"GlobalProxy\":true}"]
            }],
            SelectedHappRoutingProfileId = "happ-geo"
        };

        var route = JsonNode.Parse(SingBoxConfigBuilder.Build(state, Vless()))!["route"]!.AsObject();
        var rules = route["rules"]!.AsArray();
        var definitions = route["rule_set"]!.AsArray();

        Assert.Contains(rules, rule => rule?["rule_set"]?.AsArray().Any(tag => tag?.GetValue<string>() == "geoip-ru") == true && rule["outbound"]?.GetValue<string>() == "direct");
        Assert.Contains(rules, rule => rule?["rule_set"]?.AsArray().Any(tag => tag?.GetValue<string>() == "geosite-youtube") == true && rule["outbound"]?.GetValue<string>() == "proxy");
        Assert.DoesNotContain(rules, rule => rule?["ip_cidr"]?.AsArray().Any(ip => ip?.GetValue<string>() == "geoip:ru") == true);
        Assert.Contains(definitions, item => item?["tag"]?.GetValue<string>() == "geoip-ru" &&
            (item["type"]?.GetValue<string>() == "local" || item["download_detour"]?.GetValue<string>() == "direct"));
        Assert.Contains(definitions, item => item?["tag"]?.GetValue<string>() == "geosite-youtube");
        Assert.True(JsonNode.Parse(SingBoxConfigBuilder.Build(state, Vless()))!["experimental"]!["cache_file"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void InvalidHappIpRuleIsRejectedBeforeSingBoxLaunch()
    {
        var state = new AppState { TunEnabled = true,
            HappRoutingProfiles = [new HappRoutingProfile { Id = "bad", Links = ["{\"DirectIp\":[\"not-an-address\"]}"] }],
            SelectedHappRoutingProfileId = "bad" };

        Assert.Throws<FormatException>(() => SingBoxConfigBuilder.Build(state, Vless()));
    }

    [Theory]
    [InlineData("win10")]
    [InlineData("win11")]
    public void HappGeoIpConfigPassesBundledSingBoxCheck(string version)
    {
        var binary = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "core", version, "sing-box.exe"));
        var state = new AppState { TunEnabled = true,
            HappRoutingProfiles = [new HappRoutingProfile { Id = "geo", Links = ["{\"DirectIp\":[\"geoip:ru\"]}"] }],
            SelectedHappRoutingProfileId = "geo" };
        var path = Path.Combine(Path.GetTempPath(), $"ior-geoip-check-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, SingBoxConfigBuilder.Build(state, Vless()));
            using var process = Process.Start(new ProcessStartInfo(binary)
            {
                Arguments = $"check -c \"{path}\"", UseShellExecute = false,
                RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true
            })!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10_000), "sing-box check timed out");
            Assert.True(process.ExitCode == 0, output);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("win10")]
    [InlineData("win11")]
    public async Task BundledGeoIpRuleSetAllowsProxyStartupWithoutRemoteDownload(string version)
    {
        var binary = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "core", version, "sing-box.exe"));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var state = new AppState { TunEnabled = false, MixedProxyEnabled = true, MixedProxyPort = port,
            HappRoutingProfiles = [new HappRoutingProfile { Id = "geo", Links = ["{\"DirectIp\":[\"geoip:ru\"]}"] }],
            SelectedHappRoutingProfileId = "geo" };
        var path = Path.Combine(Path.GetTempPath(), $"ior-geoip-start-{Guid.NewGuid():N}.json");
        Process? process = null;
        try
        {
            File.WriteAllText(path, SingBoxConfigBuilder.Build(state, Vless()));
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(path);
            process = Process.Start(start)!;
            var ready = false;
            for (var attempt = 0; attempt < 40 && !process.HasExited; attempt++)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port);
                    ready = true;
                    break;
                }
                catch (SocketException) { await Task.Delay(100); }
            }
            if (!ready)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                var error = await process.StandardError.ReadToEndAsync();
                Assert.Fail("sing-box did not open mixed proxy with bundled GeoIP: " + error);
            }
        }
        finally
        {
            if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            process?.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void LocalRoutingProfileBuildsBlockProxyDirectRulesAndFinalRoute()
    {
        var state = new AppState
        {
            TunEnabled = true,
            HappRoutingProfiles =
            [
                new HappRoutingProfile
                {
                    Id = "local",
                    Name = "Local",
                    Source = RoutingProfileSource.Ior,
                    GlobalProxy = true,
                    BlockSites = ["ads.example"],
                    ProxySites = ["video.example"],
                    DirectSites = ["bank.example"],
                    DirectIp = ["10.0.0.0/8"]
                }
            ],
            SelectedHappRoutingProfileId = "local"
        };

        var route = JsonNode.Parse(SingBoxConfigBuilder.Build(state, Vless()))!["route"]!.AsObject();
        var rules = route["rules"]!.AsArray().Select(node => node!.AsObject()).ToArray();

        Assert.Equal("proxy", route["final"]!.GetValue<string>());
        Assert.Contains(rules, rule => rule["action"]?.GetValue<string>() == "reject" && rule["domain_suffix"]?.AsArray().Any(value => value!.GetValue<string>() == "ads.example") == true);
        Assert.Contains(rules, rule => rule["outbound"]?.GetValue<string>() == "proxy" && rule["domain_suffix"]?.AsArray().Any(value => value!.GetValue<string>() == "video.example") == true);
        Assert.Contains(rules, rule => rule["outbound"]?.GetValue<string>() == "direct" && rule["ip_cidr"]?.AsArray().Any(value => value!.GetValue<string>() == "10.0.0.0/8") == true);
    }

    [Fact]
    public void DnsStrategyComesFromStateForTcpProfiles()
    {
        var state = new AppState
        {
            TunEnabled = true,
            DnsStrategy = "prefer_ipv6"
        };

        var root = JsonNode.Parse(SingBoxConfigBuilder.Build(state, Vless()))!.AsObject();

        Assert.Equal("prefer_ipv6", root["dns"]!["strategy"]!.GetValue<string>());
        Assert.Equal("prefer_ipv6", root["route"]!["default_domain_resolver"]!["strategy"]!.GetValue<string>());
        Assert.Empty(root["dns"]!["rules"]!.AsArray());
    }

    [Fact]
    public void Hysteria2DoesNotAddHotspotSpecificOptions()
    {
        var state = new AppState { TunEnabled = true };

        var root = JsonNode.Parse(SingBoxConfigBuilder.Build(state, Hysteria2()))!.AsObject();
        var outbound = root["outbounds"]!.AsArray()
            .Select(x => x!.AsObject())
            .Single(x => x["tag"]!.GetValue<string>() == "proxy");

        Assert.Null(outbound["udp_fragment"]);
        Assert.Null(outbound["connect_timeout"]);
    }

    [Fact]
    public void ResolvedDomainUsesDirectIpAndPreservesTlsName()
    {
        var profile = Vless();
        profile.Server = "edge.sslip.io";
        profile.ServerName = null;
        var state = new AppState { TunEnabled = true, DnsStrategy = "prefer_ipv6" };
        var root = JsonNode.Parse(SingBoxConfigBuilder.Build(state, profile, "203.0.113.12"))!.AsObject();
        var outbound = root["outbounds"]![0]!;
        var rules = root["route"]!["rules"]!.AsArray();

        Assert.Equal("203.0.113.12", outbound["server"]!.GetValue<string>());
        Assert.Equal("edge.sslip.io", outbound["tls"]!["server_name"]!.GetValue<string>());
        Assert.Equal("hijack-dns", rules[0]!["action"]!.GetValue<string>());
        Assert.Equal("203.0.113.12/32", rules[1]!["ip_cidr"]![0]!.GetValue<string>());
        Assert.True(rules[2]!["ip_is_private"]!.GetValue<bool>());
        var tun = root["inbounds"]!.AsArray().Single(item => item!["type"]!.GetValue<string>() == "tun")!;
        Assert.Equal("203.0.113.12/32", tun["route_exclude_address"]![0]!.GetValue<string>());
        Assert.Equal("ipv4_only", root["dns"]!["strategy"]!.GetValue<string>());
        Assert.Equal("direct", root["dns"]!["servers"]![0]!["detour"]!.GetValue<string>());
        Assert.Equal("edge.sslip.io", profile.Server);
    }

    [Fact]
    public void TunPinsOutboundToPreflightPhysicalInterface()
    {
        var root = JsonNode.Parse(SingBoxConfigBuilder.Build(
            new AppState { TunEnabled = true }, Vless(), outboundInterface: "Беспроводная сеть"))!.AsObject();
        var route = root["route"]!;

        Assert.False(route["auto_detect_interface"]!.GetValue<bool>());
        Assert.Equal("Беспроводная сеть", route["default_interface"]!.GetValue<string>());
    }

    [Fact]
    public void Hysteria2DomainUsesResolvedAddressWithoutChangingConfiguredSni()
    {
        var profile = Hysteria2();
        profile.Server = "node.sslip.io";
        profile.ServerName = "cover.example";
        var root = JsonNode.Parse(SingBoxConfigBuilder.Build(new AppState { TunEnabled = true }, profile, "198.51.100.9"))!.AsObject();
        var outbound = root["outbounds"]![0]!;

        Assert.Equal("198.51.100.9", outbound["server"]!.GetValue<string>());
        Assert.Equal("cover.example", outbound["tls"]!["server_name"]!.GetValue<string>());
        Assert.Equal("198.51.100.9/32", root["route"]!["rules"]![1]!["ip_cidr"]![0]!.GetValue<string>());
    }

    [Fact]
    public void DnsHijackPrecedesPrivateRouteEvenWithSniffDisabled()
    {
        var state = new AppState { TunEnabled = true, MixedProxyEnabled = false, SniffEnabled = false };
        var rules = JsonNode.Parse(SingBoxConfigBuilder.Build(state, Vless()))!["route"]!["rules"]!.AsArray();

        Assert.Equal("hijack-dns", rules[0]!["action"]!.GetValue<string>());
        Assert.Equal("91.186.217.186/32", rules[1]!["ip_cidr"]![0]!.GetValue<string>());
        Assert.True(rules[2]!["ip_is_private"]!.GetValue<bool>());
        Assert.DoesNotContain(rules, rule => rule!["action"]?.GetValue<string>() == "sniff");
    }

    [Theory]
    [InlineData("win10")]
    [InlineData("win11")]
    public void TunConfigWithBootstrapIpPassesBundledSingBoxCheck(string version)
    {
        var binary = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "core", version, "sing-box.exe"));
        Assert.True(File.Exists(binary), $"Missing bundled {version} sing-box: {binary}");
        var profile = Hysteria2();
        profile.Server = "node.sslip.io";
        var path = Path.Combine(Path.GetTempPath(), $"ior-config-check-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, SingBoxConfigBuilder.Build(new AppState { TunEnabled = true }, profile, "198.51.100.9"));
            using var process = Process.Start(new ProcessStartInfo(binary)
            {
                Arguments = $"check -c \"{path}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            })!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10_000), "sing-box check timed out");
            Assert.True(process.ExitCode == 0, output);
        }
        finally { File.Delete(path); }
    }

    private static JsonObject BuildTun(AppState state)
    {
        var root = JsonNode.Parse(SingBoxConfigBuilder.Build(state, Vless()))!.AsObject();
        return root["inbounds"]!.AsArray()
            .Select(x => x!.AsObject())
            .Single(x => x["type"]!.GetValue<string>() == "tun");
    }

    private static ProxyProfile Vless() => new()
    {
        Name = "Server",
        Protocol = ProxyProtocol.Vless,
        Server = "91.186.217.186",
        Port = 443,
        UserId = "11111111-1111-1111-1111-111111111111",
        TlsEnabled = true,
        Network = "tcp",
        ServerName = "example.com"
    };

    private static ProxyProfile Hysteria2() => new()
    {
        Name = "Server",
        Protocol = ProxyProtocol.Hysteria2,
        Server = "91.186.217.186",
        Port = 443,
        Password = "password",
        ServerName = "example.com"
    };
}
