using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace HappBox;

/// <summary>Opt-in system TUN experiment, with an owned lease and rollback each cycle.</summary>
public static class NativeTunBenchmark
{
    public sealed record Measurement(int Run,string Protocol,string Variant,string Stage,bool Warm,long Milliseconds);
    public sealed record Failure(int Run,string Protocol,string Variant,string Code,string? Stage=null,
        string? HttpError=null,int? HttpStatus=null,string? SocketError=null,string? WebSocketError=null);
    public static Failure DescribeFailure(int run,string protocol,string variant,Exception error)
    {
        var code=System.Text.RegularExpressions.Regex.Match(error.Message,"^[A-Z][A-Z0-9_]{3,63}").Value;
        var stage=error.Data["iorProbeStage"] as string;
        if(stage is not null&&!System.Text.RegularExpressions.Regex.IsMatch(stage,"^[a-z_]{1,64}$"))stage=null;
        string? httpError=null,socketError=null,webSocketError=null;int? httpStatus=null;
        for(var cause=error;cause is not null;cause=cause.InnerException) {
            if(cause is HttpRequestException http){httpError=http.HttpRequestError.ToString();httpStatus=(int?)http.StatusCode;}
            if(cause is System.Net.Sockets.SocketException socket)socketError=socket.SocketErrorCode.ToString();
            if(cause is System.Net.WebSockets.WebSocketException webSocket)webSocketError=webSocket.WebSocketErrorCode.ToString();
        }
        return new(run,protocol,variant,string.IsNullOrEmpty(code)?error.GetType().Name:code,stage,httpError,httpStatus,socketError,webSocketError);
    }
    public static async Task RunPackagedAsync(string id,string? baselinePath=null)
    {
        var directory=NativeTunValidator.DirectoryFor(id);Directory.CreateDirectory(directory);
        var report=Environment.GetEnvironmentVariable("IOR_NATIVE_TUN_BENCH_REPORT")??Path.Combine(directory,"comparison.json");
        void Progress(string stage,string reason="")=>File.WriteAllText(Path.Combine(directory,"progress.json"),JsonSerializer.Serialize(new NativeTunValidator.Progress(stage,reason,DateTime.UtcNow)));
        var samples=new System.Collections.Concurrent.ConcurrentQueue<Measurement>();var errors=new List<Failure>();
        string? binaryHash=null;string? baselineHash=null;var rollbackPassed=true;
        async Task Save()=>await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new {
            schema=4,engine="ior-core",mode="tun",binarySha256=binaryHash,baselineSha256=baselineHash,
            scope="TUN readiness, DNS, HTTPS first body byte and complete body; browser rendering, Updating and video playback are not measured",
            measurements=samples,failures=errors,rollbackPassed,
            summary=samples.GroupBy(item=>new{item.Protocol,item.Variant,item.Stage,item.Warm}).Select(group=>new {
                group.Key.Protocol,group.Key.Variant,group.Key.Stage,group.Key.Warm,passed=group.Count(),
                medianMs=Median(group.Select(item=>item.Milliseconds)),p95Ms=Percentile95(group.Select(item=>item.Milliseconds))
            }),comparisons=Compare(samples,errors),accelerationConfirmed=errors.Count==0&&Compare(samples,errors).All(item=>item.Accepted),
            acceptanceReason="Requires ten successful starts for both variants, >=15% median improvement, p95 <=5% worse and no new failed loads. Browser playback/Updating are recorded separately."
        },new JsonSerializerOptions{WriteIndented=true}));
        try {
            if(!NativeTunValidator.IsAdministrator())throw new IOException("ADMINISTRATOR_REQUIRED");
            using var benchmarkLock=File.Open(Path.Combine(directory,"..","benchmark.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            var path=Environment.GetEnvironmentVariable("IOR_USER_STATE_TEST_PATH")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"ior","state.json");
            var state=JsonSerializer.Deserialize<AppState>(await File.ReadAllTextAsync(path))??throw new IOException("STATE_UNAVAILABLE");
            state.SelectedCore=CoreKind.IorCore;state.ExperimentalCoreTesting=true;state.TunEnabled=true;
            var wg=state.Profiles.FirstOrDefault(item=>item.Protocol==ProxyProtocol.WireGuard)??throw new IOException("WIREGUARD_PROFILE_REQUIRED");
            var hy=state.Profiles.FirstOrDefault(item=>item.Protocol==ProxyProtocol.Hysteria2)??throw new IOException("HYSTERIA2_PROFILE_REQUIRED");
            // Only this deserialized test state changes; persisted profiles remain untouched.
            state.DnsServer="8.8.8.8";wg.TunnelDns="8.8.8.8";
            var binary=Environment.GetEnvironmentVariable("IOR_NATIVE_TEST_BINARY")??CoreEngineService.IorCorePath(state);
            var baseline=baselinePath??Environment.GetEnvironmentVariable("IOR_NATIVE_TUN_BASELINE_BINARY")??throw new IOException("BASELINE_BINARY_REQUIRED");
            using(var stream=File.OpenRead(baseline))baselineHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            using(var stream=File.OpenRead(binary))binaryHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            for(var run=0;run<10;run++)foreach(var profile in run%2==0?new[]{wg,hy}:new[]{hy,wg})foreach(var variant in run%2==0?new[]{"baseline","current"}:new[]{"current","baseline"}) {
                var legacy=variant=="baseline";
                WindowsNetworkCommand.WorkerEnabled=true;
                {var preparation=Stopwatch.StartNew();await WindowsNetworkCommand.WarmupAsync();samples.Enqueue(new(run,profile.Protocol.ToString(),variant,"worker_preparation",false,preparation.ElapsedMilliseconds));}
                var protocol=profile.Protocol.ToString();Progress($"run-{run+1}-{protocol}-{variant}");
                var endpoint=await BootstrapDnsResolver.ResolveEndpointAsync(profile.Server,state.BootstrapDnsServer);
                var physical=PhysicalInterfaceResolver.ForAddress(endpoint);
                var index=NetworkInterface.GetAllNetworkInterfaces().Single(item=>item.Name==physical).GetIPProperties().GetIPv4Properties()!.Index;
                var before=await NetworkDiagnosticSnapshot.CaptureAsync("before-benchmark");
                var beforeProxy=NativeTunValidator.ProxySnapshot();
                var beforeAdapters=NetworkInterface.GetAllNetworkInterfaces().Where(item=>item.Name.StartsWith("ior-native-")&&item.OperationalStatus==OperationalStatus.Up).Select(item=>item.Id).ToHashSet();
                var cycle=Path.Combine(directory,$"cycle-{run}-{protocol}-{variant}");
                Directory.CreateDirectory(cycle);
                try {
                    await NativeTunValidator.ValidateAsync(state,profile,legacy?baseline:binary,cycle,Environment.ProcessPath!,
                        stage=>Progress($"run-{run+1}-{protocol}-{variant}-{stage}"),
                        (stage,ms,warm)=>samples.Enqueue(new(run,protocol,variant,stage,warm,ms)),performance:true,
                        fastStartProbeOnly:true,prepareAddresses:!legacy,measurePages:true);
                } catch(Exception error) {
                    var code=System.Text.RegularExpressions.Regex.Match(error.Message,"^[A-Z][A-Z0-9_]{3,63}").Value;
                    errors.Add(DescribeFailure(run,protocol,variant,error));
                    if(code=="STOP_ACTIVE_VPN_BEFORE_VALIDATION")throw;
                }
                var after=await NetworkDiagnosticSnapshot.CaptureAsync("after-benchmark");
                rollbackPassed=NativeTunValidator.NetworkRestored(before,after,index)&&
                    beforeProxy==NativeTunValidator.ProxySnapshot()&&
                    !NetworkInterface.GetAllNetworkInterfaces().Any(item=>item.Name.StartsWith("ior-native-")&&item.OperationalStatus==OperationalStatus.Up&&!beforeAdapters.Contains(item.Id))&&
                    !Directory.EnumerateFiles(cycle,"xray-tun-routes.json",SearchOption.AllDirectories).Any();
                await Save();
                if(!rollbackPassed)throw new IOException("BENCHMARK_NETWORK_ROLLBACK_FAILED");
                await XrayTunRouteLease.EnsureNativeDnsPolicyAvailableAsync();
            }
            // Separate crash qualification; its timings are not mixed with normal starts.
            WindowsNetworkCommand.WorkerEnabled=true;await WindowsNetworkCommand.WarmupAsync();
            foreach(var profile in new[]{wg,hy}) {
                Progress("crash-check-"+profile.Protocol);
                await NativeTunValidator.ValidateAsync(state,profile,binary,Path.Combine(directory,"crash-"+profile.Protocol),Environment.ProcessPath!,fastStartProbeOnly:true);
            }
            await Save();Progress(errors.Count==0?"passed":"completed-with-errors",$"cycles=40; failed={errors.Count}");
            if(errors.Count!=0)Environment.ExitCode=2;
        } catch(Exception error) {
            var code=System.Text.RegularExpressions.Regex.Match(error.Message,"^[A-Z][A-Z0-9_]{3,63}").Value;
            errors.Add(DescribeFailure(-1,"qualification","current",error));await Save();
            Progress("failed",string.IsNullOrEmpty(code)?error.GetType().Name:code);Environment.ExitCode=2;
        }
        finally {await WindowsNetworkCommand.ShutdownAsync();}
    }
    public sealed record Comparison(string Protocol,string Stage,bool Warm,int BaselineCount,int CurrentCount,double BaselineMedian,double CurrentMedian,long BaselineP95,long CurrentP95,bool Accepted);
    public static Comparison[] Compare(IEnumerable<Measurement> measurements,IEnumerable<Failure> failures)
    {
        var values=measurements.ToArray();var errors=failures.ToArray();
        return new[]{"WireGuard","Hysteria2"}.SelectMany(protocol=>new[]{("ready",false),("telegram_http_first_byte",false),("roblox_http_first_byte",false),("youtube_http_first_byte",false),("telegram_http_first_byte",true),("roblox_http_first_byte",true),("youtube_http_first_byte",true)}.Select(stage=>{
            var old=values.Where(v=>v.Protocol==protocol&&v.Variant=="baseline"&&v.Stage==stage.Item1&&v.Warm==stage.Item2).Select(v=>v.Milliseconds).ToArray();
            var current=values.Where(v=>v.Protocol==protocol&&v.Variant=="current"&&v.Stage==stage.Item1&&v.Warm==stage.Item2).Select(v=>v.Milliseconds).ToArray();
            var accepted=old.Length==10&&current.Length==10&&!errors.Any(e=>e.Protocol==protocol&&e.Variant=="current")&&Median(current)<=Median(old)*.85&&Percentile95(current)<=Percentile95(old)*1.05;
            return new Comparison(protocol,stage.Item1,stage.Item2,old.Length,current.Length,Median(old),Median(current),Percentile95(old),Percentile95(current),accepted);
        })).ToArray();
    }
    public static double Median(IEnumerable<long> values){var sorted=values.Order().ToArray();return sorted.Length==0?0:sorted.Length%2==0?(sorted[sorted.Length/2-1]+(double)sorted[sorted.Length/2])/2:sorted[sorted.Length/2];}
    public static long Percentile95(IEnumerable<long> values){var sorted=values.Order().ToArray();return sorted.Length==0?0:sorted[(int)Math.Ceiling(sorted.Length*.95)-1];}
    public static async Task MeasurePagesAsync(Action<string,long,bool> measured,Func<string,bool>? proxyConfirmed=null)
    {
        foreach(var page in new[]{("telegram","https://web.telegram.org/a/"),("roblox","https://www.roblox.com/"),("youtube","https://www.youtube.com/")}) {
            using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){Timeout=Timeout.InfiniteTimeSpan};
            for(var warm=0;warm<2;warm++) {
                var probeStage=page.Item1+"_http_headers_"+(warm==0?"cold":"warm");
                try {
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));var watch=Stopwatch.StartNew();
                using var response=await http.GetAsync(page.Item2,HttpCompletionOption.ResponseHeadersRead,deadline.Token);response.EnsureSuccessStatusCode();
                probeStage=page.Item1+"_http_first_byte_"+(warm==0?"cold":"warm");
                using var body=await response.Content.ReadAsStreamAsync(deadline.Token);
                if(await body.ReadAsync(new byte[1],deadline.Token)==0)throw new IOException("PAGE_EMPTY_RESPONSE");
                if(proxyConfirmed is not null) {
                    for(var wait=0;wait<10&&!proxyConfirmed(new Uri(page.Item2).Host);wait++)await Task.Delay(10,deadline.Token);
                    if(!proxyConfirmed(new Uri(page.Item2).Host))throw new IOException("PAGE_PROXY_PATH_UNCONFIRMED");
                }
                measured(page.Item1+"_http_first_byte",watch.ElapsedMilliseconds,warm!=0);
                probeStage=page.Item1+"_http_body_"+(warm==0?"cold":"warm");
                await body.CopyToAsync(Stream.Null,deadline.Token);
                measured(page.Item1+"_http_body_complete",watch.ElapsedMilliseconds,warm!=0);
                } catch(Exception error) {error.Data["iorProbeStage"]=probeStage;throw;}
            }
        }
        var websocketStage="discord_websocket_connect";
        try {
        using var gateway=new System.Net.WebSockets.ClientWebSocket();gateway.Options.Proxy=null;
        using var websocketDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));var websocketTime=Stopwatch.StartNew();
        await gateway.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=10&encoding=json"),websocketDeadline.Token);
        websocketStage="discord_websocket_hello";
        using var hello=new MemoryStream();var buffer=new byte[8192];System.Net.WebSockets.WebSocketReceiveResult message;
        do {message=await gateway.ReceiveAsync(buffer,websocketDeadline.Token);hello.Write(buffer,0,message.Count);if(hello.Length>65536)throw new IOException("WEBSOCKET_HELLO_TOO_LARGE");}while(!message.EndOfMessage);
        using var helloDocument=JsonDocument.Parse(hello.ToArray());if(helloDocument.RootElement.GetProperty("op").GetInt32()!=10)throw new IOException("WEBSOCKET_HELLO_INVALID");
        if(proxyConfirmed is not null && !proxyConfirmed("gateway.discord.gg"))throw new IOException("WEBSOCKET_PROXY_PATH_UNCONFIRMED");
        measured("discord_websocket",websocketTime.ElapsedMilliseconds,false);gateway.Abort();
        } catch(Exception error) {error.Data["iorProbeStage"]=websocketStage;throw;}
    }
}
