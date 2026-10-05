using System.Net;
using System.Net.Sockets;

namespace HappBox;

public static class NativeProxyHealthProbe
{
    public static async Task CheckUdpAsync(int port, CancellationToken token = default, CoreIngressAuth? auth = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var control = new TcpClient();
        await control.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var stream = control.GetStream();
        var method = (byte)(auth is null ? 0 : 2);
        await stream.WriteAsync(new byte[] { 5, 1, method }, timeout.Token);
        var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, timeout.Token);
        if (!greeting.SequenceEqual(new byte[] { 5, method })) throw new IOException("NATIVE_SOCKS_AUTH_FAILED");
        if (auth is not null)
        {
            var username=System.Text.Encoding.UTF8.GetBytes(auth.Username);var password=System.Text.Encoding.UTF8.GetBytes(auth.Password);
            if(username.Length is <1 or >255 || password.Length is <1 or >255)throw new IOException("NATIVE_SOCKS_AUTH_INVALID");
            await stream.WriteAsync(new byte[]{1,(byte)username.Length}.Concat(username).Concat(new byte[]{(byte)password.Length}).Concat(password).ToArray(),timeout.Token);
            await stream.ReadExactlyAsync(greeting,timeout.Token);
            if(!greeting.SequenceEqual(new byte[]{1,0}))throw new IOException("NATIVE_SOCKS_AUTH_REJECTED");
        }
        await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, timeout.Token);
        var reply = new byte[10]; await stream.ReadExactlyAsync(reply, timeout.Token);
        if (reply[1] != 0 || reply[3] != 1) throw new IOException("NATIVE_UDP_ASSOCIATION_FAILED");
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(new IPAddress(reply[4..8]), (reply[8] << 8) | reply[9]);
        var id = System.Security.Cryptography.RandomNumberGenerator.GetBytes(2);
        byte[] query = [id[0], id[1], 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 7,
            (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
            3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 1, 0, 1];
        byte[] header = [0, 0, 0, 1, 8, 8, 8, 8, 0, 53];
        await udp.SendAsync(header.Concat(query).ToArray(), timeout.Token);
        var answer = await udp.ReceiveAsync(timeout.Token);
        if (answer.Buffer.Length < 22 || !answer.Buffer[..10].SequenceEqual(header) ||
            answer.Buffer[10] != id[0] || answer.Buffer[11] != id[1] || (answer.Buffer[12] & 0x80) == 0)
            throw new IOException("NATIVE_UDP_DNS_RESPONSE_INVALID");
    }
}
