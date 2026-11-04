using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace HappBox;

// Explicit test: private configurations stay in memory/stdin. No TUN or route changes.
public static class NativeHysteriaUploadComparison
{
    public static async Task RunAsync(string report,string? h3Helper=null)
    {
        var samples=new List<object>();
        try {
            var state=JsonSerializer.Deserialize<AppState>(await File.ReadAllTextAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"ior","state.json")))??throw new IOException("STATE_UNAVAILABLE");
            var profile=state.RunningProfileSnapshot?.Protocol==ProxyProtocol.Hysteria2?state.RunningProfileSnapshot:
                state.Profiles.FirstOrDefault(p=>p.Id==state.SelectedProfileId&&p.Protocol==ProxyProtocol.Hysteria2)??state.Profiles.First(p=>p.Protocol==ProxyProtocol.Hysteria2);
            var ip=await BootstrapDnsResolver.ResolveEndpointAsync(profile.Server,state.BootstrapDnsServer);
            var physical=PhysicalInterfaceResolver.ForAddress(ip);var network=PhysicalInterfaceResolver.Describe(physical);
            profile.ServerName??=profile.Server;profile.Server=ip.ToString();
            state.TunEnabled=false;state.ExperimentalCoreTesting=true;state.SelectedHappRoutingProfileId=null;
            foreach(var engine in new[]{"ior-core","xray"}){
                var port=Port();
                IorCoreControlClient? native=null;Process? xray=null;Task? stdout=null,stderr=null;
                try {
                    if(engine=="ior-core"){
                        state.SelectedCore=CoreKind.IorCore;
                        if(!CoreRuntimeContract.TryCreateStart(state,profile,out var request,out _))throw new IOException("PROFILE_NOT_SUPPORTED");
                        request!.Network=network;request.Tun=null;request.Routes=[new CoreWireRule{Kind="MATCH",Value="",Target="PROXY",Source="comparison"}];
                        native=await IorCoreControlClient.StartAsync(Path.Combine(AppContext.BaseDirectory,"core","ior-core","ior-core.exe"),port,request);
                    }else{
                        state.MixedProxyPort=port;
                        var config=JsonNode.Parse(XrayConfigBuilder.Build(state,profile))!;
                        config["log"]=new JsonObject{["loglevel"]="none"};
                        config["outbounds"]![0]!["streamSettings"]!["sockopt"]=new JsonObject{["interface"]=physical};
                        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"core","xray","xray.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
                        start.ArgumentList.Add("run");start.ArgumentList.Add("-config");start.ArgumentList.Add("stdin:");start.ArgumentList.Add("-format");start.ArgumentList.Add("json");
                        xray=Process.Start(start)??throw new IOException("XRAY_START_FAILED");stdout=xray.StandardOutput.ReadToEndAsync();stderr=xray.StandardError.ReadToEndAsync();
                        await xray.StandardInput.WriteAsync(config.ToJsonString());xray.StandardInput.Close();
                        using var ready=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        while(true){try{using var socket=new TcpClient();await socket.ConnectAsync(IPAddress.Loopback,port,ready.Token);break;}catch(SocketException){await Task.Delay(100,ready.Token);}}
                    }
                    using var handler=new HttpClientHandler{Proxy=new WebProxy($"http://127.0.0.1:{port}"),UseProxy=true,UseCookies=false};
                    using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(90)};
                    using(var warm=await client.GetAsync("https://speed.cloudflare.com/__down?bytes=1024"))warm.EnsureSuccessStatusCode();
                    for(var repetition=1;repetition<=3;repetition++){
                        async Task<(HttpResponseMessage Response,long Milliseconds)> Upload(){var timer=Stopwatch.StartNew();var response=await client.PostAsync("https://speed.cloudflare.com/__up",new ByteArrayContent(Enumerable.Repeat((byte)0x5a,8*1024*1024).ToArray()));return(response,timer.ElapsedMilliseconds);}
                        var upload=Upload();await Task.Delay(200);var smallTimer=Stopwatch.StartNew();using(var small=await client.GetAsync("https://speed.cloudflare.com/__down?bytes=1024"))small.EnsureSuccessStatusCode();var smallMs=smallTimer.ElapsedMilliseconds;
                        var done=await upload;using var response=done.Response;response.EnsureSuccessStatusCode();samples.Add(new{engine,repetition,protocol="HTTP/1.1 TCP",bytes=8*1024*1024,elapsedMs=done.Milliseconds,smallRequestMs=smallMs,status=(int)response.StatusCode});
                    }
                    var helper=h3Helper;
                    if(helper is not null&&File.Exists(helper)){
                        // This is the uploaded resource, not the VPN endpoint:
                        // its system route may correctly point into the active TUN.
                        var target=await BootstrapDnsResolver.ResolveIpv4Async("speed.cloudflare.com",state.BootstrapDnsServer);
                        var probeStart=new ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};probeStart.ArgumentList.Add((engine=="ior-core"?port:port+1).ToString());probeStart.ArgumentList.Add(target.ToString());
                        using var probe=Process.Start(probeStart)??throw new IOException("H3_PROBE_START_FAILED");var resultText=probe.StandardOutput.ReadToEndAsync();var diagnosticText=probe.StandardError.ReadToEndAsync();
                        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(100));try{await probe.WaitForExitAsync(limit.Token);}catch(OperationCanceledException){probe.Kill(true);await probe.WaitForExitAsync();}
                        using var result=JsonDocument.Parse(await resultText);await diagnosticText;samples.Add(new{engine,protocol="HTTP/3 UDP",result=result.RootElement.Clone()});
                    }
                }finally{
                    if(native is not null)await native.DisposeAsync();
                    if(xray is not null){if(!xray.HasExited)xray.Kill(true);await xray.WaitForExitAsync();if(stdout is not null)await stdout;if(stderr is not null)await stderr;xray.Dispose();}
                }
            }
            await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{scope="same Hysteria profile, physical egress, loopback HTTP proxy; no TUN, Google account, cookies or system changes",samples},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception error){await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{samples,error=error.GetType().Name,comparisonComplete=false}));}
    }
    private static int Port(){for(var i=0;i<20;i++){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;try{var second=new TcpListener(IPAddress.Loopback,port+1);second.Start();second.Stop();return port;}catch(SocketException){}finally{listener.Stop();}}throw new IOException("LOCAL_PORT_UNAVAILABLE");}
}
