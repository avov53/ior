using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using HappBox;
using Xunit;

namespace HappBox.Tests;
public sealed class RealityTimingTests
{
    [Fact]
    public async Task OptInCompareRealityHandshakeWithoutChangingWindowsNetwork()
    {
        if(Environment.GetEnvironmentVariable("IOR_REALITY_COMPARE")!="1")return;
        var state=JsonSerializer.Deserialize<AppState>(File.ReadAllText(Environment.GetEnvironmentVariable("IOR_USER_STATE_TEST_PATH")!))!;
        var profile=state.Profiles.First(p=>p.Protocol==ProxyProtocol.Vless && p.RealityEnabled);
        state.TunEnabled=false;state.SelectedHappRoutingProfileId=null;
        var samples=new List<object>();
        for(var run=0;run<5;run++)foreach(var variant in run%2==0?new[]{"control","candidate"}:new[]{"candidate","control"})
        {
            Assert.True(CoreRuntimeContract.TryCreateStart(state,profile,out var request,out var reason),reason);
            var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();var port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
            var binary=Environment.GetEnvironmentVariable(variant=="control"?"IOR_REALITY_CONTROL_BINARY":"IOR_NATIVE_TEST_BINARY")!;
            var phases=new System.Collections.Concurrent.ConcurrentQueue<string>();
            await using var client=await IorCoreControlClient.StartAsync(binary,port,request!);
            client.DiagnosticMessage+=line=>{if(line.StartsWith("REALITY_",StringComparison.Ordinal))phases.Enqueue(line);};
            using var http=new HttpClient(new HttpClientHandler {UseProxy=true,Proxy=new WebProxy("127.0.0.1",port)}) {Timeout=TimeSpan.FromSeconds(15)};
            var elapsed=Stopwatch.StartNew();string? failure=null;
            try {using var response=await http.GetAsync("https://example.com/",HttpCompletionOption.ResponseHeadersRead);response.EnsureSuccessStatusCode();}
            catch(Exception error){failure=error.GetType().Name;}
            var timings=phases.Select(line=>new {Code=line.Split(' ')[0],ElapsedMs=Number(line,"elapsed",true),DurationMs=Number(line,"durationMs"),ReadWaitUs=Number(line,"readWaitUs")}).ToArray();
            samples.Add(new{run,variant,httpHeadersMs=elapsed.ElapsedMilliseconds,failure,timings});
        }
        File.WriteAllText(Environment.GetEnvironmentVariable("IOR_REALITY_COMPARE_REPORT")!,JsonSerializer.Serialize(new {scope="live proxy, fresh processes, example.com response headers; not TUN startup or browser rendering",samples},new JsonSerializerOptions {WriteIndented=true}));
    }
    private static long? Number(string text,string field,bool ms=false)
    {
        var match=Regex.Match(text,Regex.Escape(field)+@"=(\d+)"+(ms?"ms":""));
        return match.Success?long.Parse(match.Groups[1].Value):null;
    }
}
