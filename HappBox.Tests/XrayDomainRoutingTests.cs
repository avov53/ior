using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class XrayDomainRoutingTests
{
    private static ProxyProfile Profile() => new() { Protocol = ProxyProtocol.Vless, Server = "203.0.113.10", Port = 443,
        UserId = "11111111-1111-1111-1111-111111111111", TlsEnabled = true, RealityEnabled = true,
        ServerName = "reference.example", PublicKey = "test-key" };

    [Fact]
    public void TunSniffsDomainsOnlyForRoutingAndKeepsRuleOrder()
    {
        var route = new HappRoutingProfile { Id = "ru", Source = RoutingProfileSource.Happ, Links =
            ["""{"RouteOrder":"direct-block-proxy","GlobalProxy":true,"DirectSites":["domain:ru","domain:su","domain:xn--p1ai"]}"""] };
        var state = new AppState { TunEnabled = true, SniffEnabled = true,
            HappRoutingProfiles = [route], SelectedHappRoutingProfileId = route.Id };
        var json = JsonNode.Parse(XrayConfigBuilder.BuildTunCandidateForValidation(state, Profile()))!;
        foreach (var inbound in json["inbounds"]!.AsArray())
        {
            Assert.True(inbound!["sniffing"]!["enabled"]!.GetValue<bool>());
            Assert.True(inbound["sniffing"]!["routeOnly"]!.GetValue<bool>());
            Assert.False(inbound["sniffing"]!["metadataOnly"]!.GetValue<bool>());
            Assert.Contains("quic", inbound["sniffing"]!["destOverride"]!.AsArray().Select(item => item!.GetValue<string>()));
        }
        var rules = json["routing"]!["rules"]!.AsArray();
        Assert.Equal("domain:ru", rules[2]!["domain"]![0]!.GetValue<string>());
        Assert.Equal("direct", rules[2]!["outboundTag"]!.GetValue<string>());
        Assert.Equal("proxy", rules[^1]!["outboundTag"]!.GetValue<string>());
    }

    [Fact]
    public void ExplicitSniffingOptOutRemainsRespected()
    {
        var json = JsonNode.Parse(XrayConfigBuilder.Build(new AppState { TunEnabled = false, SniffEnabled = false }, Profile()))!;
        Assert.All(json["inbounds"]!.AsArray(), inbound => Assert.Null(inbound!["sniffing"]));
    }

    [Theory]
    [InlineData("reference.ru", true)]
    [InlineData("reference.su", true)]
    [InlineData("reference.xn--p1ai", true)]
    [InlineData("reference.com", false)]
    public async Task InstalledXrayRoutesByHttpHostWithoutReplacingForeignDestinationIp(string host, bool expectedDirect)
    {
        var binary = Environment.GetEnvironmentVariable("IOR_XRAY_TEST_BINARY");
        if (string.IsNullOrWhiteSpace(binary)) return;
        var direct = new TcpListener(IPAddress.Loopback, 0); direct.Start();
        var portPicker = new TcpListener(IPAddress.Loopback, 0); portPicker.Start();
        var inboundPort = ((IPEndPoint)portPicker.LocalEndpoint).Port; portPicker.Stop();
        var route = new HappRoutingProfile { Id = "ru", Source = RoutingProfileSource.Ior,
            DirectSites = ["ru", "su", "xn--p1ai"], GlobalProxy = true };
        var state = new AppState { TunEnabled = false, MixedProxyPort = inboundPort, LogLevel = "debug",
            HappRoutingProfiles = [route], SelectedHappRoutingProfileId = route.Id };
        var root = JsonNode.Parse(XrayConfigBuilder.Build(state, Profile()))!;
        // This scenario exercises only SOCKS. Reuse the selected free port
        // rather than assuming the adjacent port is also free.
        root["inbounds"]!.AsArray().RemoveAt(0);
        root["inbounds"]![0]!["port"] = inboundPort;
        root["outbounds"]![0] = new JsonObject { ["tag"] = "proxy", ["protocol"] = "blackhole" };
        root["outbounds"]![1] = new JsonObject { ["tag"] = "direct", ["protocol"] = "socks",
            ["settings"] = new JsonObject { ["servers"] = new JsonArray { new JsonObject
                { ["address"] = "127.0.0.1", ["port"] = ((IPEndPoint)direct.LocalEndpoint).Port } } } };
        var config = Path.Combine(Path.GetTempPath(), "ior-route-sniff-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(config, root.ToJsonString());
        var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(config);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await Task.Delay(500, timeout.Token);
            Assert.False(process.HasExited, process.HasExited ? $"{await stdout} {await stderr}" : "");
            var destinationTask = expectedDirect ? Task.Run(async () =>
            {
                using var outgoing = await direct.AcceptTcpClientAsync(timeout.Token);
                var stream = outgoing.GetStream();
                var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, timeout.Token);
                var methods = new byte[greeting[1]]; await stream.ReadExactlyAsync(methods, timeout.Token);
                await stream.WriteAsync(new byte[] { 5, 0 }, timeout.Token);
                var connect = new byte[10]; await stream.ReadExactlyAsync(connect, timeout.Token);
                Assert.Equal(1, connect[3]);
                Assert.Equal(new byte[] { 203, 0, 113, 80 }, connect[4..8]);
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, timeout.Token);
                var request = new byte[1024]; var count = await stream.ReadAsync(request, timeout.Token);
                Assert.Contains($"Host: {host}", Encoding.ASCII.GetString(request, 0, count));
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\ndirect"), timeout.Token);
            }) : Task.CompletedTask;
            using var client = new TcpClient();
            for (var attempt = 0; ; attempt++)
            {
                try { await client.ConnectAsync(IPAddress.Loopback, inboundPort, timeout.Token); break; }
                catch (SocketException) when (attempt < 30) { await Task.Delay(50, timeout.Token); }
            }
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
            var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, timeout.Token);
            await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 203, 0, 113, 80, 0, 80 }, timeout.Token);
            var reply = new byte[10]; await stream.ReadExactlyAsync(reply, timeout.Token);
            if (reply[1] != 0)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                Assert.Fail($"SOCKS reply {reply[1]}: {await stdout} {await stderr}");
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n"), timeout.Token);
            var buffer = new byte[512]; var count = await stream.ReadAsync(buffer, timeout.Token);
            if (expectedDirect) Assert.Contains("direct", Encoding.ASCII.GetString(buffer, 0, count));
            else { Assert.Equal(0, count); Assert.False(direct.Pending()); }
            await destinationTask;
        }
        finally
        {
            timeout.Cancel(); direct.Stop();
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await stdout; await stderr;
            File.Delete(config);
        }
    }
}
