using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class TunLoopGuardTests
{
    [Fact]
    public void RepeatedDirectSocketFailuresTripGuard()
    {
        var guard = new TunLoopGuard();
        var now = DateTime.UtcNow;
        const string failure = "ERROR outbound/direct[direct]: Only one usage of each socket address is normally permitted";
        for (var index = 0; index < 11; index++) Assert.Equal(TunLoopObservation.None, guard.Record(failure, now.AddMilliseconds(index * 100)));
        Assert.Equal(TunLoopObservation.SocketExhausted, guard.Record(failure, now.AddMilliseconds(1100)));
    }

    [Fact]
    public void OldOrUnrelatedErrorsDoNotTripGuard()
    {
        var guard = new TunLoopGuard();
        var now = DateTime.UtcNow;
        const string failure = "ERROR outbound/direct[direct]: Only one usage of each socket address is normally permitted";
        for (var index = 0; index < 11; index++) Assert.Equal(TunLoopObservation.None, guard.Record(failure, now.AddSeconds(index)));
        Assert.Equal(TunLoopObservation.None, guard.Record("INFO outbound/direct[direct]: connected", now.AddSeconds(11)));
        Assert.Equal(TunLoopObservation.None, guard.Record("ERROR outbound/vless[proxy]: reality verification failed", now.AddSeconds(11)));
        guard.Reset();
        Assert.Equal(TunLoopObservation.None, guard.Record(failure, now.AddSeconds(11)));
    }

    [Fact]
    public void RepeatedDirectConnectionsToOneDestinationTripBeforeSocketExhaustion()
    {
        var guard = new TunLoopGuard();
        var now = DateTime.UtcNow;
        const string line = "INFO outbound/direct[direct]: outbound connection to 192.0.2.1:80";
        for (var index = 0; index < 47; index++)
            Assert.Equal(TunLoopObservation.None, guard.Record(line, now.AddMilliseconds(index * 50)));
        Assert.Equal(TunLoopObservation.UnconfirmedBurst, guard.Record(line, now.AddMilliseconds(47 * 50)));
        Assert.Equal(TunLoopObservation.None, guard.Record(line, now.AddMilliseconds(48 * 50)));
    }

    [Fact]
    public void DistinctDestinationsAndSlowConnectionsDoNotTripGuard()
    {
        var guard = new TunLoopGuard();
        var now = DateTime.UtcNow;
        for (var index = 0; index < 100; index++)
            Assert.Equal(TunLoopObservation.None, guard.Record($"INFO outbound/direct[direct]: outbound connection to 192.0.2.{index % 100 + 1}:80", now.AddMilliseconds(index * 20)));
        guard.Reset();
        for (var index = 0; index < 100; index++)
            Assert.Equal(TunLoopObservation.None, guard.Record("INFO outbound/direct[direct]: outbound connection to 192.0.2.1:80", now.AddSeconds(index)));
    }

    [Fact]
    public void ASingleSocketBufferExhaustionStopsTunImmediately()
    {
        var guard = new TunLoopGuard();
        Assert.Equal(TunLoopObservation.SocketExhausted,
            guard.Record("ERROR outbound/direct[direct]: system lacked sufficient buffer space", DateTime.UtcNow));
    }
}
