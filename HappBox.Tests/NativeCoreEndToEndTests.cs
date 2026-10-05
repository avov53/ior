using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class NativeCoreEndToEndTests
{
    [Fact]
    public async Task OptInNativeHysteriaProxyCarriesHttpsAndSocksUdp()
    {
        if (Environment.GetEnvironmentVariable("IOR_LIVE_NATIVE_PROXY") != "1") return;
        var (state,profile,binary)=ReadInputs();
        state.TunEnabled=false;state.SelectedHappRoutingProfileId=null;
        if(Environment.GetEnvironmentVariable("IOR_NATIVE_ROUTING_TEST")=="1")
        {
            var route=ClashSubscriptionParser.Parse("""
                proxies:
                  - {name: synthetic, type: hysteria2, server: example.com, port: 443, password: synthetic, sni: example.com}
                rules:
                  - DOMAIN,blocked.ior.invalid,REJECT
                  - GEOIP,ru,DIRECT,no-resolve
                  - GEOSITE,category-ru,DIRECT
                  - MATCH,PROXY
                """).Routing!;
            state.HappRoutingProfiles.Add(route);state.SelectedHappRoutingProfileId=route.Id;
        }
        Assert.True(CoreRuntimeContract.TryCreateStart(state,profile,out var request,out var reason),reason);
        var port=FreePort();
        await using var client=await IorCoreControlClient.StartAsync(binary,port,request!);
        Assert.Equal(profile.Protocol==ProxyProtocol.Hysteria2 || profile.RealityEnabled,client.Ready.Udp);
        var localProxy=Environment.GetEnvironmentVariable("IOR_NATIVE_SOCKS4_TEST")=="1"
            ? new WebProxy(new Uri($"socks4a://127.0.0.1:{port}")) : new WebProxy("127.0.0.1",port);
        using var http=new HttpClient(new HttpClientHandler{UseProxy=true,Proxy=localProxy}){Timeout=TimeSpan.FromSeconds(15)};
        HttpResponseMessage response;
        try { response=await http.GetAsync("https://example.com/"); }
        catch(Exception error) { throw new IOException(client.DiagnosticCodes,error); }
        using var ownedResponse=response;
        response.EnsureSuccessStatusCode();
        if(client.Ready.Udp)await NativeProxyHealthProbe.CheckUdpAsync(port);
        if(Environment.GetEnvironmentVariable("IOR_NATIVE_DISCORD_TEST")=="1")
        {
            using var gateway=new System.Net.WebSockets.ClientWebSocket();
            gateway.Options.Proxy=localProxy;
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await gateway.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=10&encoding=json"),deadline.Token);
            var data=new byte[16384];
            var message=await gateway.ReceiveAsync(data,deadline.Token);
            using var hello=JsonDocument.Parse(data.AsMemory(0,message.Count));
            Assert.Equal(10,hello.RootElement.GetProperty("op").GetInt32());
            gateway.Abort();
        }
    }

    [Fact]
    public async Task OptInNativeTunCarriesTrafficAndRestoresRoutes()
    {
        if (Environment.GetEnvironmentVariable("IOR_LIVE_NATIVE_TUN") != "1") return;
        var inputs = ReadInputs();
        var validationDirectory = Path.Combine(Path.GetTempPath(), "ior-native-validation-" + Guid.NewGuid().ToString("N"));
        await NativeTunValidator.ValidateAsync(inputs.Item1, inputs.Item2, inputs.Item3, validationDirectory,
            Path.Combine(Environment.GetEnvironmentVariable("IOR_PROJECT_ROOT")!, "bin", "Debug", "net8.0-windows", "ior.exe"), Progress);
        Assert.True(NativeTunQualification.CanRun(inputs.Item3, inputs.Item2.Flow ?? "", out var validationReason), validationReason);
        Progress("stopped");
    }

    // Retained for optional extended load experiments; this does not qualify baseline rollback.
    [Fact]
    public async Task OptInExtendedNativeTunTrafficExperiment()
    {
        if (Environment.GetEnvironmentVariable("IOR_LIVE_NATIVE_TUN_EXTENDED") != "1") return;
        Assert.Empty(Process.GetProcessesByName("xray"));
        Assert.Empty(Process.GetProcessesByName("sing-box"));
        Assert.Empty(Process.GetProcessesByName("ior-core"));
        var (state,profile,binary)=ReadInputs();
        state.TunEnabled=true;state.SelectedHappRoutingProfileId=null;
        if(Environment.GetEnvironmentVariable("IOR_NATIVE_TUN_ROUTING_TEST")=="1")
        {
            var route=ClashSubscriptionParser.Parse("""
                proxies:
                  - {name: synthetic, type: hysteria2, server: example.com, port: 443, password: synthetic, sni: example.com}
                rules:
                  - DOMAIN,blocked.ior.invalid,REJECT
                  - DOMAIN,example.com,DIRECT
                  - IP-CIDR,127.0.0.0/8,DIRECT,no-resolve
                  - MATCH,PROXY
                """).Routing!;
            state.HappRoutingProfiles.Add(route);state.SelectedHappRoutingProfileId=route.Id;
        }
        Assert.True(CoreRuntimeContract.TryCreateStart(state,profile,out var request,out var reason),reason);
        var endpoint=await BootstrapDnsResolver.ResolveIpv4Async(profile.Server,state.BootstrapDnsServer);
        request!.Profile.Server=endpoint.ToString();
        var physical=PhysicalInterfaceResolver.ForIpv4(endpoint);
        var adapter=NetworkInterface.GetAllNetworkInterfaces().First(item=>item.Name==physical);
        request.Network=new CoreWireNetwork { PhysicalAddress=adapter.GetIPProperties().UnicastAddresses.First(item=>item.Address.AddressFamily==AddressFamily.InterNetwork).Address.ToString(),
            InterfaceIndex=(uint)adapter.GetIPProperties().GetIPv4Properties()!.Index };
        var nativeName="ior-native-"+request.RequestId[..8];
        request.Tun=new CoreWireTun{Name=nativeName,Mtu=1400,WintunPath=Path.Combine(Environment.GetEnvironmentVariable("IOR_PROJECT_ROOT")!,"core","xray","wintun.dll")};
        var directory=Path.Combine(Path.GetTempPath(),"ior-native-test-"+request.RequestId);
        Directory.CreateDirectory(directory);
        var beforeForwarding=await XrayTunRouteLease.PhysicalForwardingEnabledAsync(physical);
        Progress("preflight");
        var verified=new HashSet<string>(StringComparer.Ordinal);
        var messages=new System.Collections.Concurrent.ConcurrentQueue<string>();
        IorCoreControlClient? core=null;XrayTunRouteLease? lease=null;NetworkWatchdog? watchdog=null;
        try
        {
            Progress("starting");
            watchdog=await NetworkWatchdog.StartAsync(directory,request.RequestId,
                Path.Combine(Environment.GetEnvironmentVariable("IOR_PROJECT_ROOT")!,"bin","Debug","net8.0-windows","ior.exe"));
            lease=await XrayTunRouteLease.AcquireAsync(directory,endpoint,IPAddress.Parse(request.Dns.Bootstrap),physical,
                runId:request.RequestId,allowTemporaryForwardingDisable:true);
            core=await IorCoreControlClient.StartAsync(binary,FreePort(),request);
            core.DiagnosticMessage+=message=>{messages.Enqueue(message);while(messages.Count>1024)messages.TryDequeue(out _);};
            await watchdog.OwnChildAsync(core.Process);
            await core.CheckDirectEgressAsync();
            var parameters=NativeTunAddress.Parse("172.29.91.1/30");
            await lease.ConfigureNativeTunAsync(nativeName,"172.29.91.1/30",parameters.Dns);
            await core.CheckDirectEgressAsync();
            var result=await NativeTunHealthProbe.CheckAsync(endpoint,physical,nativeName,parameters.Dns);
            Assert.True(result.Ready,result.Reason);
            Progress("connected");
            verified.UnionWith(["tcp","dns","udp","direct"]);
            if(Environment.GetEnvironmentVariable("IOR_NATIVE_TUN_ROUTING_TEST")=="1")
            {
                using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(15)};
                using var direct=await http.GetAsync("https://example.com/");direct.EnsureSuccessStatusCode();
                Assert.Contains(messages,message=>message.Contains("example.com:443",StringComparison.Ordinal) && message.Contains("→ DIRECT",StringComparison.Ordinal));
                using var dns=new UdpClient(AddressFamily.InterNetwork);dns.Connect(parameters.Dns,53);
                byte[] query=[0x56,0x78,1,0,0,1,0,0,0,0,0,0,7,(byte)'b',(byte)'l',(byte)'o',(byte)'c',(byte)'k',(byte)'e',(byte)'d',3,(byte)'i',(byte)'o',(byte)'r',7,(byte)'i',(byte)'n',(byte)'v',(byte)'a',(byte)'l',(byte)'i',(byte)'d',0,0,1,0,1];
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));await dns.SendAsync(query,deadline.Token);
                var denied=await dns.ReceiveAsync(deadline.Token);Assert.Equal(5,denied.Buffer[3]&15);
                Assert.Contains("DNS_RULE_REJECTED",core.DiagnosticCodes);
                verified.Add("reject");
            }
            if(Environment.GetEnvironmentVariable("IOR_NATIVE_DISCORD_TEST")=="1")
            {
                using var gateway=new System.Net.WebSockets.ClientWebSocket();
                gateway.Options.Proxy=null;
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));
                await gateway.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=10&encoding=json"),deadline.Token);
                var data=new byte[16384];var message=await gateway.ReceiveAsync(data,deadline.Token);
                using var hello=JsonDocument.Parse(data.AsMemory(0,message.Count));
                Assert.Equal(10,hello.RootElement.GetProperty("op").GetInt32());gateway.Abort();
                verified.Add("websocket");
            }
            if(profile.Flow=="xtls-rprx-vision")
            {
                using var quic=new UdpClient(AddressFamily.InterNetwork);quic.Connect("1.0.0.1",443);
                await quic.SendAsync(new byte[]{1,2,3,4});
                for(var attempt=0;attempt<20 && !messages.Any(message=>message.Contains("VISION_UDP443_BLOCKED",StringComparison.Ordinal));attempt++)await Task.Delay(100);
                Assert.Contains(messages,message=>message.Contains("VISION_UDP443_BLOCKED",StringComparison.Ordinal));
                verified.Add("udp443Flow");
            }
