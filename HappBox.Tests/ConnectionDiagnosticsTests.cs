using System.IO.Compression;
using HappBox;
using Xunit;

public sealed class ConnectionDiagnosticsTests
{
    [Fact]
    public void RedactsCredentialsAndFullUrlsButKeepsLocalDestination()
    {
        var value = ConnectionDiagnostics.Redact("password=sensitive abc 12345678-1234-1234-1234-123456789abc https://example.org/private/path?token=xxx direct 8.8.8.8:53", ["abc"]);
        Assert.DoesNotContain("sensitive", value);
        Assert.DoesNotContain("private/path", value);
        Assert.DoesNotContain("12345678", value);
        Assert.Contains("8.8.8.8:53", value);
    }

    [Fact]
    public async Task ExportIsAllowlistedAndPseudonymizesAddresses()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ior-diagnostics-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var diagnostics = new ConnectionDiagnostics(directory);
        diagnostics.Begin("xray", "tun", "Vless");
        diagnostics.Record("egress", "PHYSICAL_INTERFACE_SELECTED", ("address", "203.0.113.1"),
            ("interface", "PrivateWifi"), ("password", "should-not-exist"));
        var path = Path.Combine(directory, "bundle.zip");
        await diagnostics.ExportAsync(path, []);
        using var archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader(archive.GetEntry("events.json")!.Open());
        var exported = await reader.ReadToEndAsync();
        Assert.DoesNotContain("203.0.113.1", exported);
        Assert.DoesNotContain("PrivateWifi", exported);
        Assert.DoesNotContain("should-not-exist", exported);
        Assert.Contains("anon-", exported);
        Assert.Contains(diagnostics.RunId, exported);
    }
}
