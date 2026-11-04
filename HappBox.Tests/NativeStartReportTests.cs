using System.Text.Json;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class NativeStartReportTests
{
    [Fact]
    public void EarlyFailureSurvivesLogRotationWithoutExceptionSecrets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ior-start-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var report = new NativeStartReport(dir, Guid.NewGuid().ToString("N"), "1.2.3", 19045, "test-hash", "tun", "Hysteria2");
            report.Enter("preflight");
            report.Fail(new IOException("WINTUN_LIBRARY_MISSING https://secret.example/sub?token=private-key hwid=private-id"));
            Assert.True(report.Persisted);
            var text = File.ReadAllText(report.Path);
            Assert.DoesNotContain("private-key", text); Assert.DoesNotContain("private-id", text); Assert.DoesNotContain("secret.example", text);
            using var json = JsonDocument.Parse(text);
            Assert.Equal("WINTUN_LIBRARY_MISSING", json.RootElement.GetProperty("code").GetString());
            Assert.Equal(19045, json.RootElement.GetProperty("windowsBuild").GetInt32());
            Assert.Equal("preflight", json.RootElement.GetProperty("stage").GetString());
            report.Enter("httpsProbe"); report.Complete();
            using var complete = JsonDocument.Parse(File.ReadAllText(report.Path));
            Assert.Equal("connected", complete.RootElement.GetProperty("status").GetString());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
