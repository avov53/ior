using HappBox;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;
namespace HappBox.Tests;
public sealed class NativeLocalComparisonTests
{
    [Fact]public async Task OptInLocalOldNewStartupAndTransferComparison()
    {
        var old=Environment.GetEnvironmentVariable("IOR_LOCAL_COMPARE_OLD");var candidate=Environment.GetEnvironmentVariable("IOR_LOCAL_COMPARE_NEW");
        if(old is null||candidate is null)return;
        var results=new List<object>();var bytes=Enumerable.Repeat((byte)0x61,4*1024*1024).ToArray();
        foreach(var variant in new[]{("old",old),("new",candidate)})for(var run=0;run<3;run++)
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));var token=deadline.Token;
            var destination=new TcpListener(IPAddress.Loopback,0);destination.Start();var port=((IPEndPoint)destination.LocalEndpoint).Port;
            var echo=Task.Run(async()=>{using var peer=await destination.AcceptTcpClientAsync(token);using var stream=peer.GetStream();await stream.CopyToAsync(stream,token);},token);
            var reserve=new TcpListener(IPAddress.Loopback,0);reserve.Start();var ingress=((IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
            try
            {
                Assert.True(CoreRuntimeContract.TryCreateStart(new AppState{TunEnabled=false},new ProxyProfile{Protocol=ProxyProtocol.Vless,Server="203.0.113.1",Port=443,UserId="11111111-1111-1111-1111-111111111111",TlsEnabled=true,ServerName="example.test"},out var request,out var reason),reason);
                request!.Routes=[new(){Kind="MATCH",Target="DIRECT"}];
                var watch=Stopwatch.StartNew();await using var core=await IorCoreControlClient.StartAsync(variant.Item2,ingress,request,token);var startup=watch.Elapsed.TotalMilliseconds;
                using var tcp=new TcpClient();await tcp.ConnectAsync(IPAddress.Loopback,ingress,token);using var proxy=tcp.GetStream();
                await proxy.WriteAsync(new byte[]{5,1,0},token);var greeting=new byte[2];await proxy.ReadExactlyAsync(greeting,token);Assert.Equal(new byte[]{5,0},greeting);
                await proxy.WriteAsync(new byte[]{5,1,0,1,127,0,0,1,(byte)(port>>8),(byte)port},token);var reply=new byte[10];await proxy.ReadExactlyAsync(reply,token);Assert.Equal(0,reply[1]);
                var received=new byte[bytes.Length];watch.Restart();
                await Task.WhenAll(Task.Run(async()=>{await proxy.WriteAsync(bytes,token);tcp.Client.Shutdown(SocketShutdown.Send);},token),proxy.ReadExactlyAsync(received,token).AsTask());
                Assert.Equal(bytes,received);await echo;results.Add(new{variant=variant.Item1,run,startupMs=startup,transferMs=watch.Elapsed.TotalMilliseconds,bytes=bytes.Length});
            }
            finally{destination.Stop();}
        }
        var output=Environment.GetEnvironmentVariable("IOR_LOCAL_COMPARE_OUTPUT");if(output is not null)await File.WriteAllTextAsync(output,System.Text.Json.JsonSerializer.Serialize(results));
    }
}
