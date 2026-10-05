using System.Net;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class XrayTunRouteLeaseTests
{
    [Fact]
    public async Task RestartClearsJournalWhenOwnedRouteHasAlreadyDisappeared()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ior-missing-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var journal = XrayTunRouteLease.JournalPath(directory);
        try
        {
            // Impossible interface index: no system route can match this ownership tuple.
            await File.WriteAllTextAsync(journal,
                "{\"InterfaceName\":\"Wi-Fi\",\"Address\":\"192.0.2.5\",\"Gateway\":\"192.0.2.1\",\"Routes\":[{\"Prefix\":\"203.0.113.251/32\",\"InterfaceIndex\":2147483000,\"NextHop\":\"192.0.2.1\"}]}");
            var lease = XrayTunRouteLease.LoadForRecovery(directory)!;
            await lease.ReleaseAsync();
            await lease.ReleaseAsync();
            Assert.False(File.Exists(journal));
        }
        finally
        {
            if (File.Exists(journal)) File.Delete(journal);
            Directory.Delete(directory);
        }
    }
    [Fact]
    public async Task NativeRecoveryRejectsAnUnownedAdapterIdentity()
    {
        var directory=Path.Combine(Path.GetTempPath(),"ior-native-journal-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var journal=XrayTunRouteLease.JournalPath(directory);
        try
        {
            await File.WriteAllTextAsync(journal,"{\"InterfaceName\":\"Wi-Fi\",\"Address\":\"192.0.2.5\",\"Gateway\":\"192.0.2.1\",\"Routes\":[],\"NativeTunName\":\"Wi-Fi\",\"NativeTunIndex\":3,\"NativeTunAddress\":\"172.29.91.1\"}");
            Assert.Throws<InvalidDataException>(()=>XrayTunRouteLease.LoadForRecovery(directory));
        }
        finally{File.Delete(journal);Directory.Delete(directory);}
    }
    [Fact]
    public async Task ForwardingRecoveryRequiresAnExplicitInterfaceIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ior-forwarding-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var journal = XrayTunRouteLease.JournalPath(directory);
        try
        {
            await File.WriteAllTextAsync(journal, "{\"InterfaceName\":\"Wi-Fi\",\"Address\":\"192.0.2.5\",\"Gateway\":\"192.0.2.1\",\"Routes\":[],\"RestoreForwarding\":true}");
            Assert.Throws<InvalidDataException>(() => XrayTunRouteLease.LoadForRecovery(directory));
            await File.WriteAllTextAsync(journal, "{\"InterfaceName\":\"Wi-Fi\",\"Address\":\"192.0.2.5\",\"Gateway\":\"192.0.2.1\",\"Routes\":[],\"RestoreForwarding\":true,\"ForwardingInterfaceIndex\":3}");
            Assert.NotNull(XrayTunRouteLease.LoadForRecovery(directory));
        }
        finally { File.Delete(journal); Directory.Delete(directory); }
    }
    [Fact]
    public async Task WatchdogCannotRecoverAnotherRunsJournal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ior-journal-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var journal = XrayTunRouteLease.JournalPath(directory);
        await File.WriteAllTextAsync(journal, "{\"InterfaceName\":\"Wi-Fi\",\"Address\":\"192.0.2.5\",\"Gateway\":\"192.0.2.1\",\"Routes\":[],\"RunId\":\"new-run\"}");
        Assert.Null(XrayTunRouteLease.LoadForRecovery(directory, "old-run"));
        Assert.NotNull(XrayTunRouteLease.LoadForRecovery(directory, "new-run"));
        File.Delete(journal); Directory.Delete(directory);
    }
    [Fact]
    public async Task RestartCanLoadAndClearAnEmptyRouteJournal()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ior-route-journal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var journal = XrayTunRouteLease.JournalPath(directory);
            await File.WriteAllTextAsync(journal,
                "{\"InterfaceName\":\"Wi-Fi\",\"Address\":\"192.0.2.5\",\"Gateway\":\"192.0.2.1\",\"Routes\":[]}");
            var recovered = XrayTunRouteLease.LoadForRecovery(directory);
            Assert.NotNull(recovered);
            await recovered!.ReleaseAsync();
            Assert.False(File.Exists(journal));
        }
        finally
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task OptInElevatedRouteLeaseAddsAndRemovesOnlyItsOwnHostRoutes()
    {
        if (Environment.GetEnvironmentVariable("IOR_LIVE_ROUTE_LEASE_TEST") != "1") return;
        var directory = Path.Combine(Path.GetTempPath(), $"ior-route-lease-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        XrayTunRouteLease? lease = null;
        try
        {
            var physical = PhysicalInterfaceResolver.ForIpv4(IPAddress.Parse("1.1.1.1"));
            lease = await XrayTunRouteLease.AcquireAsync(directory, IPAddress.Parse("203.0.113.123"),
                IPAddress.Parse("198.51.100.123"), physical);
            Assert.True(File.Exists(XrayTunRouteLease.JournalPath(directory)));
            await lease.ReleaseAsync();
            Assert.False(File.Exists(XrayTunRouteLease.JournalPath(directory)));
        }
        finally
        {
            if (lease is not null) await lease.ReleaseAsync();
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
}
