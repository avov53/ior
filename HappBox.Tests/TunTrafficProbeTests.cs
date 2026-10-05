using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class TunTrafficProbeTests
{
    [Fact]
    public async Task SlowFirstDestinationDoesNotDelaySuccessfulSecondAndLoserIsCancelled()
    {
        var secondStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run=XrayTunHealthProbe.CheckTrafficStageAsync("HTTPS",async (attempt,token)=> {
            if(attempt==1){secondStarted.TrySetResult();return;}
            try {await Task.Delay(Timeout.Infinite,token);}catch(OperationCanceledException){firstCancelled.TrySetResult();throw;}
        },CancellationToken.None);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await run.WaitAsync(TimeSpan.FromSeconds(1));
        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task FastSecondFailureStillWaitsForSuccessfulFirst()
    {
        await XrayTunHealthProbe.CheckTrafficStageAsync("HTTPS",async (attempt,token)=> {
            if(attempt==1)throw new IOException("second unavailable");
            await Task.Delay(400,token);
        },CancellationToken.None);
    }
    [Fact]
    public async Task RetriesTransientFailureButRequiresTrafficSuccess()
    {
        var calls = 0;
        await XrayTunHealthProbe.CheckTrafficStageAsync("HTTPS", (attempt, _) =>
        {
            calls++;
            Assert.Equal(calls - 1, attempt);
            return attempt == 0 ? Task.FromException(new HttpRequestException()) : Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PersistentFailureReportsItsStageAndDoesNotRetryForever()
    {
        var calls = 0;
        var error = await Assert.ThrowsAsync<IOException>(() =>
            XrayTunHealthProbe.CheckTrafficStageAsync("UDP_DNS", (_, _) =>
            {
                calls++;
                return Task.FromException(new IOException("invalid response"));
            }, CancellationToken.None));
        Assert.Contains("TUN_PROBE_UDP_DNS_FAILED", error.Message);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SuccessfulStageRunsOnceAndCancellationNeverRetries()
    {
        var calls = 0;
        await XrayTunHealthProbe.CheckTrafficStageAsync("HTTPS", (_, _) =>
        { calls++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(1, calls);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            XrayTunHealthProbe.CheckTrafficStageAsync("HTTPS", (_, _) =>
            { calls++; return Task.CompletedTask; }, stop.Token));
        Assert.Equal(1, calls);
    }
}
