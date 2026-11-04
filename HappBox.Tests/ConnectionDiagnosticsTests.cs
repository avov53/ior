using System.IO.Compression;
using HappBox;
using Xunit;

public sealed class ConnectionDiagnosticsTests
{
    [Fact]
    public void CompactionKeepsStartupAndLatestWholeJsonRowsWithinLimit()
    {
        var rows=Enumerable.Range(0,3000).Select(i=>System.Text.Json.JsonSerializer.Serialize(new{id=i,note=new string('x',128)})).ToArray();
        var compact=ConnectionDiagnostics.CompactResourceRows(rows);var kept=compact.Split('\n',StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(rows[0],kept[0]);Assert.Equal(rows[^1],kept[^1]);Assert.True(System.Text.Encoding.UTF8.GetByteCount(compact)<=160*1024);
        foreach(var row in kept){using var parsed=System.Text.Json.JsonDocument.Parse(row);Assert.True(parsed.RootElement.GetProperty("id").GetInt32()>=0);}
    }
    [Fact]
    public void ResourceReportKeepsOnlyCurrentRunAndAllowedMetadata()
    {
        var directory=Path.Combine(Path.GetTempPath(),"ior-resources-"+Guid.NewGuid().ToString("N"));
        try{
            using(var diagnostics=new ConnectionDiagnostics(directory)){
                diagnostics.Begin("ior-core","tun","AmneziaWG");diagnostics.Record("dns","RESOURCE_DNS_OBSERVATION",("ttlSeconds","30"),("password","secret"));
                diagnostics.Begin("ior-core","tun","AmneziaWG");diagnostics.Record("dns","RESOURCE_DNS_OBSERVATION",("ttlSeconds","17"),("dnsNameCount","1"),("transport","udp"),("nameSource","dns_candidates"),("password","secret"));
            }
            var report=File.ReadAllText(Path.Combine(directory,"resources.jsonl"));
            Assert.Contains("17",report);Assert.DoesNotContain("secret",report);Assert.DoesNotContain("\"ttlSeconds\":\"30\"",report);
            Assert.Contains("\"transport\":\"udp\"",report);Assert.Contains("dns_candidates",report);
            Assert.Single(File.ReadAllLines(Path.Combine(directory,"resources.jsonl")));
        }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
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
        diagnostics.Record("native", "DNS_RESOLVER_FAILED", ("destinationName", "cdn.example.net"),
            ("resolver", "8.8.8.8"), ("queryType", "65"), ("reason", "TUN_DNS_SERVER_FAILURE"));
        diagnostics.Record("dns","RESOURCE_DNS_RESPONSE",("rcode","3"),("answerCount","0"));
        var path = Path.Combine(directory, "bundle.zip");
        await diagnostics.ExportAsync(path, []);
        using var archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader(archive.GetEntry("events.json")!.Open());
        var exported = await reader.ReadToEndAsync();
        Assert.DoesNotContain("203.0.113.1", exported);
        Assert.DoesNotContain("PrivateWifi", exported);
        Assert.DoesNotContain("should-not-exist", exported);
        Assert.DoesNotContain("cdn.example.net", exported);
        Assert.DoesNotContain("8.8.8.8", exported);
        Assert.Contains("TUN_DNS_SERVER_FAILURE", exported);
        Assert.Contains("rcode",exported);Assert.Contains("answerCount",exported);
        Assert.Contains("anon-", exported);
        Assert.Contains(diagnostics.RunId, exported);
    }
}
