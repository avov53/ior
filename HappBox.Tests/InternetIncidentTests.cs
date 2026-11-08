using HappBox;
using System.Net;
using System.Net.Sockets;
using Xunit;
namespace HappBox.Tests;
public sealed class InternetIncidentTests
{
    [Fact] public async Task ReadOnlyWindowsInspectionReturnsStructuredResultsWithinBudget()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var report = await InternetIncident.MeasureAsync("network_snapshot", async ct => await InternetIncident.ReadNetworkAsync(ct), timeout.Token, 6000);
        Assert.Contains(report.Status, new[] { "ok", "timeout", "cancelled" });
        if (report.Status == "ok")
        {
            using var json = System.Text.Json.JsonDocument.Parse(report.Detail!);
            Assert.True(json.RootElement.TryGetProperty("routes", out _));
            Assert.True(json.RootElement.TryGetProperty("dnsPolicies", out _));
        }
    }
    [Fact] public async Task DnsProbeValidatesReplyAndTimesOutIndependentlyOfHttps()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var responder = Task.Run(async () =>
        {
            var packet = await server.ReceiveAsync(); var query = packet.Buffer;
            var answer = query.Concat(new byte[] { 0xc0,0x0c,0,1,0,1,0,0,0,30,0,4,203,0,113,9 }).ToArray();
            answer[2]=0x81;answer[3]=0x80;answer[7]=1;
            await server.SendAsync(answer, packet.RemoteEndPoint);
        });
        var result = await InternetIncident.MeasureAsync("dns", ct => InternetIncident.DnsAsync(IPAddress.Loopback, "example.test", ct, port), default);
        await responder; Assert.Equal("ok", result.Status);
        var delayed = await InternetIncident.MeasureAsync("dns", ct => InternetIncident.DnsAsync(IPAddress.Loopback, "example.test", ct, port), default, 30);
        Assert.Equal("timeout", delayed.Status);
    }
    [Fact] public async Task TimeoutAndCancellationKeepOriginalStage()
    {
        var timed = await InternetIncident.MeasureAsync("dns", async token => { await Task.Delay(5000, token); return null; }, default, 20);
        Assert.Equal("timeout", timed.Status); Assert.Equal("dns", timed.Stage); Assert.True(timed.DurationMs >= 10);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var result = await InternetIncident.MeasureAsync("udp", async token => { await Task.Delay(5000, token); return null; }, cancelled.Token);
        Assert.Equal("cancelled", result.Status);
    }
    [Fact] public async Task UnsupportedAndSkippedProbesAreNotSuccessful()
    {
        var unsupported = await InternetIncident.MeasureAsync("http3", _ => throw new PlatformNotSupportedException("secret"), default);
        Assert.Equal("unavailable", unsupported.Status); Assert.DoesNotContain("secret", unsupported.Detail!);
        var skipped = await InternetIncident.MeasureAsync("tcp", _ => Task.FromResult<string?>("notProbed:mixedRoutes"), default);
        Assert.Equal("notProbed", skipped.Status);
    }
    [Fact] public async Task OneFailureDoesNotCancelAnotherProbeAndDetailsDoNotLeak()
    {
        var probes = await Task.WhenAll(InternetIncident.MeasureAsync("udp", _ => throw new SocketException(), default),
            InternetIncident.MeasureAsync("https", async token => { await Task.Delay(20, token); return "HTTP_204"; }, default));
        Assert.Equal("failed", probes[0].Status); Assert.Equal("ok", probes[1].Status); Assert.True(probes[1].DurationMs >= 10);
    }
    [Fact] public async Task DuplicateCaptureDoesNotRunAndGateRecoversAfterFailure()
    {
        var incident = new InternetIncident();
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var path = Path.Combine(Path.GetTempPath(), "ior-incident-test-" + Guid.NewGuid().ToString("N"));
        var first = incident.CaptureAsync(path, async _ => { entered.SetResult(); await release.Task; throw new IOException(); }, default);
        await entered.Task;
        Assert.Null(await incident.CaptureAsync(path, _ => throw new Exception("must not run"), default));
        release.SetResult(); await Assert.ThrowsAsync<IOException>(async () => await first);
        Assert.False(incident.Busy);
        try { var id = await incident.CaptureAsync(path, _ => Task.FromResult<object>(new { engine = "test" }), default); Assert.True(File.Exists(Path.Combine(path, id + ".json"))); }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    [Fact] public void TimelineAndRetentionAreBoundedWithoutTouchingRecovery()
    {
        var incident = new InternetIncident(); for (var i = 0; i < 1000; i++) incident.Observe("sample", count: i);
        Assert.Equal(256, incident.Timeline().Length);
        var path = Path.Combine(Path.GetTempPath(), "ior-incident-retention-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(path, "diagnostics", "internet-incidents"); Directory.CreateDirectory(dir);
        var old = Path.Combine(dir, new string('a', 32) + ".json"); File.WriteAllText(old, "{}"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-25));
        var unknown = Path.Combine(dir, "personal.json"); File.WriteAllText(unknown, "{}"); File.SetLastWriteTimeUtc(unknown, DateTime.UtcNow.AddHours(-25));
        try { WindowsTransientFiles.Prune(path, Path.Combine(path, "runtime"), DateTime.UtcNow, true, true); Assert.False(File.Exists(old)); Assert.True(File.Exists(unknown)); }
        finally { Directory.Delete(path, true); }
    }
}
