using System.Net;
using System.Net.Sockets;
using System.Text;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class DirectEgressHelperTests
{
    private static async Task<TcpClient> Authenticate(DirectEgressHelper helper, string? password = null)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, helper.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 2 });
        var reply = new byte[2]; await stream.ReadExactlyAsync(reply);
        Assert.Equal(new byte[] { 5, 2 }, reply);
        var user = Encoding.ASCII.GetBytes(helper.User);
        var secret = Encoding.ASCII.GetBytes(password ?? helper.Password);
        await stream.WriteAsync(new byte[] { 1, (byte)user.Length }.Concat(user).Concat(new byte[] { (byte)secret.Length }).Concat(secret).ToArray());
        await stream.ReadExactlyAsync(reply);
        Assert.Equal(password is null ? (byte)0 : (byte)1, reply[1]);
        return client;
    }

    private static byte[] Request(byte command, int port) => [5, command, 0, 1, 127, 0, 0, 1, (byte)(port >> 8), (byte)port];

    [Fact]
    public async Task AuthenticatedTcpRelayUsesBoundSocketAndPassesPayload()
    {
        var count = 0;
        await using var helper = new DirectEgressHelper(socket => { Interlocked.Increment(ref count); socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); }, "1.1.1.1");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            using var local = await Authenticate(helper);
            var stream = local.GetStream();
            await stream.WriteAsync(Request(1, ((IPEndPoint)listener.LocalEndpoint).Port));
            using var destination = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(helper.IsOwnTcpFlow((IPEndPoint)destination.Client.RemoteEndPoint!, (IPEndPoint)listener.LocalEndpoint));
            Assert.False(helper.IsOwnTcpFlow(new IPEndPoint(IPAddress.Loopback, 1), (IPEndPoint)listener.LocalEndpoint));
            var reply = new byte[10]; await stream.ReadExactlyAsync(reply);
            Assert.Equal(0, reply[1]);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("request"));
            var payload = new byte[7]; await destination.GetStream().ReadExactlyAsync(payload);
            Assert.Equal("request", Encoding.ASCII.GetString(payload));
            await destination.GetStream().WriteAsync(Encoding.ASCII.GetBytes("reply"));
            payload = new byte[5]; await stream.ReadExactlyAsync(payload);
            Assert.Equal("reply", Encoding.ASCII.GetString(payload));
            Assert.Equal(1, count);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task UdpAssociationRelaysThroughBoundConnectedSocket()
    {
        var count = 0;
        await using var helper = new DirectEgressHelper(socket => { Interlocked.Increment(ref count); socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); }, "1.1.1.1");
        using var remote = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var client = await Authenticate(helper);
        var stream = client.GetStream();
        await stream.WriteAsync(Request(3, 0));
        var reply = new byte[10]; await stream.ReadExactlyAsync(reply);
        using var local = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = new IPEndPoint(IPAddress.Loopback, (reply[8] << 8) | reply[9]);
        var header = Request(0, ((IPEndPoint)remote.Client.LocalEndPoint!).Port); header[0] = 0;
        var datagram = header.Concat(Encoding.ASCII.GetBytes("udp-echo")).ToArray();
        await local.SendAsync(datagram, endpoint);
        var outbound = await remote.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("udp-echo", Encoding.ASCII.GetString(outbound.Buffer));
        await remote.SendAsync(outbound.Buffer, outbound.RemoteEndPoint);
        var returned = await local.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("udp-echo", Encoding.ASCII.GetString(returned.Buffer[10..]));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task IncorrectAuthenticationNeverOpensPhysicalSocket()
    {
        var count = 0;
        await using var helper = new DirectEgressHelper(socket => Interlocked.Increment(ref count), "1.1.1.1");
        using var client = await Authenticate(helper, "invalid");
        Assert.Equal(0, count);
    }

    [Theory]
    [InlineData("ior")]
    [InlineData("ior-xray")]
    [InlineData("IOR-core")]
    public void OwnTunAdaptersAreNeverConsideredPhysical(string name) => Assert.True(PhysicalInterfaceResolver.IsIorInterface(name));
}
