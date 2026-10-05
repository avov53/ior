using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HappBox;

/// <summary>Read-only, allowlisted network evidence. Does not include profiles, credentials or registry contents.</summary>
public sealed record NetworkDiagnosticSnapshot(DateTimeOffset Utc, string Phase,
    NetworkDiagnosticSnapshot.Route[] Routes, NetworkDiagnosticSnapshot.Dns[] DnsServers,
    NetworkDiagnosticSnapshot.InterfaceState[]? Interfaces = null)
{
    public sealed record Route(string DestinationPrefix, string NextHop, int InterfaceIndex, int RouteMetric);
    public sealed record Dns(int InterfaceIndex, string[] ServerAddresses);
    public sealed record InterfaceState(int InterfaceIndex, string Forwarding, string WeakHostSend, string WeakHostReceive);
    private sealed record Payload(Route[] Routes, Dns[] DnsServers, InterfaceState[]? Interfaces = null);

    public static async Task<NetworkDiagnosticSnapshot> CaptureAsync(string phase, CancellationToken token = default)
    {
        var script =
            "@{ Routes=@(Get-NetRoute -AddressFamily IPv4 | Select-Object DestinationPrefix,NextHop,InterfaceIndex,RouteMetric); " +
            "DnsServers=@(Get-DnsClientServerAddress -AddressFamily IPv4 | Select-Object InterfaceIndex,ServerAddresses); " +
            "Interfaces=@(Get-NetIPInterface -AddressFamily IPv4 | Select-Object InterfaceIndex,@{n='Forwarding';e={$_.Forwarding.ToString()}},@{n='WeakHostSend';e={$_.WeakHostSend.ToString()}},@{n='WeakHostReceive';e={$_.WeakHostReceive.ToString()}}) } | ConvertTo-Json -Depth 5 -Compress";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var output=await WindowsNetworkCommand.RunAsync(script,timeout.Token,WindowsNetworkOperation.Snapshot);
        if (output.Length > 512_000) throw new IOException("Network snapshot failed or exceeds limit.");
        var data = JsonSerializer.Deserialize<Payload>(output, new JsonSerializerOptions { PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString }) ?? throw new IOException("Empty network snapshot.");
        return new NetworkDiagnosticSnapshot(DateTimeOffset.UtcNow, phase, data.Routes ?? [], data.DnsServers ?? [], data.Interfaces ?? []);
    }
}
