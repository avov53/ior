using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace HappBox;

public sealed partial class MainForm
{
    private IorCoreControlClient? _iorControlClient;
    private readonly ProcessLogLimiter _nativeLogLimiter=new();

    private async Task<bool> StartIorCoreAsync(ProxyProfile? overrideProfile = null)
    {
        var profile = overrideProfile ?? SelectedProfile();
        var runId = Diagnostics.Begin("ior-core", _state.TunEnabled ? "tun" : "proxy", profile?.Protocol.ToString() ?? "none");
        var report = new NativeStartReport(Path.Combine(_appDir, "diagnostics", "native-start"), runId, AppVersion,
            Environment.OSVersion.Version.Build, CoreEngineService.NativeBuildId(_state), _state.TunEnabled ? "tun" : "proxy", profile?.Protocol.ToString() ?? "none");
        report.Enter("preflight");
        try
        {
            var connected = await StartIorCoreAttemptAsync(profile, runId, report);
            if (connected) report.Complete();
            return connected;
        }
        catch (Exception error)
        {
            report.Fail(error);
            Diagnostics.Record("start", "NATIVE_START_FAILED", ("errorType", error.GetType().Name));
            Log(report.Persisted ? "ior-core: отчёт запуска сохранён: " + report.Path : "ior-core: не удалось сохранить отчёт запуска.");
            throw;
        }
    }

