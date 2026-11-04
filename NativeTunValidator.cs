using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace HappBox;

/// <summary>Live qualification uses the packaged native engine and the same owned network lease as connection startup.</summary>
public static class NativeTunValidator
{
    public sealed record Progress(string Stage, string Reason, DateTime Utc);
    private static string ValidationRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ior", "diagnostics", "native-tun-validation");
    public static bool IsRunning()
    {
        var benchmark=Path.Combine(ValidationRoot,"benchmark.lock");
        if(File.Exists(benchmark))try{using var check=File.Open(benchmark,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);}catch(IOException){return true;}catch(UnauthorizedAccessException){return true;}
        var path = Path.Combine(ValidationRoot, "validation.lock");
        if (!File.Exists(path)) return false;
        try { using var handle = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return false; }
        catch (FileNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
    public static string DirectoryFor(string runId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ior", "diagnostics", "native-tun-validation", runId);
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static async Task RunFastChecksPackagedAsync(string runId,int repetitions=1)
    {
        var directory=DirectoryFor(runId);Directory.CreateDirectory(directory);
        void Update(string stage,string reason="")=>File.WriteAllText(Path.Combine(directory,"progress.json"),JsonSerializer.Serialize(new Progress(stage,reason,DateTime.UtcNow)));
        AppState? state=null;
        try
        {
            if(!IsAdministrator())throw new InvalidOperationException("ADMINISTRATOR_REQUIRED");
            state=JsonSerializer.Deserialize<AppState>(await File.ReadAllTextAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"ior","state.json")))??throw new IOException("STATE_UNAVAILABLE");
            repetitions=Math.Clamp(repetitions,1,5);
            for(var repetition=0;repetition<repetitions;repetition++)
            foreach(var protocol in new[]{ProxyProtocol.Hysteria2})
            {
                var profile=state.Profiles.FirstOrDefault(item=>item.Protocol==protocol)??throw new IOException("TEST_PROFILE_MISSING");
                await ValidateAsync(state,profile,Environment.GetEnvironmentVariable("IOR_NATIVE_TEST_BINARY")??CoreEngineService.IorCorePath(state),Path.Combine(directory,protocol.ToString(),repetition.ToString()),
                    Environment.ProcessPath!,stage=>Update(protocol+":"+stage),fastStartProbeOnly:true);
            }
            Update("passed","HTTPS и откат проверены; DNS/UDP имеют отдельные результаты.");
        }
        catch(Exception error)
        {
            var secrets=state?.Profiles.SelectMany(p=>new[]{p.Password,p.UserId,p.PublicKey,p.ShortId,p.ObfsPassword,p.PrivateKey,p.PreSharedKey})??[];
            Update("failed",ConnectionDiagnostics.Redact(error.Message,secrets));Environment.ExitCode=1;
        }
        finally {await WindowsNetworkCommand.ShutdownAsync();}
    }

    public static async Task RunPackagedAsync(string runId)
    {
        var directory = DirectoryFor(runId);
        Directory.CreateDirectory(directory);
        void Update(string stage, string reason = "") => File.WriteAllText(Path.Combine(directory, "progress.json"),
            JsonSerializer.Serialize(new Progress(stage, reason, DateTime.UtcNow)));
        Update("preflight");
        AppState? state = null;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ior", "state.json");
            state = JsonSerializer.Deserialize<AppState>(await File.ReadAllTextAsync(path)) ?? throw new IOException("STATE_UNAVAILABLE");
            var profile = state.Profiles.FirstOrDefault(item => item.Id == state.SelectedProfileId);
            if (Environment.GetEnvironmentVariable("IOR_NATIVE_REALITY_TEST") == "1")
                profile = state.Profiles.FirstOrDefault(item => item.Protocol == ProxyProtocol.Vless && item.RealityEnabled);
            if (Environment.GetEnvironmentVariable("IOR_NATIVE_HYSTERIA_TEST") == "1")
                profile = state.Profiles.FirstOrDefault(item => item.Protocol == ProxyProtocol.Hysteria2);
            if (profile is null || !Supported(profile)) throw new InvalidOperationException("SELECT_SUPPORTED_PROFILE");
            await ValidateAsync(state, profile, Environment.GetEnvironmentVariable("IOR_NATIVE_TEST_BINARY") ?? CoreEngineService.IorCorePath(state), directory,
                Environment.ProcessPath!, stage => Update(stage));
            Update("passed", "Базовая проверка трафика и отката пройдена.");
        }
        catch (Exception error)
        {
            var code = System.Text.RegularExpressions.Regex.Match(error.Message, "^[A-Z][A-Z0-9_]{3,63}").Value;
            var secrets = state?.Profiles.SelectMany(profile => new[] { profile.UserId, profile.Password, profile.PublicKey,
                profile.PrivateKey, profile.PeerPublicKey, profile.PreSharedKey, profile.ShortId, profile.ObfsPassword })
                .Concat(state.Subscriptions.Select(item => item.Url));
            var detail = ConnectionDiagnostics.Redact(error.Message, secrets);
            if (detail.Length > 512) detail = detail[..512];
            Update("failed", string.IsNullOrEmpty(code) ? "NATIVE_TUN_CHECK_FAILED: " + error.GetType().Name + " · " + detail : detail);
            Environment.ExitCode = 2;
        }
    }

