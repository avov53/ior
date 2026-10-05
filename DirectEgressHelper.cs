using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace HappBox;

/// <summary>Authenticated local SOCKS5 egress. Outbound TCP, UDP and bootstrap sockets bind to a physical IPv4 interface.</summary>
public sealed class DirectEgressHelper : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, Task> _clients = new();
    private readonly ConcurrentDictionary<string, byte> _outgoingTcp = new();
    private readonly SemaphoreSlim _capacity = new(256);
    private readonly Action<Socket> _bind;
    private readonly string _bootstrap;
    private readonly ConnectionDiagnostics? _diagnostics;
    private readonly Task _accept;
    private int _sequence;
    private long _tcpConnections;
    private long _udpDatagrams;
    public long TcpConnections => Interlocked.Read(ref _tcpConnections);
    public long UdpDatagrams => Interlocked.Read(ref _udpDatagrams);
    public string User { get; } = "ior-direct";
    public string Password { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static DirectEgressHelper ForPhysicalInterface(string name, string bootstrap, ConnectionDiagnostics? diagnostics = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (PhysicalInterfaceResolver.IsIorInterface(name)) throw new InvalidOperationException("DIRECT cannot bind to ior TUN.");
        var network = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(item => item.Name == name &&
            item.OperationalStatus == OperationalStatus.Up && item.NetworkInterfaceType is not (NetworkInterfaceType.Tunnel or NetworkInterfaceType.Loopback))
            ?? throw new InvalidOperationException("DIRECT physical interface unavailable.");
        var properties = network.GetIPProperties();
        var index = properties.GetIPv4Properties()?.Index ?? throw new InvalidOperationException("DIRECT requires IPv4.");
        var address = properties.UnicastAddresses.Select(item => item.Address).FirstOrDefault(item =>
            item.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item) && !item.ToString().StartsWith("169.254."))
            ?? throw new InvalidOperationException("DIRECT physical IPv4 address unavailable.");
        return new DirectEgressHelper(socket =>
        {
            // IP_UNICAST_IF (31) takes the interface index in network byte order on Windows.
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(index));
            socket.Bind(new IPEndPoint(address, 0));
            // Windows getsockopt returns the index in host order, unlike setsockopt.
            if ((int)socket.GetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31)! != index)
                throw new InvalidOperationException("DIRECT socket lost its physical interface binding.");
        }, bootstrap, diagnostics);
    }

    // Exposed for deterministic loopback reference-server tests; application uses ForPhysicalInterface only.
    public DirectEgressHelper(Action<Socket> bind, string bootstrap, ConnectionDiagnostics? diagnostics = null)
    {
        _bind = bind; _bootstrap = bootstrap; _diagnostics = diagnostics;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (!_capacity.Wait(0)) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref _sequence);
                var task = HandleAsync(client);
                _clients[id] = task;
                _ = task.ContinueWith(completed => { _clients.TryRemove(id, out _); _capacity.Release(); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var session = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            try
            {
                session.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = client.GetStream();
                client.NoDelay = true;
                var greeting = await ReadAsync(stream, 2, session.Token);
                if (greeting[0] != 5 || greeting[1] == 0) return;
                var methods = await ReadAsync(stream, greeting[1], session.Token);
                if (!methods.Contains((byte)2)) { await stream.WriteAsync(new byte[] { 5, 255 }, session.Token); return; }
                await stream.WriteAsync(new byte[] { 5, 2 }, session.Token);
                var auth = await ReadAsync(stream, 2, session.Token);
                if (auth[0] != 1) return;
                var user = await ReadAsync(stream, auth[1], session.Token);
                var passwordLength = (await ReadAsync(stream, 1, session.Token))[0];
                var password = await ReadAsync(stream, passwordLength, session.Token);
                var valid = CryptographicOperations.FixedTimeEquals(user, Encoding.ASCII.GetBytes(User)) &&
                    CryptographicOperations.FixedTimeEquals(password, Encoding.ASCII.GetBytes(Password));
                await stream.WriteAsync(new byte[] { 1, valid ? (byte)0 : (byte)1 }, session.Token);
                if (!valid) return;
                var request = await ReadAsync(stream, 4, session.Token);
                if (request[0] != 5 || request[2] != 0) return;
                var endpoint = await ReadEndpointAsync(stream, request[3], session.Token);
                if (request[1] == 1)
                {
                    using var outgoing = await OpenTcpAsync(endpoint, session.Token, trackFlow: true);
                    try
                    {
                        Interlocked.Increment(ref _tcpConnections);
                        _diagnostics?.Record("direct", "DIRECT_TCP_CONNECTED", ("destination", endpoint.ToString()), ("target", "DIRECT"));
                        await stream.WriteAsync(Reply((IPEndPoint)outgoing.LocalEndPoint!), session.Token);
                        session.CancelAfter(Timeout.InfiniteTimeSpan);
                        using var remote = new NetworkStream(outgoing, ownsSocket: false);
                        async Task Pump(Stream from, Stream to, Socket shutdown)
                        {
                            await from.CopyToAsync(to, session.Token);
                            shutdown.Shutdown(SocketShutdown.Send);
                        }
                        var upload = Pump(stream, remote, outgoing);
                        var download = Pump(remote, stream, client.Client);
                        var first = await Task.WhenAny(upload, download);
                        if (first.IsFaulted || first.IsCanceled) session.Cancel();
                        await Task.WhenAll(upload, download);
                    }
                    finally { ForgetTcp(outgoing, endpoint); }
                }
                else if (request[1] == 3)
                {
                    // A UDP association is scoped to its authenticated control connection.
                    using var relay = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    await stream.WriteAsync(Reply((IPEndPoint)relay.Client.LocalEndPoint!), session.Token);
                    session.CancelAfter(Timeout.InfiniteTimeSpan);
                    var udpTask = RelayUdpAsync(relay, endpoint, session.Token);
                    var controlTask = ReadAsync(stream, 1, session.Token);
                    await Task.WhenAny(udpTask, controlTask);
                    session.Cancel();
                    try { await udpTask; } catch (OperationCanceledException) { }
                    try { await controlTask; } catch (OperationCanceledException) { } catch (EndOfStreamException) { }
                }
                else await stream.WriteAsync(new byte[] { 5, 7, 0, 1, 0, 0, 0, 0, 0, 0 }, session.Token);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ArgumentException or InvalidOperationException)
            {
                _diagnostics?.Record("direct", "DIRECT_FLOW_FAILED", ("errorType", error.GetType().Name),
                    ("socketError", error is SocketException socket ? socket.SocketErrorCode.ToString() : "none"));
            }
        }
    }

    public async Task<Socket> OpenTcpAsync(IPEndPoint endpoint, CancellationToken token, bool trackFlow = false)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        string? flow = null;
        try
        {
            _bind(socket); socket.NoDelay = true;
            if (trackFlow && socket.LocalEndPoint is IPEndPoint local)
            {
                flow = TcpFlowKey(local, endpoint);
                _outgoingTcp[flow] = 0;
            }
            await socket.ConnectAsync(endpoint, token);
            return socket;
        }
        catch { if (flow is not null) _outgoingTcp.TryRemove(flow, out _); socket.Dispose(); throw; }
    }

    private static string TcpFlowKey(IPEndPoint source, IPEndPoint destination) => $"{source}>{destination}";

    // A matching socket tuple, rather than connection volume, proves that our own
    // physical egress was captured by TUN. Closed sockets are pruned below.
    public bool IsOwnTcpFlow(IPEndPoint source, IPEndPoint destination) =>
        _outgoingTcp.ContainsKey(TcpFlowKey(source, destination));

    private void ForgetTcp(Socket socket, IPEndPoint destination)
    {
        if (socket.LocalEndPoint is IPEndPoint source) _outgoingTcp.TryRemove(TcpFlowKey(source, destination), out _);
    }

    public async Task VerifyPhysicalEgressAsync(string phase, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var destination = await BootstrapDnsResolver.ResolveIpv4Async("www.cloudflare.com", _bootstrap, timeout.Token, _bind);
        using var socket = await OpenTcpAsync(new IPEndPoint(destination, 443), timeout.Token);
        using var stream = new System.Net.Security.SslStream(new NetworkStream(socket, ownsSocket: false));
        await stream.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
            { TargetHost = "www.cloudflare.com" }, timeout.Token);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HEAD / HTTP/1.1\r\nHost: www.cloudflare.com\r\nConnection: close\r\n\r\n"), timeout.Token);
        var response = new byte[12];
        await stream.ReadExactlyAsync(response, timeout.Token);
        if (!Encoding.ASCII.GetString(response).StartsWith("HTTP/1.")) throw new IOException("DIRECT HTTPS probe rejected.");
        _diagnostics?.Record("direct", "PHYSICAL_TCP_TLS_OK", ("phase", phase), ("destination", destination.ToString()));
        // A real, matched DNS datagram verifies UDP (not just a bound UDP port).
        await BootstrapDnsResolver.ResolveIpv4Async("example.com", _bootstrap, timeout.Token, _bind);
        _diagnostics?.Record("direct", "PHYSICAL_UDP_DNS_OK", ("phase", phase));
    }

    private async Task RelayUdpAsync(UdpClient relay, IPEndPoint requested, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        IPEndPoint? client = requested.Port > 0 && IPAddress.IsLoopback(requested.Address) ? requested : null;
        var flows = new Dictionary<IPEndPoint, UdpClient>();
        var readers = new List<Task>();
        try
        {
            while (true)
            {
                var packet = await relay.ReceiveAsync(lifetime.Token);
                if (!IPAddress.IsLoopback(packet.RemoteEndPoint.Address)) continue;
                client ??= packet.RemoteEndPoint;
                if (!client.Equals(packet.RemoteEndPoint) || packet.Buffer.Length < 10 ||
                    packet.Buffer[0] != 0 || packet.Buffer[1] != 0 || packet.Buffer[2] != 0) continue;
                using var bytes = new MemoryStream(packet.Buffer, writable: false);
                bytes.Position = 4;
                var destination = await ReadEndpointAsync(bytes, packet.Buffer[3], lifetime.Token);
                if (!flows.TryGetValue(destination, out var outbound))
                {
                    if (flows.Count >= 64) continue;
                    outbound = new UdpClient(AddressFamily.InterNetwork);
                    try { _bind(outbound.Client); outbound.Connect(destination); }
                    catch { outbound.Dispose(); throw; }
                    flows.Add(destination, outbound);
                    _diagnostics?.Record("direct", "DIRECT_UDP_FLOW_OPENED", ("destination", destination.ToString()), ("target", "DIRECT"));
                    var source = outbound;
                    var recipient = client;
                    readers.Add(Task.Run(async () =>
                    {
                        while (true)
                        {
                            var reply = await source.ReceiveAsync(lifetime.Token);
                            var header = Reply(destination); header[0] = 0; header[1] = 0;
                            var response = new byte[header.Length + reply.Buffer.Length];
                            header.CopyTo(response, 0); reply.Buffer.CopyTo(response, header.Length);
                            await relay.SendAsync(response, recipient, lifetime.Token);
                        }
                    }, lifetime.Token));
                }
                await outbound.SendAsync(packet.Buffer.AsMemory((int)bytes.Position), lifetime.Token);
                Interlocked.Increment(ref _udpDatagrams);
            }
        }
        finally
        {
            lifetime.Cancel();
            foreach (var flow in flows.Values) flow.Dispose();
            try { await Task.WhenAll(readers); } catch (OperationCanceledException) { } catch (SocketException) { } catch (ObjectDisposedException) { }
        }
    }

    private async Task<IPEndPoint> ReadEndpointAsync(Stream stream, byte type, CancellationToken token)
    {
        IPAddress address;
        if (type == 1) address = new IPAddress(await ReadAsync(stream, 4, token));
        else if (type == 3)
        {
            var count = (await ReadAsync(stream, 1, token))[0];
            var domain = Encoding.ASCII.GetString(await ReadAsync(stream, count, token));
            address = await BootstrapDnsResolver.ResolveIpv4Async(domain, _bootstrap, token, _bind);
        }
        else throw new InvalidDataException("DIRECT IPv6 is not enabled; no unbound fallback.");
        var port = await ReadAsync(stream, 2, token);
        return new IPEndPoint(address, (port[0] << 8) | port[1]);
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int count, CancellationToken token)
    {
        var buffer = new byte[count]; await stream.ReadExactlyAsync(buffer, token); return buffer;
    }

    private static byte[] Reply(IPEndPoint endpoint)
    {
        var bytes = new byte[10]; bytes[0] = 5; bytes[3] = 1;
        endpoint.Address.GetAddressBytes().CopyTo(bytes, 4);
        bytes[8] = (byte)(endpoint.Port >> 8); bytes[9] = (byte)endpoint.Port;
        return bytes;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        await _accept;
        try { await Task.WhenAll(_clients.Values).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { }
        _stop.Dispose();
    }
}