    private async Task<bool> StartIorCoreAttemptAsync(ProxyProfile? profile, string runId, NativeStartReport report)
    {
        var startupTime = System.Diagnostics.Stopwatch.StartNew();
        var stageTime = System.Diagnostics.Stopwatch.StartNew();
        var stages=new List<string>();
        var longestStage="none"; long longestStageMs=0;
        _state.NativeTunReadiness=null;
        if (profile is null) throw new InvalidOperationException("NATIVE_PROFILE_MISSING: Сначала выберите сервер.");
        void Stage(string stage,string code) {var ms=stageTime.ElapsedMilliseconds;Diagnostics.Record(stage,code,("durationMs",ms.ToString()),("elapsedMs",startupTime.ElapsedMilliseconds.ToString()));if(ms>longestStageMs){longestStage=stage;longestStageMs=ms;}Log($"ior-core: {code} · stage={stage} · durationMs={ms}");stages.Add(stage+"="+ms+"мс");stageTime.Restart();}
        Log($"ior {AppVersion}: ior-core preflight · Windows={Environment.OSVersion.Version.Build} · TUN={_state.TunEnabled} · supported={NativeWindowsPlatform.TunAvailable}");
        if (!CoreEngineService.CanRun(_state, profile, out var reason))
            throw new InvalidOperationException("NATIVE_PREFLIGHT_REJECTED: " + reason);
        if (!CoreRuntimeContract.TryCreateStart(_state, profile!, out var request, out reason))
            throw new InvalidOperationException(reason);
        var fastTunStart=_state.TunEnabled &&
            (profile!.Protocol is ProxyProtocol.Hysteria2 || profile.Protocol == ProxyProtocol.Vless && profile.RealityEnabled);
        NativeTunAddress? tunParameters = null;
        var tunAddress = _state.TunAddress.Contains('/') ? _state.TunAddress : _state.TunAddress + "/30";
        if (_state.TunEnabled)
        {
            report.Enter("addresses");
            tunParameters = NativeTunAddress.Parse(tunAddress);
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "core", "xray", "wintun.dll")))
                throw new IOException("WINTUN_LIBRARY_MISSING");
        }
        Diagnostics.Record("start", "NATIVE_WINDOWS_PLATFORM", ("windowsBuild", Environment.OSVersion.Version.Build.ToString()),
            ("tunSupported", NativeWindowsPlatform.TunAvailable.ToString()));
        using var networkRun=WindowsNetworkCommand.BeginRun(runId);
        request!.RequestId = runId;
        if(profile.RealityEnabled)Diagnostics.Record("profile","REALITY_EFFECTIVE_FLOW",("flow",request.Profile.Flow ?? "none"));
        if(profile.RealityEnabled)Log("ior-core Reality: flow="+(request.Profile.Flow ?? "none")+" · UDP/443 experiment="+_state.RealityUdp443Enabled);
        var buildId=CoreEngineService.NativeBuildId(_state);
        Diagnostics.Record("start","NATIVE_BUILD_SELECTED",("version",buildId??"unknown"),("engine","ior-core"),("mode",_state.TunEnabled?"tun":"proxy"),("protocol",profile.Protocol.ToString()));
        Log($"ior-core: сборка={buildId} · режим={(_state.TunEnabled?"TUN":"Proxy")} · экспериментально");
        using var networkTimings=WindowsNetworkCommand.Observe((operation,duration)=>Diagnostics.Record("network","WINDOWS_NETWORK_COMMAND_COMPLETE",("operation",operation),("durationMs",duration.ToString())));
        Stage("profile","NATIVE_PREFLIGHT_COMPLETE");
        if(profile.Protocol==ProxyProtocol.AmneziaWG)Log("ior-core: AWG_ROUTING_MODE · mode="+request!.Profile.Options.AwgRoutingMode);
        Log($"ior-core: запуск={runId[..8]} · протокол={profile.Protocol} · компонент=" +
            (profile.Protocol == ProxyProtocol.AmneziaWG ? "AWG "+request!.Profile.Options.AwgEngine+" · ior-core" : "ior-core Rust"));

        if(fastTunStart)Log("ior-core TUN: быстрый запуск; DNS и UDP проверяются отдельно, предел 2 с.");
        try
        {
            report.Enter("bootstrap");
            Diagnostics.Record("dns", "BOOTSTRAP_STARTED");
            AdvanceWebOperation("bootstrap");
            var endpoint = profile.Protocol is ProxyProtocol.WireGuard or ProxyProtocol.AmneziaWG
                ? await BootstrapDnsResolver.ResolveEndpointAsync(profile.Server,_state.BootstrapDnsServer)
                : await BootstrapDnsResolver.ResolveIpv4Async(profile!.Server, _state.BootstrapDnsServer);
            request.Profile.Server = endpoint.ToString();
            var physical = PhysicalInterfaceResolver.ForAddress(endpoint);
            request.Network = PhysicalInterfaceResolver.Describe(physical);
            Diagnostics.Record("network", "PHYSICAL_EGRESS_SELECTED", ("interfaceIndex", request.Network.InterfaceIndex.ToString()));
            Stage("dns", "BOOTSTRAP_READY");
            if(profile.Protocol is ProxyProtocol.Vmess or ProxyProtocol.Shadowsocks or ProxyProtocol.Trojan or ProxyProtocol.WireGuard or ProxyProtocol.AmneziaWG)
            {
                await IorCoreControlClient.ValidateProfileAsync(CoreEngineService.IorCorePath(_state),request);
                Stage("profile","NATIVE_PROFILE_VALIDATED_BEFORE_NETWORK");
            }
            if (_state.TunEnabled)
            {
                report.Enter("network");
                await Diagnostics.CaptureNetworkAsync("before-tun");
                Stage("snapshot","NATIVE_NETWORK_SNAPSHOT_COMPLETE");
                AdvanceWebOperation("network");
                _xrayWatchdog = await NetworkWatchdog.StartAsync(_appDir, runId);
                if(WindowsNetworkCommand.WorkerProcessId is { } workerPid)await _xrayWatchdog.OwnNetworkWorkerAsync(workerPid);
                _xrayTunRouteLease = await XrayTunRouteLease.AcquireAsync(_appDir, endpoint,
                    IPAddress.Parse(request.Dns.Bootstrap), physical, runId: runId, allowTemporaryForwardingDisable: true,requireNativeDnsPolicyAvailable:true);
                Stage("egress", "NATIVE_NETWORK_LEASE_READY");
                var name = "ior-native-" + runId[..8];
                if (NetworkInterface.GetAllNetworkInterfaces().Any(item => item.Name == name))
                    throw new IOException("TUN_ADAPTER_NAME_CONFLICT");
                request.Tun = new CoreWireTun { Name = name, Mtu = NativeTunnelPolicy.EffectiveMtu(_state,profile),
                    WintunPath = Path.Combine(AppContext.BaseDirectory, "core", "xray", "wintun.dll") };

            }
            Diagnostics.Record("handshake", "NATIVE_TRANSPORT_STARTING");
            report.Enter("transport");
            AdvanceWebOperation("handshake");
            _iorControlClient = await IorCoreControlClient.StartAsync(CoreEngineService.IorCorePath(_state),
                _state.MixedProxyPort, request);
            Stage("transport","NATIVE_PROCESS_INGRESS_READY");
            _iorControlClient.DiagnosticDetails += (code, fields) =>
            {
                Diagnostics.Record("native",code,fields);
            };
            _iorControlClient.DiagnosticMessage += message =>
            {
                var phase=message.Split(' ')[0];
                if(phase is "REALITY_TCP_CONNECTED" or "REALITY_WORKER_READY" or "REALITY_CLIENT_HELLO_READY" or "REALITY_TLS_COMPLETE") {
                    var duration=System.Text.RegularExpressions.Regex.Match(message,@"durationMs=(\d+)");
                    var elapsed=System.Text.RegularExpressions.Regex.Match(message,@"elapsed=(\d+)ms");
                    Diagnostics.Record("handshake",phase,("durationMs",duration.Success?duration.Groups[1].Value:"unknown"),("elapsedMs",elapsed.Success?elapsed.Groups[1].Value:"unknown"));
                }
                var line="ior-core: "+message;
                var accepted=_nativeLogLimiter.TryAccept(line,out var summary);
                if((accepted || summary is not null) && !IsDisposed && IsHandleCreated)
                    try { BeginInvoke(new Action(()=>{if(summary is not null)Log(summary);if(accepted)Log(line);})); }
                    catch(InvalidOperationException) { /* The form closed while a diagnostic was in flight. */ }
            };
            _process = _iorControlClient.Process;
            var started = _process;
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(async () =>
                {
                    if (!ReferenceEquals(_process, started)) return;
                    Diagnostics.Record("process", "NATIVE_PROCESS_EXITED");
                    await ReleaseXrayTunRoutesAsync();
                    RestoreSystemProxyIfNeeded();
                    SetDisconnected();
                }));
            };
            if (_xrayWatchdog is not null) await _xrayWatchdog.OwnChildAsync(started);
            _state.ActiveProcessId = started.Id;
            _state.RunningCore = CoreKind.IorCore;
            _state.RunningServerId = profile.Id;
            _state.RunningServerName = profile.Name;
            _state.RunningProfileSnapshot = JsonSerializer.Deserialize<ProxyProfile>(JsonSerializer.Serialize(profile));
            _state.ActiveConnectionStartedUtc = DateTime.UtcNow;
            if (request.Tun is { } tun)
            {
                if (request.Routes.Any(rule => rule.Target == "DIRECT"))
                {
                    await _iorControlClient.CheckDirectEgressAsync();
                    Diagnostics.Record("probe", "NATIVE_DIRECT_BEFORE_TUN_OK");
                }
                Diagnostics.Record("tun", "NATIVE_ADAPTER_READY");
                var parameters = tunParameters!;
                report.Enter("routesDns");
                await _xrayTunRouteLease!.ConfigureNativeTunAsync(tun.Name, tunAddress, parameters.Dns,mtu:tun.Mtu);
                Stage("tun", "NATIVE_ROUTES_DNS_APPLIED");
                if (request.Routes.Any(rule => rule.Target == "DIRECT"))
                {
                    await _iorControlClient.CheckDirectEgressAsync();
                    Diagnostics.Record("probe", "NATIVE_DIRECT_AFTER_TUN_OK");
                }
                AdvanceWebOperation("probe");
                report.Enter("httpsProbe");
                var probe = await NativeTunHealthProbe.CheckAsync(endpoint, physical, tun.Name, parameters.Dns,dnsPolicyAlreadyVerified:true,
                    ipv6Only:request.Profile.Options.LocalAddresses is {Length:>0} addresses && addresses.All(value=>value.Contains(':')),
                    measured:(stage,attempt,ms,result)=>Diagnostics.Record("probe","NATIVE_TRAFFIC_PROBE_COMPLETE",("phase",stage),("count",attempt.ToString()),("durationMs",ms.ToString()),("errorType",result)),
                    proxyTrafficConfirmed:_iorControlClient.HasProxiedTraffic,
                    trafficDnsEndpoint:IPAddress.Parse(request.Dns.Upstream),
                    fastStart:fastTunStart,
                    readiness:result=>_state.NativeTunReadiness=result,proxyDnsConfirmed:_iorControlClient.HasProxiedDnsTraffic,
                    proxyPacketTrafficConfirmed:_iorControlClient.HasProxiedPacketTraffic);
                if (!probe.Ready) throw new IOException("NATIVE_TUN_TRAFFIC_FAILED: " + probe.Reason);
                Stage("probe", _state.NativeTunReadiness is null?"NATIVE_TUN_HTTPS_UDP_OK":"NATIVE_TUN_HTTPS_READY");
            }
            else
            {
                AdvanceWebOperation("probe");
                using var handler = new HttpClientHandler { Proxy = new WebProxy("127.0.0.1", _state.MixedProxyPort), UseProxy = true };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                using var response = await http.GetAsync("https://example.com/");
                response.EnsureSuccessStatusCode();
                Diagnostics.Record("probe", "NATIVE_PROXY_HTTPS_OK");
                if (_iorControlClient.Ready.Udp)
                {
                    await NativeProxyHealthProbe.CheckUdpAsync(_state.MixedProxyPort);
                    Diagnostics.Record("probe", "NATIVE_PROXY_UDP_OK");
                }
            }
            ApplySystemProxyIfNeeded();
            SetConnected(); SaveState();
            Diagnostics.Record("ready", "NATIVE_CONNECTION_READY", ("durationMs", startupTime.ElapsedMilliseconds.ToString()),("slowStage",longestStage));
            if(startupTime.ElapsedMilliseconds>3000)Diagnostics.Record("ready","NATIVE_START_TARGET_EXCEEDED",("slowStage",longestStage),("durationMs",longestStageMs.ToString()));
            Log((_state.TunEnabled ? _state.NativeTunReadiness is null ? "ior-core TUN: HTTPS и UDP проверены." : "ior-core TUN: HTTPS проверен, быстрый запуск." : "ior-core Proxy: HTTPS проверен.") +
                $" Подключение за {startupTime.Elapsed.TotalSeconds:0.0} с.");
            if(_state.NativeTunReadiness is { } ready)
                Log($"ior-core: DNS={ready.Dns.Status} · {ready.Dns.Code} · {ready.Dns.DurationMs}мс; UDP={ready.Udp.Status} · {ready.Udp.Code} · {ready.Udp.DurationMs}мс");
            Log("ior-core: запуск "+runId[..8]+" · "+profile.Protocol+" · сборка="+buildId+" · "+string.Join("; ",stages));
            return true;
        }
        catch (Exception error)
        {
            var primaryCode=_iorControlClient?.DiagnosticCodes.Split(", ").LastOrDefault(code=>code.StartsWith("REALITY_",StringComparison.Ordinal) && code != "REALITY_TCP_CONNECTED" && code != "REALITY_TLS_COMPLETE");
            if (_iorControlClient?.DiagnosticCodes.Contains("TUN_EGRESS_RECAPTURED", StringComparison.Ordinal) == true)
                primaryCode = "TUN_EGRESS_RECAPTURED";
            if(_iorControlClient is { } failedClient)
            {
                report.RecordNativeCodes(failedClient.DiagnosticCodes);
                Log("ior-core diagnostics: "+failedClient.DiagnosticCodes);
            }
            Diagnostics.Record("start", "NATIVE_START_FAILED", ("errorType", error.GetType().Name));
            if (_iorControlClient is { } client) {
                _iorControlClient = null;
                if(ReferenceEquals(_process,client.Process))_process=null;
                await client.DisposeAsync();
            }
            try { await ReleaseXrayTunRoutesAsync(); }
            catch (Exception rollbackError)
            {
                Log("NATIVE_ROLLBACK_PENDING: " + ConnectionDiagnostics.Redact(rollbackError.Message, DiagnosticSecrets()));
                Diagnostics.Record("rollback", "NATIVE_ROLLBACK_PENDING", ("errorType", rollbackError.GetType().Name));
            }
            RestoreSystemProxyIfNeeded(); SetDisconnected();
            throw new IOException(primaryCode is not null ? "ior-core: "+primaryCode : ConnectionDiagnostics.Redact(error.Message, DiagnosticSecrets()), error);
        }
    }
}