    public static async Task ValidateAsync(AppState state, ProxyProfile profile, string binary, string directory,
        string watchdogExecutable, Action<string>? progress = null, Action<string,long,bool>? measured = null, bool performance = false,bool legacy=false,bool fastStartProbeOnly=false,
        bool prepareAddresses=true,bool measurePages=false)
    {
        if (!NativeWindowsPlatform.TunAvailable) throw new InvalidOperationException("WINDOWS_10_1607_OR_NEWER_REQUIRED");
        if (!Supported(profile)) throw new InvalidOperationException("SELECT_SUPPORTED_PROFILE");
        if (!CoreRuntimeContract.TryCreateStart(state, profile, out _, out var reason)) throw new InvalidOperationException(reason);
        if (!IsAdministrator()) throw new InvalidOperationException("ADMINISTRATOR_REQUIRED");
        if (!File.Exists(binary)) throw new IOException("NATIVE_BINARY_MISSING");
        Directory.CreateDirectory(ValidationRoot);
        FileStream validationLock;
        try { validationLock = File.Open(Path.Combine(ValidationRoot, "validation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("NATIVE_VALIDATION_ALREADY_RUNNING"); }
        using var exclusiveValidation = validationLock;
        foreach (var name in new[] { "ior-core", "xray", "sing-box" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length != 0) throw new InvalidOperationException("STOP_ACTIVE_VPN_BEFORE_VALIDATION"); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        await XrayTunRouteLease.EnsureNativeDnsPolicyAvailableAsync();
        Directory.CreateDirectory(directory);
        using var diagnostics = new ConnectionDiagnostics(Path.Combine(directory, "diagnostics"));
        using var networkTimings=WindowsNetworkCommand.Observe((operation,duration)=>diagnostics.Record("network","WINDOWS_NETWORK_COMMAND_COMPLETE",("operation",operation),("durationMs",duration.ToString())));
        var verified = new HashSet<string>(StringComparer.Ordinal);
        // Qualification applies a controlled route so each target can be proved independently.
        // The selected route above must still compile before any network changes.
        var controlled = JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(state))!;
        controlled.TunEnabled = true;
        controlled.RoutingBypassSites.Clear(); controlled.RoutingProxySites.Clear();
        var route = ClashSubscriptionParser.Parse("""
            proxies:
              - {name: reference, type: hysteria2, server: example.com, port: 443, password: reference, sni: example.com}
            rules:
              - DOMAIN,blocked.ior.invalid,REJECT
              - DOMAIN,example.com,DIRECT
              - MATCH,PROXY
            """).Routing!;
        controlled.HappRoutingProfiles.Add(route); controlled.SelectedHappRoutingProfileId = route.Id;
        for (var cycle = 0; cycle < (performance ? 1 : 2); cycle++)
        {
            var startup=Stopwatch.StartNew();
            var stageTime=Stopwatch.StartNew();
            var timings=new ConcurrentQueue<object>();
            void Stage(string stage){timings.Enqueue(new {phase=stage,durationMs=stageTime.ElapsedMilliseconds,result="OK"});measured?.Invoke(stage,stageTime.ElapsedMilliseconds,false);stageTime.Restart();}
            var crash = !performance && cycle == 1;
            progress?.Invoke(crash ? "crash-check" : "starting");
            diagnostics.Begin("ior-core", "tun", profile.Protocol.ToString());
            if (!CoreRuntimeContract.TryCreateStart(controlled, profile, out var request, out reason)) throw new InvalidOperationException(reason);
            var endpoint = profile.Protocol is ProxyProtocol.WireGuard
                ? await BootstrapDnsResolver.ResolveEndpointAsync(profile.Server,controlled.BootstrapDnsServer)
                : await BootstrapDnsResolver.ResolveIpv4Async(profile.Server, controlled.BootstrapDnsServer);
            request!.Profile.Server = endpoint.ToString();
            using var networkRun=WindowsNetworkCommand.BeginRun(request.RequestId);
            var physical = PhysicalInterfaceResolver.ForAddress(endpoint);
            var adapter = NetworkInterface.GetAllNetworkInterfaces().Single(item => item.Name == physical);
            var index = adapter.GetIPProperties().GetIPv4Properties()!.Index;
            request.Network = PhysicalInterfaceResolver.Describe(physical);
            Stage("bootstrap");
            if(profile.Protocol is ProxyProtocol.WireGuard) {
                await IorCoreControlClient.ValidateProfileAsync(binary,request);
                Stage("profile_validation");
            }
            var name = "ior-native-" + request.RequestId[..8];
            request.Tun = new CoreWireTun { Name = name, Mtu = NativeTunnelPolicy.EffectiveMtu(controlled,profile),
                WintunPath = Path.Combine(Path.GetDirectoryName(watchdogExecutable)!, "core", "xray", "wintun.dll") };
            if (!File.Exists(request.Tun.WintunPath)) throw new IOException("WINTUN_LIBRARY_MISSING");
            var address = NativeTunAddress.Parse("172.29.91.1/30");
            var before = await NetworkDiagnosticSnapshot.CaptureAsync("before-validation");
            var beforeProxy = ProxySnapshot();
            var cycleDirectory = Path.Combine(directory, request.RequestId);
            var messages = new ConcurrentQueue<string>();
            IorCoreControlClient? core = null; XrayTunRouteLease? lease = null; NetworkWatchdog? watchdog = null;
            var exitedRollback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                watchdog = await NetworkWatchdog.StartAsync(cycleDirectory, request.RequestId, watchdogExecutable);
                if(WindowsNetworkCommand.WorkerProcessId is { } workerPid)await watchdog.OwnNetworkWorkerAsync(workerPid);
                lease = await XrayTunRouteLease.AcquireAsync(cycleDirectory, endpoint, IPAddress.Parse(request.Dns.Bootstrap), physical,
                    runId: request.RequestId, allowTemporaryForwardingDisable: true,requireNativeDnsPolicyAvailable:true);
                Stage("network_preparation");
                core = await IorCoreControlClient.StartAsync(binary, FreePort(), request);
                Stage("transport_start");
                var firstTimings=new HashSet<string>();
                core.DiagnosticMessage += message =>
                {
                    messages.Enqueue(message); while (messages.Count > 2048) messages.TryDequeue(out _);
                    diagnostics.Record("native", "FLOW_TRACE", ("phase", message));
                    var code=message.Split(' ')[0];
                    var stage=code switch {"HYSTERIA2_HANDSHAKE_AUTHENTICATED"=>"authenticated_handshake","TUN_ADAPTER_CREATED"=>"adapter_creation",_=>null};
                    if(stage is not null && firstTimings.Add(stage)) {
                        var duration=System.Text.RegularExpressions.Regex.Match(message,@"durationMs=(\d+)");
                        if(duration.Success && long.TryParse(duration.Groups[1].Value,out var ms))measured?.Invoke(stage,ms,false);
                    }
                };
                core.Diagnostic += code => diagnostics.Record("native", code);
                await watchdog.OwnChildAsync(core.Process);
                var ownedLease = lease;
                core.Process.EnableRaisingEvents = true;
                core.Process.Exited += async (_, _) =>
                {
                    try { await ownedLease.ReleaseAsync(); exitedRollback.TrySetResult(); }
                    catch (Exception error) { exitedRollback.TrySetException(error); }
                };
                await core.CheckDirectEgressAsync();
                Stage("direct_preflight");
                await lease.ConfigureNativeTunAsync(name, "172.29.91.1/30", address.Dns,mtu:request.Tun.Mtu,prepareAddresses:prepareAddresses);
                Stage("routes_dns");
                await core.CheckDirectEgressAsync();
                Stage("direct_postflight");
                NativeTunReadinessResult? fastReadiness=null;
                var health = await NativeTunHealthProbe.CheckAsync(endpoint, physical, name, address.Dns,dnsPolicyAlreadyVerified:true,
                    measured:(phase,attempt,ms,result)=>{timings.Enqueue(new {phase,attempt,durationMs=ms,result});diagnostics.Record("probe","NATIVE_TRAFFIC_PROBE_COMPLETE",("phase",phase),("count",attempt.ToString()),("durationMs",ms.ToString()),("errorType",result));if(result is "OK" or "DNS_OK" or "UDP_OK")measured?.Invoke(phase,ms,false);},
                    proxyTrafficConfirmed:legacy?core.HasProxyRoute:core.HasProxiedTraffic,hedged:!legacy,
                    trafficDnsEndpoint:IPAddress.Parse(request.Dns.Upstream),fastStart:fastStartProbeOnly,readiness:value=>fastReadiness=value,proxyDnsConfirmed:core.HasProxiedDnsTraffic);
                if (!health.Ready) throw new IOException("NATIVE_TUN_TRAFFIC_FAILED: " + health.Reason);
                measured?.Invoke("ready",startup.ElapsedMilliseconds,false);
                if(fastStartProbeOnly)
                {
                    verified.Add("tcp");
                    if(fastReadiness?.Dns.Status=="ready")verified.Add("dns");
                    if(fastReadiness?.Udp.Status=="ready")verified.Add("udpDns");
                    await File.WriteAllTextAsync(Path.Combine(directory,$"fast-start-{cycle}.json"),JsonSerializer.Serialize(new {protocol=profile.Protocol.ToString(),readyMilliseconds=startup.ElapsedMilliseconds,readiness=fastReadiness,crash}));
                    if(measurePages)await NativeTunBenchmark.MeasurePagesAsync(measured!,core.HasProxiedTraffic);
                }
                else
                {
                verified.UnionWith(["tcp", "dns", "direct"]);
                using (var proxyDns = new UdpClient(AddressFamily.InterNetwork))
                {
                    var dnsTime=Stopwatch.StartNew();
                    proxyDns.Connect(address.Dns, 53);
                    byte[] query = [0x43,0x21,1,0,0,1,0,0,0,0,0,0,10,(byte)'c',(byte)'l',(byte)'o',(byte)'u',(byte)'d',(byte)'f',(byte)'l',(byte)'a',(byte)'r',(byte)'e',3,(byte)'c',(byte)'o',(byte)'m',0,0,1,0,1];
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await proxyDns.SendAsync(query, deadline.Token); var answer = await proxyDns.ReceiveAsync(deadline.Token);
                    Require(answer.Buffer.Length >= 12 && answer.Buffer[0] == 0x43 && answer.Buffer[1] == 0x21 && (answer.Buffer[2] & 0x80) != 0 &&
                        (answer.Buffer[3] & 15) == 0 && (answer.Buffer[6] != 0 || answer.Buffer[7] != 0), "PROXY_UDP_DNS_FAILED");
                    Require(messages.Any(item => item.Contains("cloudflare.com:53", StringComparison.Ordinal) && item.Contains("→ PROXY", StringComparison.Ordinal) &&
                        item.Contains("dns-udp", StringComparison.Ordinal)), "PROXY_UDP_ROUTE_NOT_OBSERVED");
                    measured?.Invoke("dns",dnsTime.ElapsedMilliseconds,false);
                }
                verified.Add("udp");
                Require(messages.Any(item => item.Contains("→ PROXY", StringComparison.Ordinal) && item.Contains("stage=routing", StringComparison.Ordinal)), "PROXY_ROUTE_NOT_OBSERVED");
                verified.Add("proxy");
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
                var directProbeTime=System.Diagnostics.Stopwatch.StartNew();
                try {
                    using var direct = await http.GetAsync("https://example.com/"); direct.EnsureSuccessStatusCode();
                    diagnostics.Record("probe","DIRECT_HTTPS_VERIFIED",("durationMs",directProbeTime.ElapsedMilliseconds.ToString()));
                    measured?.Invoke("direct_https",directProbeTime.ElapsedMilliseconds,false);
                } catch (Exception error) when(error is HttpRequestException or OperationCanceledException or IOException) {
                    diagnostics.Record("probe","DIRECT_HTTPS_FAILED",("durationMs",directProbeTime.ElapsedMilliseconds.ToString()),("errorType",error.GetType().Name));
                    throw new IOException("NATIVE_DIRECT_HTTPS_PROBE_FAILED",error);
                }
                Require(messages.Any(item => item.Contains("example.com:443", StringComparison.Ordinal) && item.Contains("→ DIRECT", StringComparison.Ordinal)), "DIRECT_ROUTE_NOT_OBSERVED");
                using (var dns = new UdpClient(AddressFamily.InterNetwork))
                {
                    dns.Connect(address.Dns, 53);
                    byte[] query = [0x56,0x78,1,0,0,1,0,0,0,0,0,0,7,(byte)'b',(byte)'l',(byte)'o',(byte)'c',(byte)'k',(byte)'e',(byte)'d',3,(byte)'i',(byte)'o',(byte)'r',7,(byte)'i',(byte)'n',(byte)'v',(byte)'a',(byte)'l',(byte)'i',(byte)'d',0,0,1,0,1];
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await dns.SendAsync(query, deadline.Token); var denied = await dns.ReceiveAsync(deadline.Token);
                    Require(denied.Buffer.Length >= 12 && (denied.Buffer[3] & 15) == 5 && core.DiagnosticCodes.Contains("DNS_RULE_REJECTED"), "REJECT_ROUTE_NOT_OBSERVED");
                }
                verified.Add("reject");
                using (var gateway = new ClientWebSocket())
                {
                    var websocketTime=Stopwatch.StartNew();
                    gateway.Options.Proxy = null;
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    await gateway.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=10&encoding=json"), deadline.Token);
                    using var data = new MemoryStream(); var buffer = new byte[8192]; WebSocketReceiveResult message;
                    do { message = await gateway.ReceiveAsync(buffer, deadline.Token); data.Write(buffer, 0, message.Count); Require(data.Length <= 65536, "WEBSOCKET_HELLO_TOO_LARGE"); }
                    while (!message.EndOfMessage);
                    using var hello = JsonDocument.Parse(data.ToArray());
                    Require(hello.RootElement.GetProperty("op").GetInt32() == 10, "WEBSOCKET_HELLO_INVALID"); gateway.Abort();
                    measured?.Invoke("discord_websocket",websocketTime.ElapsedMilliseconds,false);
                }
                if(performance)await NativeTunBenchmark.MeasurePagesAsync(measured!);
                verified.Add("websocket");
                await CheckUdp443Async(profile.Flow ?? "", messages);
                verified.Add("udp443Flow");
                }
                Require(!core.DiagnosticCodes.Contains("TUN_EGRESS_RECAPTURED"), "TUN_EGRESS_RECAPTURED");
                progress?.Invoke(crash ? "crash-rollback" : "connected");
                if (crash)
                {
                    core.Process.Kill(entireProcessTree: true);
                    await core.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    await exitedRollback.Task.WaitAsync(TimeSpan.FromSeconds(45));
                    Require(!File.Exists(XrayTunRouteLease.JournalPath(cycleDirectory)), "CRASH_ROLLBACK_INCOMPLETE");
                }
            }
            finally
            {
                progress?.Invoke("rollback");
                var shutdownTime=System.Diagnostics.Stopwatch.StartNew();
                try { if (core is not null) await core.DisposeAsync(); }
                finally
                {
                    diagnostics.Record("rollback","NATIVE_PROCESS_STOP_COMPLETE",("durationMs",shutdownTime.ElapsedMilliseconds.ToString()));
                    timings.Enqueue(new {phase="process_stop",durationMs=shutdownTime.ElapsedMilliseconds,result="OK"});
                    measured?.Invoke("process_stop",shutdownTime.ElapsedMilliseconds,false);
                    shutdownTime.Restart();
                    // Keep the watchdog armed if restoring the journal fails.
                    if (lease is not null) await lease.ReleaseAsync();
                    if (watchdog is not null) await watchdog.DisposeAsync();
                    diagnostics.Record("rollback","NATIVE_NETWORK_RESTORE_COMPLETE",("durationMs",shutdownTime.ElapsedMilliseconds.ToString()));
                    timings.Enqueue(new {phase="network_restore",durationMs=shutdownTime.ElapsedMilliseconds,result="OK"});
                    measured?.Invoke("network_restore",shutdownTime.ElapsedMilliseconds,false);
                }
            }
            Require(!File.Exists(XrayTunRouteLease.JournalPath(cycleDirectory)), "ROLLBACK_JOURNAL_REMAINS");
            Require(!NetworkInterface.GetAllNetworkInterfaces().Any(item => item.Name == name && item.OperationalStatus == OperationalStatus.Up), "TUN_ADAPTER_REMAINS_ACTIVE");
            await XrayTunRouteLease.EnsureNativeDnsPolicyAvailableAsync();
            var after = await NetworkDiagnosticSnapshot.CaptureAsync("after-validation");
            Require(NetworkRestored(before, after, index) && beforeProxy == ProxySnapshot(), "NETWORK_ROLLBACK_MISMATCH");
            verified.Add(crash ? "crashRollback" : "stopRollback");
            if(fastStartProbeOnly)await File.WriteAllTextAsync(Path.Combine(directory,$"timings-{cycle}.json"),JsonSerializer.Serialize(timings.ToArray()));
            diagnostics.Record("validation", crash ? "CRASH_ROLLBACK_VERIFIED" : "STOP_ROLLBACK_VERIFIED");
        }
        if (profile.Protocol == ProxyProtocol.Vless && !performance)
        {
            NativeTunQualification.Record(binary, profile.Flow ?? "", verified);
            Require(NativeTunQualification.CanRun(binary, profile.Flow ?? "", out _), "QUALIFICATION_REPORT_INCOMPLETE");
        }
        else await File.WriteAllTextAsync(Path.Combine(directory,profile.Protocol.ToString().ToLowerInvariant()+"-result.json"), JsonSerializer.Serialize(new { protocol=profile.Protocol.ToString(), verified=verified.Order().ToArray() }));
    }
    private static bool Supported(ProxyProfile profile) => profile.Protocol is ProxyProtocol.Hysteria2 || (profile.Protocol == ProxyProtocol.Vless && profile.RealityEnabled);

    public static bool NetworkRestored(NetworkDiagnosticSnapshot before, NetworkDiagnosticSnapshot after, int physicalIndex)
    {
        static string[] Routes(NetworkDiagnosticSnapshot value, int index) => value.Routes.Where(item => item.InterfaceIndex == index)
            .Select(item => $"{item.DestinationPrefix}|{item.NextHop}|{item.RouteMetric}").Order(StringComparer.Ordinal).ToArray();
        static string[] Dns(NetworkDiagnosticSnapshot value, int index) => value.DnsServers.Where(item => item.InterfaceIndex == index)
            .SelectMany(item => item.ServerAddresses).Order(StringComparer.Ordinal).ToArray();
        return Routes(before, physicalIndex).SequenceEqual(Routes(after, physicalIndex)) && Dns(before, physicalIndex).SequenceEqual(Dns(after, physicalIndex)) &&
            before.Interfaces?.SingleOrDefault(item => item.InterfaceIndex == physicalIndex) == after.Interfaces?.SingleOrDefault(item => item.InterfaceIndex == physicalIndex);
    }
    public static string ProxySnapshot()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        return JsonSerializer.Serialize(new[] { key?.GetValue("ProxyEnable"), key?.GetValue("ProxyServer"), key?.GetValue("ProxyOverride") });
    }
    private static void Require(bool condition, string code) { if (!condition) throw new IOException(code); }
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static async Task CheckUdp443Async(string flow, ConcurrentQueue<string> messages)
    {
        if (flow == "xtls-rprx-vision")
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork); udp.Connect("1.0.0.1", 443); await udp.SendAsync(new byte[] { 1, 2, 3, 4 });
            for (var attempt = 0; attempt < 30 && !messages.Any(item => item.Contains("VISION_UDP443_BLOCKED", StringComparison.Ordinal)); attempt++) await Task.Delay(100);
            Require(messages.Any(item => item.Contains("VISION_UDP443_BLOCKED", StringComparison.Ordinal)), "VISION_UDP443_POLICY_UNVERIFIED");
            return;
        }
#pragma warning disable CA2252
        if (!System.Net.Quic.QuicConnection.IsSupported) throw new InvalidOperationException("HTTP3_PROBE_UNAVAILABLE");
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
        using var query = new HttpRequestMessage(HttpMethod.Get, "https://www.cloudflare.com/cdn-cgi/trace")
            { Version = new Version(3, 0), VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        using var response = await http.SendAsync(query); response.EnsureSuccessStatusCode(); Require(response.Version.Major == 3, "HTTP3_PROBE_FAILED");
#pragma warning restore CA2252
    }
}
