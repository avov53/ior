using HappBox;
using System.Net.Sockets;
using Xunit;
namespace HappBox.Tests;
public sealed class Windows126Tests
{
    [Fact]
    public async Task ProbeKeepsBothFailureKinds()
    {
        var error=await Assert.ThrowsAsync<IOException>(()=>XrayTunHealthProbe.CheckTrafficStageAsync("UDP_DNS",
            (attempt,token)=>Task.FromException(attempt==0?new SocketException((int)SocketError.AddressFamilyNotSupported):new OperationCanceledException()),
            CancellationToken.None,hedgeDelay:TimeSpan.Zero));
        Assert.Contains("SOCKET_AddressFamilyNotSupported",error.Message);
        Assert.Contains("TIMEOUT",error.Message);
    }
    [Fact]
    public async Task CancelledProbeDoesNotReportSuccess()
    {
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>XrayTunHealthProbe.CheckTrafficStageAsync("DNS",(_,_)=>Task.CompletedTask,cancellation.Token));
    }
}
