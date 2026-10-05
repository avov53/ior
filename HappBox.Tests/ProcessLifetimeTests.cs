using System.Diagnostics;
using HappBox;
using Xunit;
namespace HappBox.Tests;
public sealed class ProcessLifetimeTests
{
    [Fact]
    public void UnassociatedAndDisposedProcessesAreNotRunning()
    {
        Assert.False(ProcessLifetime.IsRunning(null));
        var process=new Process();
        Assert.False(ProcessLifetime.IsRunning(process));
        process.Dispose();
        Assert.False(ProcessLifetime.IsRunning(process));
    }
}
