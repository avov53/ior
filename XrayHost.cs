using System.Diagnostics;
using System.Net;

namespace HappBox;

public sealed partial class MainForm
{
    private XrayTunRouteLease? _xrayTunRouteLease;
    private DirectEgressHelper? _xrayDirectHelper;
    private NetworkWatchdog? _xrayWatchdog;
    private readonly SemaphoreSlim _xrayCleanupGate = new(1, 1);
    private int _xrayNetworkRecoveryScheduled;

    private void RegisterXrayNetworkRecovery()
    {
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnXrayNetworkAddressChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnXrayPowerModeChanged;
    }

    private void UnregisterXrayNetworkRecovery()
    {
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnXrayNetworkAddressChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnXrayPowerModeChanged;
    }

    private void OnXrayNetworkAddressChanged(object? sender, EventArgs e){if(_waitingForSleepNetwork&&IsHandleCreated&&!IsDisposed){BeginInvoke(new Action(async()=>await RestoreAfterSleepAsync()));return;}ScheduleXrayNetworkRecovery(force:false);}

    private void OnXrayPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if(e.Mode==Microsoft.Win32.PowerModes.Suspend){_sleepProfile=_state.RestoreAfterSleep&&IsConnectionRunning()?(_state.RunningProfileSnapshot??SelectedProfile()):null;_sleepCore=_state.RunningCore;}
        if(e.Mode==Microsoft.Win32.PowerModes.Resume&&IsHandleCreated&&!IsDisposed)BeginInvoke(new Action(async()=>await RestoreAfterSleepAsync()));
    }

    private void ScheduleXrayNetworkRecovery(bool force)
    {
        if (_sleepProfile is not null || _recoveringSleep || _state.RunningCore is not (CoreKind.XrayCore or CoreKind.IorCore) || !_state.TunEnabled || _xrayTunRouteLease is null ||
            !IsHandleCreated || IsDisposed || Interlocked.Exchange(ref _xrayNetworkRecoveryScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(1200);
            if (IsDisposed || !IsHandleCreated) { Interlocked.Exchange(ref _xrayNetworkRecoveryScheduled, 0); return; }
            BeginInvoke(new Action(async () =>
            {
                try
                {
                    var lease = _xrayTunRouteLease;
                    if (_sleepProfile is not null || _recoveringSleep || _state.RunningCore is not (CoreKind.XrayCore or CoreKind.IorCore) || !_state.TunEnabled || lease is null ||
                        _connectedAt is null || _connectionTransitionInProgress ||
                        (!force && lease.IsPhysicalLinkUnchanged())) return;
                    _connectionTransitionInProgress = true;
                    _powerButton.Enabled = false;
                    var profile = _state.RunningProfileSnapshot ?? SelectedProfile();
                    var coreLabel = _state.RunningCore == CoreKind.IorCore ? "ior-core" : "Xray";
                    Diagnostics.Record("network", force ? "POWER_RESUME_RECONNECT" : "PHYSICAL_LINK_RECONNECT");
                    Log(coreLabel + " TUN: физическая сеть изменилась; переподключение с новыми маршрутами.");
                    if (!await StopSingBoxAsync()) return;
                    await Task.Delay(600);
                    if (profile is null || !await StartSingBoxAsync(profile))
                        Log(coreLabel + " TUN не удалось восстановить после изменения сети.");
                }
                catch (Exception ex) { Log($"Xray TUN network recovery failed: {ex.Message}"); }
                finally
                {
                    _connectionTransitionInProgress = false;
                    _powerButton.Enabled = true;
                    Interlocked.Exchange(ref _xrayNetworkRecoveryScheduled, 0);
                }
            }));
        });
    }

    private async Task<bool> StartXrayAsync(ProxyProfile? overrideProfile = null)
    {
        Log($"ior {AppVersion}: Xray preflight · Windows={Environment.OSVersion.Version.Build} · TUN={_state.TunEnabled} · supported={NativeWindowsPlatform.TunAvailable}");
        var profile = overrideProfile ?? SelectedProfile();
        if (!CoreEngineService.CanRun(_state, profile, out var reason))
        {
            Log(reason);
            MessageBox.Show(reason, "Xray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (_process is { HasExited: false }) return true;

        var runId = Diagnostics.Begin("xray", _state.TunEnabled ? "tun" : "proxy", profile!.Protocol.ToString());
        Interlocked.Exchange(ref _tunLoopStopScheduled, 0);
        Log($"Xray diagnostic run: {runId}.");

        var binary = CoreEngineService.XrayCorePath(_state);
        var configPath = Path.Combine(_appDir, "config.xray.generated.json");
        try
        {
            SaveSettings();
            var selectedRoute = _state.HappRoutingProfiles.FirstOrDefault(item => item.Id == _state.SelectedHappRoutingProfileId);
            if (selectedRoute is not null && OrderedRoutePlan.TryCreate(selectedRoute, out var routeRules, out _))
                foreach (var rule in routeRules)
                    Diagnostics.Record("routing", "RULE_COMPILED", ("ruleId", rule.Id), ("target", rule.Target));
            IPAddress? endpoint = null;
            string? physicalInterface = null;
            var allowForwardingChange = false;
            string config;
            if (_state.TunEnabled)
            {
                await Diagnostics.CaptureNetworkAsync("before-tun");
                endpoint = await BootstrapDnsResolver.ResolveIpv4Async(profile!.Server, _state.BootstrapDnsServer);
                physicalInterface = PhysicalInterfaceResolver.ForIpv4(endpoint);
                if (await XrayTunRouteLease.PhysicalForwardingEnabledAsync(physicalInterface))
                {
                    Diagnostics.Record("egress", "PHYSICAL_FORWARDING_ENABLED", ("interface", physicalInterface));
                    // Temporary and journalled: Acquire records the original value before
                    // changing this adapter; Release/the watchdog restore it after exit.
                    allowForwardingChange = true;
                    Log("Xray TUN: переадресация IPv4 будет временно отключена на физическом интерфейсе и восстановлена при остановке. Раздача интернета может быть недоступна во время VPN.");
                    Diagnostics.Record("egress", "PHYSICAL_FORWARDING_AUTOMATIC_RESTORE_PLANNED", ("interface", physicalInterface));
                }
                Diagnostics.Record("dns", "BOOTSTRAP_OK", ("address", endpoint.ToString()));
                Diagnostics.Record("egress", "PHYSICAL_INTERFACE_SELECTED", ("interface", physicalInterface));
                {
                    _xrayDirectHelper = DirectEgressHelper.ForPhysicalInterface(physicalInterface, _state.BootstrapDnsServer, Diagnostics);
                    await _xrayDirectHelper.VerifyPhysicalEgressAsync("before-tun");
                }
                config = XrayConfigBuilder.BuildTunCandidateForValidation(_state, profile, endpoint.ToString(), physicalInterface, _xrayDirectHelper);
                Log($"Xray TUN prepared on physical interface: {physicalInterface}.");
            }
            else config = XrayConfigBuilder.Build(_state, profile!);
            await File.WriteAllTextAsync(configPath, config);
            Diagnostics.Record("config", "CONFIG_GENERATED");
            var check = await CheckXrayConfigAsync(binary, configPath);
            if (!check.Valid)
            {
                Diagnostics.Record("config", "CONFIG_REJECTED");
                Log($"Xray config rejected: {check.Output}");
                MessageBox.Show(check.Output, "Проверка Xray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                await ReleaseXrayTunRoutesAsync();
                return false;
            }
            Diagnostics.Record("config", "CONFIG_CHECK_OK");

            if (_state.TunEnabled)
            {
                _xrayWatchdog = await NetworkWatchdog.StartAsync(_appDir, runId);
                await WindowsNetworkCommand.WarmupAsync();
                if (WindowsNetworkCommand.WorkerProcessId is int networkWorkerId)
                    await _xrayWatchdog.OwnNetworkWorkerAsync(networkWorkerId);
                _xrayTunRouteLease = await XrayTunRouteLease.AcquireAsync(_appDir, endpoint!,
                    BootstrapDnsResolver.ServerAddress(_state.BootstrapDnsServer), physicalInterface!, runId: runId,
                    allowTemporaryForwardingDisable: allowForwardingChange);
                Diagnostics.Record("egress", "PHYSICAL_FORWARDING_SAFE", ("interface", physicalInterface!));
            }

            var start = new ProcessStartInfo(binary)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(binary)!
            };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(configPath);
            _process = new Process { StartInfo = start, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) => LogFromProcess(e.Data);
            _process.ErrorDataReceived += (_, e) => LogFromProcess(e.Data);
            var started = _process;
            _process.Exited += (_, _) => BeginInvoke(new Action(async () =>
            {
                if (!ReferenceEquals(_process, started)) return;
                Log($"Xray exited with code {TryGetExitCode(started)}.");
                Diagnostics.Record("process", "PROCESS_EXITED", ("exitCode", TryGetExitCode(started).ToString()));
                try { await ReleaseXrayTunRoutesAsync(); }
                catch (Exception error) { Log($"Xray TUN route cleanup failed: {error.Message}"); }
                RestoreSystemProxyIfNeeded();
                SetDisconnected();
            }));
            _process.Start();
            if (_xrayWatchdog is not null) await _xrayWatchdog.OwnChildAsync(_process);
            Diagnostics.Record("process", "PROCESS_STARTED");
            _state.ActiveProcessId = _process.Id;
            _state.RunningCore = CoreKind.XrayCore;
            _state.RunningServerId = profile!.Id;
            _state.RunningServerName = profile.Name;
            _state.RunningProfileSnapshot = System.Text.Json.JsonSerializer.Deserialize<ProxyProfile>(System.Text.Json.JsonSerializer.Serialize(profile));
            _state.ActiveConnectionStartedUtc = DateTime.UtcNow;
            SaveState();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            if (!await Task.Run(() => WaitForSingBoxStartup(started, SingBoxStartupTimeoutMs)))
            {
                if (!started.HasExited) { started.Kill(entireProcessTree: true); await started.WaitForExitAsync(); }
                Log("Xray did not open the local proxy port.");
                Diagnostics.Record("probe", "LOCAL_PROXY_TIMEOUT");
                await ReleaseXrayTunRoutesAsync();
                SetDisconnected();
                return false;
            }
            if (_state.TunEnabled)
            {
                if (_xrayDirectHelper is not null) await _xrayDirectHelper.VerifyPhysicalEgressAsync("after-tun");
                await Diagnostics.CaptureNetworkAsync("after-tun");
                var probe = await XrayTunHealthProbe.CheckAsync(endpoint!, physicalInterface!);
                Diagnostics.Record("probe", probe.Ready ? "TUN_TRAFFIC_OK" : "TUN_TRAFFIC_FAILED");
                if (!probe.Ready)
                {
                    Log(probe.Reason);
                    if (!started.HasExited) { started.Kill(entireProcessTree: true); await started.WaitForExitAsync(); }
                    await ReleaseXrayTunRoutesAsync();
                    SetDisconnected();
                    MessageBox.Show(probe.Reason, "Xray TUN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
            ApplySystemProxyIfNeeded();
            SetConnected();
            Diagnostics.Record("ready", "CONNECTION_READY");
            Log(_state.TunEnabled ? "Xray TUN started; HTTPS and UDP probes passed." : "Xray started in Proxy mode.");
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Record("start", "START_FAILED", ("errorType", ex.GetType().Name),
                ("socketError", ex is System.Net.Sockets.SocketException socketError ? socketError.SocketErrorCode.ToString() : "none"));
            Log($"Xray start failed: {ex.Message}");
            try
            {
                if (_process is { HasExited: false } failed)
                {
                    failed.Kill(entireProcessTree: true);
                    await failed.WaitForExitAsync();
                }
            }
            catch (Exception cleanupEx) { Log($"Xray cleanup failed: {cleanupEx.Message}"); }
            try { await ReleaseXrayTunRoutesAsync(); }
            catch (Exception cleanupEx) { Log($"Xray TUN route cleanup failed: {cleanupEx.Message}"); }
            MessageBox.Show(ConnectionDiagnostics.Redact(ex.Message, DiagnosticSecrets()) + $"\n\nКод: START_FAILED\nЗапуск: {runId}", "Xray", MessageBoxButtons.OK, MessageBoxIcon.Error);
            RestoreSystemProxyIfNeeded();
            SetDisconnected();
            return false;
        }
    }

    private async Task ReleaseXrayTunRoutesAsync()
    {
        await _xrayCleanupGate.WaitAsync();
        try
        {
        var lease = _xrayTunRouteLease;
        var helper = _xrayDirectHelper;
        _xrayDirectHelper = null;
        if (helper is not null) await helper.DisposeAsync();
        if (lease is not null)
        {
            Diagnostics.Record("rollback", "ROLLBACK_STARTED");
            await lease.ReleaseAsync();
            Diagnostics.Record("rollback", "ROLLBACK_COMPLETE");
            await Diagnostics.CaptureNetworkAsync("after-rollback");
            if (ReferenceEquals(_xrayTunRouteLease, lease)) _xrayTunRouteLease = null;
        }
        var watchdog = _xrayWatchdog;
        if (watchdog is not null)
        {
            await watchdog.DisposeAsync();
            if (ReferenceEquals(_xrayWatchdog, watchdog)) _xrayWatchdog = null;
        }
        }
        finally { _xrayCleanupGate.Release(); }
    }

    private static async Task<(bool Valid, string Output)> CheckXrayConfigAsync(string binary, string configPath)
    {
        var start = new ProcessStartInfo(binary)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(binary)!
        };
        start.ArgumentList.Add("run"); start.ArgumentList.Add("-test");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add(configPath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить Xray для проверки.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return (false, "Время проверки Xray истекло.");
        }
        var output = (await stdout + Environment.NewLine + await stderr).Trim();
        return (process.ExitCode == 0, output.Length == 0
            ? (process.ExitCode == 0 ? "Конфигурация корректна." : "Xray отклонил конфигурацию.")
            : output);
    }
}
