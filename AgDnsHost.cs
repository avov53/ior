namespace HappBox;
public sealed partial class MainForm
{
    private AgDnsSuspension? _agDns;
    private int _agLifecycleDepth;
    private CancellationToken _agStartToken;
    private bool _agPreserveForReconnect;
    private System.Windows.Forms.Timer? _agDnsTimer;
    private static bool AgDnsEnabled=>OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build is >=14393 and <22000;
    private AgDnsSuspension AgDns
    {
        get
        {
            if(_agDns is not null)return _agDns;
            _agDns=new AgDnsSuspension(_appDir,new AgDnsWindowsBackend());
            _agDns.Notice+=message=>{if(IsHandleCreated&&!IsDisposed)BeginInvoke(new Action(()=>{Log(message);PushWebSnapshot();}));};
            _agDnsTimer=new System.Windows.Forms.Timer{Interval=10000};
            _agDnsTimer.Tick+=async(_,_)=>{if(_agLifecycleDepth==0&&IsConnectionRunning())try{await _agDns.MonitorAsync();}catch(Exception e){Log("AG_DNS_MONITOR_FAILED: "+e.GetType().Name);}};
            _agDnsTimer.Start();
            Disposed+=(_,_)=>_agDnsTimer.Dispose();
            return _agDns;
        }
    }
    private async Task<bool> StartSingBoxAsync(ProxyProfile? overrideProfile=null)
    {
        if(!AgDnsEnabled)return await StartConnectionCoreAsync(overrideProfile);
        Interlocked.Increment(ref _agLifecycleDepth);
        var success=false;
        _agPreserveForReconnect=false;
        _agStartToken=_backgroundConnectionCancellation.Token;
        try
        {
            if(AgDns.Status=="recoveryRequired")throw new IOException("AG_DNS_RECOVERY_REQUIRED: сначала восстановите DNS-правила из сохранённого журнала.");
            if(_state.TunEnabled && _state.SelectedCore is CoreKind.IorCore or CoreKind.XrayCore)
            {
                await AgDns.SuspendAsync();
                if(AgDns.Status=="suspended")await XrayTunRouteLease.EnsureNativeDnsPolicyAvailableAsync();
            }
            else if(AgDns.Status=="suspended")await AgDns.RestoreAsync();
            _agStartToken.ThrowIfCancellationRequested();
            success=await StartConnectionCoreAsync(overrideProfile);
            return success;
        }
        catch(Exception error){Log("AG_DNS_START_FAILED: "+error.Message);MessageBox.Show(this,error.Message,"DNS / VPN",MessageBoxButtons.OK,MessageBoxIcon.Warning);return false;}
        finally
        {
            Interlocked.Decrement(ref _agLifecycleDepth);
            if(!success&&_agDns is not null)try{await _agDns.RestoreAsync();}catch(Exception e){Log(e.Message);}
            if(!success)PruneTransientFiles();
        }
    }
    private async Task<bool> StopSingBoxAsync(bool preserveAgDns=false)
    {
        if(!AgDnsEnabled)return await StopConnectionCoreAsync();
        Interlocked.Increment(ref _agLifecycleDepth);
        _agPreserveForReconnect=preserveAgDns;
        try
        {
            var stopped=await StopConnectionCoreAsync();
            if(stopped&&!preserveAgDns&&_agDns is not null)await _agDns.RestoreAsync();
            return stopped;
        }
        catch(Exception e){Log("AG_DNS_RECOVERY_REQUIRED: "+e.Message);return false;}
        finally{Interlocked.Decrement(ref _agLifecycleDepth);if(!preserveAgDns)PruneTransientFiles();}
    }
    private async Task RestoreAgDnsAfterExitAsync()
    {
        if(AgDnsEnabled&&_agLifecycleDepth==0&&!_agPreserveForReconnect&&!IsConnectionRunning()&&_agDns is not null){await _agDns.RestoreAsync();PruneTransientFiles();}
    }
    private async Task CaptureWin10AdapterFailureAsync()
    {
        try
        {
            var report=await WindowsNetworkCommand.RunIsolatedAsync("""
$since=(Get-Date).AddMinutes(-5)
$adapters=@(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object {$_.Name -like 'ior-*'} | Select-Object Name,InterfaceGuid,ifIndex,Status,InterfaceDescription)
$events=@(Get-WinEvent -FilterHashtable @{LogName='System';StartTime=$since} -MaxEvents 200 -ErrorAction SilentlyContinue | Where-Object {$_.ProviderName -match 'Kernel-PnP|UserPnp'} | Select-Object -First 20 TimeCreated,Id,ProviderName,LevelDisplayName,Message)
@{Windows=[Environment]::OSVersion.Version.ToString();Adapters=$adapters;Events=$events} | ConvertTo-Json -Depth 5
""");
            await File.WriteAllTextAsync(Path.Combine(_appDir,"win10-tun-failure.json"),report);
        }
        catch(Exception e){Log("TUN_DIAGNOSTIC_FAILED: "+e.GetType().Name);}
    }
}
