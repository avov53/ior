using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class NetworkDiagnosticSnapshotTests
{
    [Fact]
    public async Task OptInReadOnlySnapshotContainsOnlyRouteAndDnsEvidence()
    {
        if (Environment.GetEnvironmentVariable("IOR_NETWORK_SNAPSHOT_TEST") != "1") return;
        var snapshot = await NetworkDiagnosticSnapshot.CaptureAsync("reference");
        Assert.NotEmpty(snapshot.Routes);
        Assert.NotEmpty(snapshot.DnsServers);
        Assert.Equal("reference", snapshot.Phase);
    }
}
