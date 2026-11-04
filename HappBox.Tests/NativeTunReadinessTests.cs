using HappBox;
using Xunit;
namespace HappBox.Tests;
public sealed class NativeTunReadinessTests
{
    [Fact]public async Task RequiredDnsFailureCannotBeReportedAsConnected(){
        var result=await NativeTunReadiness.CheckAsync(Success,_=>throw new IOException("DNS_FAILED"),Success,default,requireDns:true);
        Assert.False(result.Ready);Assert.Equal("DNS_FAILED",result.Dns.Code);
    }
    [Fact]public async Task RequiredDnsWaitsPastOptionalBudget(){
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation=NativeTunReadiness.CheckAsync(Success,_=>completion.Task,Success,default,TimeSpan.FromMilliseconds(10),requireDns:true);
        await Task.Delay(30);Assert.False(operation.IsCompleted);completion.SetResult();Assert.True((await operation).Ready);
    }
    [Fact]public async Task AdapterDiscoveryRequiresOwnedNameAndUpStatus(){
        var count=0;
        Assert.True(await TunAdapterDiscovery.WaitAsync("ior-xray",inspect:()=>{count++;return [new("other-vpn",System.Net.NetworkInformation.OperationalStatus.Up),new("ior-xray",count<3?System.Net.NetworkInformation.OperationalStatus.Down:System.Net.NetworkInformation.OperationalStatus.Up)];},limit:TimeSpan.FromSeconds(1)));
        Assert.False(await TunAdapterDiscovery.WaitAsync("ior-xray",inspect:()=>[new("other-vpn",System.Net.NetworkInformation.OperationalStatus.Up)],limit:TimeSpan.FromMilliseconds(20)));
    }
    private static Task Success(CancellationToken token)=>Task.CompletedTask;
    private static Task Wait(CancellationToken token)=>Task.Delay(Timeout.Infinite,token);
    [Fact]public async Task FailedUdpDoesNotDisableVerifiedHttpsAndDns()
    {
        var result=await NativeTunReadiness.CheckAsync(Success,Success,_=>throw new IOException("UDP_REFERENCE_REJECTED"),default);
        Assert.True(result.Ready);Assert.Equal("ready",result.Dns.Status);
        Assert.Equal("error",result.Udp.Status);Assert.Equal("UDP_REFERENCE_REJECTED",result.Udp.Code);
    }
    [Fact]public async Task OptionalTimeoutsAreBoundedAndRemainUnverified()
    {
        var result=await NativeTunReadiness.CheckAsync(Success,Wait,Wait,default,TimeSpan.FromMilliseconds(70));
        Assert.True(result.Ready);Assert.Equal("unverified",result.Dns.Status);Assert.Equal("unverified",result.Udp.Status);
        Assert.InRange(result.Udp.DurationMs,40,1000);
    }
    [Fact]public async Task HttpsFailurePreventsReadyAndCancelsOptionalProbes()
    {
        var result=await NativeTunReadiness.CheckAsync(_=>throw new IOException("HTTPS_CERTIFICATE_REJECTED"),Wait,Wait,default);
        Assert.False(result.Ready);Assert.Equal("HTTPS_CERTIFICATE_REJECTED",result.Https.Code);
    }
    [Fact]public async Task ReadyWaitsForHttpsEvenWhenDnsAndUdpAlreadyPassed()
    {
        var verified=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation=NativeTunReadiness.CheckAsync(_=>verified.Task,Success,Success,default);
        Assert.False(operation.IsCompleted);verified.SetResult();Assert.True((await operation).Ready);
    }
    [Fact]public async Task UserCancellationNeverBecomesSuccessfulPartialReadiness()
    {
        using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>NativeTunReadiness.CheckAsync(Wait,Wait,Wait,cancel.Token));
    }
}
