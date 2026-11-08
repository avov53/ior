using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace HappBox;

/// <summary>Bounded, read-only incident inspection. Never repairs or restarts a connection.</summary>
public sealed class InternetIncident
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<object> _timeline = new();
    public bool Busy { get; private set; }
    public void Observe(string stage, long durationMs = 0, int count = 0, long queueDelayMs = 0)
    {
        lock (_timeline)
        {
            _timeline.Enqueue(new { utc = DateTime.UtcNow, stage, durationMs, count, queueDelayMs });
            while (_timeline.Count > 256) _timeline.Dequeue();
        }
    }
    public object[] Timeline() { lock (_timeline) return _timeline.ToArray(); }
    public sealed record Probe(string Stage, string Status, long DurationMs, string? Detail = null);
    public static async Task<Probe> MeasureAsync(string stage, Func<CancellationToken, Task<string?>> probe,
        CancellationToken token, int budgetMs = 6000)
    {
        var watch = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(budgetMs);
        try { var detail = await probe(limit.Token); return new(stage, detail?.StartsWith("notProbed:") == true ? "notProbed" : "ok", watch.ElapsedMilliseconds, detail); }
        catch (OperationCanceledException) { return new(stage, token.IsCancellationRequested ? "cancelled" : "timeout", watch.ElapsedMilliseconds); }
        catch (PlatformNotSupportedException) { return new(stage, "unavailable", watch.ElapsedMilliseconds, "PlatformNotSupportedException"); }
        catch (HttpRequestException e) when (e.HttpRequestError == HttpRequestError.VersionNegotiationError)
        { return new(stage, "unavailable", watch.ElapsedMilliseconds, "VersionNegotiationError"); }
        catch (Exception e)
        {
            var detail = e.GetType().Name + ";hresult=" + e.HResult.ToString("X8");
            if (e is HttpRequestException http) detail += ";httpError=" + http.HttpRequestError;
            if (e is SocketException socket) detail += ";socketError=" + socket.SocketErrorCode;
            if (e.InnerException is { } inner) detail += ";inner=" + inner.GetType().Name + ";innerHresult=" + inner.HResult.ToString("X8");
            return new(stage, "failed", watch.ElapsedMilliseconds, detail);
        }
    }
    public async Task<string?> CaptureAsync(string directory, Func<CancellationToken, Task<object>> collect, CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token)) return null;
        Busy = true;
        var id = Guid.NewGuid().ToString("N");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var markedLocal = DateTimeOffset.Now;
            var markedUtc = markedLocal.UtcDateTime;
            var data = await collect(timeout.Token);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, id + ".json");
            PrivateRuntimeFile.Write(path, JsonSerializer.Serialize(new { schema = 1, id, markedUtc, markedLocal, completedUtc = DateTime.UtcNow, data, timeline = Timeline() }));
            return id;
        }
        finally { Busy = false; _gate.Release(); }
    }
    public static async Task<string> ReadNetworkAsync(CancellationToken token)
    {
        // Fixed script; no profile data or user input can become executable commands.
        const string script = " [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $ErrorActionPreference='Stop'; $r=@{}; foreach($k in @('routes','dnsPolicies','dnsRules','adapterStatistics')){try{$r[$k]=switch($k){'routes'{@(Get-NetRoute -ErrorAction Stop | Select-Object InterfaceIndex,DestinationPrefix,NextHop,RouteMetric,State)}'dnsPolicies'{@(Get-DnsClientNrptPolicy -Effective -ErrorAction Stop | Select-Object Namespace,NameServers)}'dnsRules'{@(Get-DnsClientNrptRule -ErrorAction Stop | Select-Object Name,DisplayName,Namespace,NameServers)}'adapterStatistics'{@(Get-NetAdapterStatistics -ErrorAction Stop | Select-Object Name,ReceivedBytes,SentBytes,ReceivedDiscardedPackets,OutboundDiscardedPackets,ReceivedPacketErrors,OutboundPacketErrors)}}}catch{$r[$k]=@{errorType=$_.Exception.GetType().Name}}};$r|ConvertTo-Json -Depth 6 -Compress";
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("INCIDENT_INSPECT_START_FAILED");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            var text = await stdout; await stderr;
            if (process.ExitCode != 0) throw new IOException("INCIDENT_INSPECT_FAILED");
            if (text.Length > 512_000) throw new IOException("INCIDENT_INSPECT_TOO_LARGE");
            return text;
        }
        finally { if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } } }
    }
    public static async Task<string?> DnsAsync(IPAddress resolver, string host, CancellationToken token, int port = 53)
    {
        using var udp = new UdpClient(resolver.AddressFamily);
        udp.Connect(resolver, port);
        var query = BootstrapDnsResolver.BuildQuery(host);
        await udp.SendAsync(query, token);
        var reply = await udp.ReceiveAsync(token);
        var answer = BootstrapDnsResolver.ReadFirstAddress(reply.Buffer, query, 1);
        return answer.AddressFamily.ToString();
    }
    public static object[] Interfaces() => NetworkInterface.GetAllNetworkInterfaces().Select(n =>
    {
        try { return (object)new { n.Id, n.Name, state = n.OperationalStatus.ToString(), type = n.NetworkInterfaceType.ToString(), dns = n.GetIPProperties().DnsAddresses.Select(a => a.ToString()).ToArray() }; }
        catch (NetworkInformationException) { return new { n.Id, state = "changedDuringInspection" }; }
    }).ToArray();
    public static async Task<string?> HttpsAsync(bool http3, string adapter, CancellationToken token)
    {
        const string host = "www.youtube.com";
        var addresses = await Dns.GetHostAddressesAsync(host, token);
        if (!addresses.Any(address => PhysicalInterfaceResolver.UsesInterfaceForAddress(address, adapter)))
            return "notProbed:destinationNotRoutedThroughOwnedTun";
        // Restrict DNS for this diagnostic request to addresses whose route was verified.
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        if (!http3) handler.ConnectCallback = async (context, cancellation) =>
        {
            var allowed = addresses.Where(address => PhysicalInterfaceResolver.UsesInterfaceForAddress(address, adapter)).ToArray();
            if (allowed.Length == 0) throw new IOException("INCIDENT_ROUTE_CHANGED");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellation); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        };
        // HTTP/3 may resolve again inside MsQuic; only probe when every returned address is routed through TUN.
        if (http3 && addresses.Any(address => !PhysicalInterfaceResolver.UsesInterfaceForAddress(address, adapter)))
            return "notProbed:mixedRoutes";
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/generate_204")
        { Version = http3 ? HttpVersion.Version30 : HttpVersion.Version20, VersionPolicy = http3 ? HttpVersionPolicy.RequestVersionExact : HttpVersionPolicy.RequestVersionOrLower };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        return "HTTP_" + (int)response.StatusCode + ";version=" + response.Version;
    }
}
