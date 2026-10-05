using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class NativeTunValidatorTests
{
    [Fact]
    public void RollbackComparisonDetectsOwnedPhysicalRouteDnsAndForwardingChanges()
    {
        var baseline = new NetworkDiagnosticSnapshot(DateTimeOffset.UtcNow, "before",
            [new("0.0.0.0/0", "192.0.2.1", 3, 10)], [new(3, ["1.1.1.1"])], [new(3, "Enabled", "Disabled", "Disabled")]);
        Assert.True(NativeTunValidator.NetworkRestored(baseline, baseline with { Phase = "after" }, 3));
        Assert.False(NativeTunValidator.NetworkRestored(baseline, baseline with { Routes = [.. baseline.Routes, new("198.51.100.1/32", "192.0.2.1", 3, 1)] }, 3));
        Assert.False(NativeTunValidator.NetworkRestored(baseline, baseline with { DnsServers = [new(3, ["172.29.91.2"])] }, 3));
        Assert.False(NativeTunValidator.NetworkRestored(baseline, baseline with { Interfaces = [new(3, "Disabled", "Disabled", "Disabled")] }, 3));
    }
}
