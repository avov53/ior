using HappBox;
using Xunit;
namespace HappBox.Tests;
public sealed class ClickLatencyTests
{
    private static ProxyProfile Profile(string ip)=>new(){Server=ip,Port=443,Protocol=ProxyProtocol.AmneziaWG};
    private static async Task Until(Func<bool> ready){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));while(!ready())await Task.Delay(5,timeout.Token);}
    [Theory][InlineData(ProxyProtocol.AmneziaWG,true)][InlineData(ProxyProtocol.WireGuard,true)][InlineData(ProxyProtocol.Hysteria2,true)][InlineData(ProxyProtocol.Vless,false)]
    public void AwgNeverProbesItsUdpPortWithTcp(ProxyProtocol protocol,bool icmp)=>Assert.Equal(icmp,LatencyMonitor.UsesIcmpOnly(protocol));
    [Fact]public async Task RepeatedClicksCoalesceAndSelectedEndpointIsNotAutomaticallyPolled()
    {
        var calls=0;var response=new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor=new LatencyMonitor((_,token)=>{Interlocked.Increment(ref calls);return response.Task.WaitAsync(token);});var profile=Profile("192.0.2.1");monitor.SetProfiles([profile],profile.Id);
        monitor.Select(profile.Id);await Until(()=>calls==1);monitor.Select(profile.Id);monitor.Select(profile.Id);Assert.True(monitor.SnapshotFor(profile)!.Measuring);
        response.SetResult(42);await Until(()=>!monitor.SnapshotFor(profile)!.Measuring);Assert.Equal(42,monitor.SnapshotFor(profile)!.Current);
        await monitor.ProbeDueAsync(CancellationToken.None);Assert.Equal(1,calls);
        monitor.Select(profile.Id);await Until(()=>calls==2&&!monitor.SnapshotFor(profile)!.Measuring);Assert.Equal(2,monitor.SnapshotFor(profile)!.Samples.Count);
    }
    [Fact]public async Task SwitchingSelectionIgnoresLateCancelledResultsAndKeepsBackgroundPolling()
    {
        var a=Profile("192.0.2.1");var b=Profile("192.0.2.2");var c=Profile("192.0.2.3");var old=new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);var started=false;var background=0;
        using var monitor=new LatencyMonitor((p,_)=>{if(p.Id==a.Id){started=true;return old.Task;}if(p.Id==c.Id)Interlocked.Increment(ref background);return Task.FromResult<long?>(12);});monitor.SetProfiles([a,b,c],a.Id);
        monitor.Select(a.Id);await Until(()=>started);monitor.Select(b.Id);await Until(()=>monitor.SnapshotFor(b)!.Attempted);old.SetResult(999);await Until(()=>!monitor.SnapshotFor(a)!.Measuring);
        Assert.Null(monitor.SnapshotFor(a)!.Current);Assert.Equal(12,monitor.SnapshotFor(b)!.Current);await monitor.ProbeDueAsync(CancellationToken.None);Assert.Equal(1,background);
    }
    [Fact]public async Task CancellingWhileQueuedDoesNotLeakSemaphoreOrRunningState()
    {
        var a=Profile("192.0.2.1");var b=Profile("192.0.2.2");var c=Profile("192.0.2.3");var release=new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);var started=false;
        using var monitor=new LatencyMonitor((p,_)=>{if(p.Id==a.Id){started=true;return release.Task;}return Task.FromResult<long?>(7);},maximumConcurrency:1);monitor.SetProfiles([a,b,c],a.Id);
        monitor.Select(a.Id);await Until(()=>started);monitor.Select(b.Id);monitor.Select(c.Id);release.SetResult(100);
        await Until(()=>monitor.SnapshotFor(c)!.Attempted&&!monitor.SnapshotFor(b)!.Measuring);Assert.Equal(7,monitor.SnapshotFor(c)!.Current);Assert.Null(monitor.SnapshotFor(b)!.Current);
    }
    [Fact]public async Task UnavailableHasNoInventedLatency()
    {
        using var monitor=new LatencyMonitor((_,_)=>Task.FromResult<long?>(null));var profile=Profile("192.0.2.1");monitor.SetProfiles([profile],profile.Id);monitor.Select(profile.Id);await Until(()=>monitor.SnapshotFor(profile)!.Attempted&&!monitor.SnapshotFor(profile)!.Measuring);
        Assert.Equal("unavailable",monitor.SnapshotFor(profile)!.Status);Assert.Equal("Недоступно",monitor.SnapshotFor(profile)!.Display);Assert.Null(monitor.SnapshotFor(profile)!.Current);
    }
    [Fact]public async Task ExplicitRefreshProbesTargetWithoutChangingSelectionAndCoalescesRequests()
    {
        var selected=Profile("192.0.2.1");var target=Profile("192.0.2.2");var calls=0;
        var selectedResponse=new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var targetResponse=new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor=new LatencyMonitor((p,token)=>p.Id==selected.Id?selectedResponse.Task.WaitAsync(token):ProbeTarget(token));
        Task<long?> ProbeTarget(CancellationToken token){Interlocked.Increment(ref calls);return targetResponse.Task.WaitAsync(token);}
        monitor.SetProfiles([selected,target],selected.Id);monitor.Select(selected.Id);
        await Until(()=>monitor.SnapshotFor(selected)!.Measuring);
        var first=monitor.RefreshAsync(target);var second=monitor.RefreshAsync(target);
        Assert.Same(first,second);await Until(()=>calls==1);targetResponse.SetResult(27);await first;
        Assert.True(monitor.SnapshotFor(selected)!.Measuring);Assert.Equal(27,monitor.SnapshotFor(target)!.Current);
        await monitor.RefreshAsync(target);Assert.Equal(2,calls);
        selectedResponse.SetResult(10);await Until(()=>!monitor.SnapshotFor(selected)!.Measuring);
    }
}
