using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HappBox;
using Xunit;
namespace HappBox.Tests;
public sealed class YoutubeLiveProbeTests
{
    [Fact]public async Task OptInBoundedProxyChecksDoNotChangeSystemNetwork()
    {
        var source=Environment.GetEnvironmentVariable("IOR_YOUTUBE_PROBE_STATE");
        if(source is null)return;
        var binary=Environment.GetEnvironmentVariable("IOR_YOUTUBE_PROBE_BINARY")!;
        var output=Environment.GetEnvironmentVariable("IOR_YOUTUBE_PROBE_OUTPUT")!;
        var original=JsonSerializer.Deserialize<AppState>(await File.ReadAllTextAsync(source))!;
        var rows=new List<object>();
        foreach(var protocol in new[]{ProxyProtocol.AmneziaWG,ProxyProtocol.Hysteria2})
        {
            var selected=Environment.GetEnvironmentVariable("IOR_YOUTUBE_PROBE_PROTOCOL");
            if(!string.IsNullOrEmpty(selected)&&selected!=protocol.ToString())continue;
            if(protocol==ProxyProtocol.AmneziaWG)
            {
                rows.Add(new{protocol=protocol.ToString(),status="notProbed",reason="AWG_TUN_REQUIRED"});
                continue; // Never create a second system TUN merely to run a diagnostic probe.
            }
            var profile=original.Profiles.FirstOrDefault(p=>p.Protocol==protocol);if(profile is null)continue;
            try{
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(35));
                var state=new AppState{TunEnabled=false,AwgEngine=AwgEngine.Rust};
                Assert.True(CoreRuntimeContract.TryCreateStart(state,profile,out var request,out var reason),reason);
                request!.Routes=[new(){Kind="MATCH",Target="PROXY"}];
                var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
                await using var core=await IorCoreControlClient.StartAsync(binary,port,request,deadline.Token);
                foreach(var (host,path) in new[]{("www.youtube.com","/generate_204"),("yt3.ggpht.com","/"),("redirector.googlevideo.com","/"),("www.cloudflare.com","/cdn-cgi/trace")})
                {
                    var watch=Stopwatch.StartNew();
                    try{
                        using var limit=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);limit.CancelAfter(TimeSpan.FromSeconds(6));
                        using var handler=new SocketsHttpHandler{Proxy=new WebProxy($"socks5://127.0.0.1:{port}"),UseProxy=true,AllowAutoRedirect=false};
                        using var client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
                        using var response=await client.GetAsync("https://"+host+path,HttpCompletionOption.ResponseHeadersRead,limit.Token);
                        string? exit=null;
                        if(host=="www.cloudflare.com"){
                            using var stream=await response.Content.ReadAsStreamAsync(limit.Token);var buffer=new byte[4096];var n=await stream.ReadAsync(buffer,limit.Token);
                            var ip=Encoding.UTF8.GetString(buffer,0,n).Split('\n').FirstOrDefault(l=>l.StartsWith("ip="));
                            if(ip is not null)exit=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ip)))[..16];
                        }
                        rows.Add(new{protocol=protocol.ToString(),host,status=(int)response.StatusCode,durationMs=watch.ElapsedMilliseconds,exitFingerprint=exit});
                    }catch(Exception e){rows.Add(new{protocol=protocol.ToString(),host,errorType=e.GetType().Name,durationMs=watch.ElapsedMilliseconds});}
                }
            }catch(Exception e){rows.Add(new{protocol=protocol.ToString(),stage="start",errorType=e.GetType().Name});}
        }
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(rows));
    }
}
