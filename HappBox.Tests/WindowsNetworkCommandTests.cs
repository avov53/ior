using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class WindowsNetworkCommandTests
{
    [Fact]
    public async Task PreparedProcessIsReusedAndCancelledCommandDoesNotPoisonNextResponse()
    {
        await WindowsNetworkCommand.WarmupAsync();var first=WindowsNetworkCommand.WorkerProcessId;
        Assert.NotNull(first);
        Assert.Equal("one",(await WindowsNetworkCommand.RunAsync("'one'")).Trim());
        Assert.Equal(first,WindowsNetworkCommand.WorkerProcessId);
        using var stop=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>WindowsNetworkCommand.RunAsync("Start-Sleep -Seconds 10; 'stale'",stop.Token));
        Assert.Equal("fresh",(await WindowsNetworkCommand.RunAsync("'fresh'")).Trim());
        Assert.NotEqual(first,WindowsNetworkCommand.WorkerProcessId);
    }
    [Fact]
    public async Task SuppressedMissingItemDoesNotTurnSuccessfulCleanupIntoFailure()
    {
        var result = await WindowsNetworkCommand.RunAsync(
            "Write-Error 'EXPECTED_ABSENT_ITEM' -ErrorAction SilentlyContinue; if ($false) { throw 'UNREACHABLE' }");
        Assert.True(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public async Task MissingRouteLookupIsSuccessfulAndDoesNotChangeNetwork()
    {
        var result = await WindowsNetworkCommand.RunAsync(
            "Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '198.51.100.231/32' -ErrorAction SilentlyContinue | Out-Null");
        Assert.True(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public async Task ActualFailureKeepsReadableUnicodeReasonInsteadOfProgressXml()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WindowsNetworkCommand.RunAsync("throw 'NETWORK_TEST_REJECTED: сетевая команда отклонена'"));
        Assert.Contains("NETWORK_TEST_REJECTED", error.Message);
        Assert.Contains("сетевая команда отклонена", error.Message);
        Assert.Contains("exit=1", error.Message);
        Assert.DoesNotContain("CLIXML", error.Message);
    }
}
