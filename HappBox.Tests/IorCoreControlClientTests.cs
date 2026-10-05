using System.Net;
using System.Net.Sockets;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class IorCoreControlClientTests
{
    [Fact]
    public async Task NativeProcessUsesPrivateControlAndStopsItsListener()
    {
        var binary = Environment.GetEnvironmentVariable("IOR_NATIVE_TEST_BINARY");
        if (string.IsNullOrWhiteSpace(binary)) return;
        Assert.True(File.Exists(binary));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var state = new AppState { TunEnabled = false };
        var profile = new ProxyProfile { Protocol = ProxyProtocol.Vless, Server = "203.0.113.1", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111", ServerName = "example.com", TlsEnabled = true };
        Assert.True(CoreRuntimeContract.TryCreateStart(state, profile, out var request, out var reason), reason);
        var client = await IorCoreControlClient.StartAsync(binary, port, request!);
        try
        {
            var status = await client.StatusAsync();
            Assert.Equal("status", status.Operation);
            Assert.True(status.Tcp); Assert.False(status.Udp); Assert.False(status.Tun);
            Assert.DoesNotContain(profile.UserId, System.Text.Json.JsonSerializer.Serialize(status));
        }
        finally { await client.DisposeAsync(); }
        var probe = new TcpListener(IPAddress.Loopback, port);
        probe.Start(); probe.Stop(); // stop releases the port rather than leaving an orphaned core.
    }
}