#pragma warning disable CA2252 // QUIC is used only by the explicitly enabled .NET 8 live qualification.
            else if(System.Net.Quic.QuicConnection.IsSupported)
            {
                using var http3=new HttpClient(new SocketsHttpHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(20)};
                using var query=new HttpRequestMessage(HttpMethod.Get,"https://www.cloudflare.com/cdn-cgi/trace")
                {Version=new Version(3,0),VersionPolicy=HttpVersionPolicy.RequestVersionExact};
                using var response=await http3.SendAsync(query);response.EnsureSuccessStatusCode();Assert.Equal(3,response.Version.Major);
                verified.Add("udp443Flow");
            }
#pragma warning restore CA2252
            if(int.TryParse(Environment.GetEnvironmentVariable("IOR_NATIVE_TUN_LOAD_MINUTES"),out var minutes) && minutes>0)
            {
                using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(15)};
                var until=DateTime.UtcNow.AddMinutes(minutes);var rounds=0;
                while(DateTime.UtcNow<until)
                {
                    foreach(var url in new[]{"https://www.cloudflare.com/cdn-cgi/trace","https://discord.com/api/v10/gateway","https://www.youtube.com/"})
                    {using var response=await http.GetAsync(url);response.EnsureSuccessStatusCode();}
                    if(rounds%10==0){var health=await NativeTunHealthProbe.CheckAsync(endpoint,physical,nativeName,parameters.Dns);Assert.True(health.Ready,health.Reason);}
                    rounds++;await Task.Delay(TimeSpan.FromSeconds(5));
                }
                Assert.True(rounds>0);
                if(minutes>=30)verified.Add("load30");
            }
            if(Environment.GetEnvironmentVariable("IOR_NATIVE_TUN_CRASH_TEST")=="1")
            {core.Process.Kill(entireProcessTree:true);await core.Process.WaitForExitAsync();}
        }
        finally
        {
            Progress("rollback");
            if(core is not null)await core.DisposeAsync();
            if(lease is not null)await lease.ReleaseAsync();
            if(watchdog is not null)await watchdog.DisposeAsync();
        }
        Assert.False(File.Exists(XrayTunRouteLease.JournalPath(directory)));
        Assert.Equal(beforeForwarding,await XrayTunRouteLease.PhysicalForwardingEnabledAsync(physical));
        Assert.DoesNotContain(NetworkInterface.GetAllNetworkInterfaces(),item=>item.Name==nativeName && item.OperationalStatus==OperationalStatus.Up);
        verified.Add("stopRollback");
        if(Environment.GetEnvironmentVariable("IOR_NATIVE_TUN_CRASH_TEST")=="1")verified.Add("crashRollback");
        if(profile.RealityEnabled)NativeTunQualification.Record(binary,profile.Flow ?? "",verified.Intersect(NativeTunQualification.ExtendedChecks));
        Progress("stopped");
    }
    private static void Progress(string stage)
    {
        if(Environment.GetEnvironmentVariable("IOR_NATIVE_TUN_PROGRESS_PATH") is {Length:>0} path)
            File.WriteAllText(path,JsonSerializer.Serialize(new {stage,utc=DateTime.UtcNow}));
    }
    private static (AppState,ProxyProfile,string) ReadInputs()
    {
        var state=JsonSerializer.Deserialize<AppState>(File.ReadAllText(Environment.GetEnvironmentVariable("IOR_USER_STATE_TEST_PATH")!))!;
        var reality=Environment.GetEnvironmentVariable("IOR_NATIVE_REALITY_TEST")=="1";
        var profile=state.Profiles.First(item=>reality
            ? item.Protocol==ProxyProtocol.Vless && item.RealityEnabled
            : item.Protocol==ProxyProtocol.Hysteria2 && !item.AllowInsecure && string.IsNullOrWhiteSpace(item.ObfsType));
        return(state,profile,Environment.GetEnvironmentVariable("IOR_NATIVE_TEST_BINARY")!);
    }
    private static int FreePort(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
}
