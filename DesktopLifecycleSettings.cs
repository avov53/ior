using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
namespace HappBox;

public sealed partial class MainForm
{
    private string _windowsStartupStatus="Выключено";
    private CancellationTokenSource _backgroundConnectionCancellation=new();
    private ProxyProfile? _sleepProfile;
    private CoreKind? _sleepCore;
    private bool _recoveringSleep;
    private bool _waitingForSleepNetwork;
    private void CancelBackgroundConnectionActions(){_backgroundConnectionCancellation.Cancel();_backgroundConnectionCancellation=new();_sleepProfile=null;_waitingForSleepNetwork=false;}
    private async Task SaveLifecycleSettingsAsync(JsonElement input)
    {
        var enabled=input.GetProperty("launchWithWindows").GetBoolean();var connect=input.GetProperty("connectAtWindowsStartup").GetBoolean();var id=input.TryGetProperty("windowsStartupProfileId",out var selected)?selected.GetString():null;
        if(enabled&&connect&&!_state.Profiles.Any(p=>p.Id==id))throw new InvalidOperationException("Выберите существующий сервер для автоподключения.");
        await WindowsStartup.ConfigureAsync(enabled);
        _state.LaunchWithWindows=enabled;_state.ConnectAtWindowsStartup=enabled&&connect;_state.WindowsStartupProfileId=id;_state.RestoreAfterSleep=input.GetProperty("restoreAfterSleep").GetBoolean();SaveState();
        _windowsStartupStatus=enabled?await WindowsStartup.StatusAsync():"Выключено";PushWebSnapshot();
    }
    private async Task RunWindowsStartupAsync()
    {
        if(!Program.WindowsAutostart||!_state.LaunchWithWindows||!_state.ConnectAtWindowsStartup)return;
        var token=_backgroundConnectionCancellation.Token;
        try{
            var profile=_state.Profiles.FirstOrDefault(p=>p.Id==_state.WindowsStartupProfileId);
            if(profile is null){Log("Автоподключение: сохранённый сервер удалён. Не подключено.");return;}
            if(!await WaitForPhysicalNetworkAsync(token)){Log("Автоподключение: нет доступной сети. Не подключено.");return;}
            token.ThrowIfCancellationRequested();if(IsConnectionRunning())return;
            _state.SelectedProfileId=profile.Id;SaveState();
            var connected=await ToggleConnectionAsync(fromStartup:true);if(token.IsCancellationRequested&&connected){await StopSingBoxAsync();return;}if(!connected)Log("Автоподключение не выполнено. Выбранный конфиг недоступен.");
        }catch(OperationCanceledException){}catch(Exception error){Log("Автоподключение не выполнено: "+ConnectionDiagnostics.Redact(error.Message));}
    }
    private static async Task<bool> WaitForPhysicalNetworkAsync(CancellationToken token)
    {
        for(var n=0;n<30;n++){token.ThrowIfCancellationRequested();if(NetworkInterface.GetAllNetworkInterfaces().Any(i=>i.OperationalStatus==OperationalStatus.Up&&i.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet))return true;await Task.Delay(1000,token);}return false;
    }
    private async Task<bool> ResumeConnectionHealthyAsync(CancellationToken token)
    {
        if(!IsConnectionRunning()||(_state.TunEnabled&&_xrayTunRouteLease is not null&&!_xrayTunRouteLease.IsPhysicalLinkUnchanged()))return false;
        if(!_state.TunEnabled&&!_state.MixedProxyEnabled)return false;
        try{using var handler=new HttpClientHandler{UseProxy=_state.MixedProxyEnabled,Proxy=_state.MixedProxyEnabled?new WebProxy("http://127.0.0.1:"+_state.MixedProxyPort):null};using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(6)};using var request=new HttpRequestMessage(HttpMethod.Head,"https://www.gstatic.com/generate_204");using var reply=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);return reply.IsSuccessStatusCode;}catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}catch{return false;}
    }
    private async Task RestoreAfterSleepAsync()
    {
        if(!_state.RestoreAfterSleep||_sleepProfile is not { } profile||_recoveringSleep||_shutdownRequested)return;
        _recoveringSleep=true;var token=_backgroundConnectionCancellation.Token;
        try{
            await Task.Delay(1200,token);if(!await WaitForPhysicalNetworkAsync(token)){_waitingForSleepNetwork=true;Log("Сохранён запрос VPN: ожидается сеть после сна.");return;}_waitingForSleepNetwork=false;
            for(var attempt=0;attempt<3;attempt++){
                token.ThrowIfCancellationRequested();if(_connectionTransitionInProgress){await Task.Delay(1000,token);attempt--;continue;}
                if(await ResumeConnectionHealthyAsync(token)){Diagnostics.Record("network","POWER_RESUME_CONNECTION_PRESERVED");return;}
                await Task.Delay(new[]{2,5,10}[attempt]*1000,token);token.ThrowIfCancellationRequested();
                _connectionTransitionInProgress=true;_powerButton.Enabled=false;
                try{if(!await StopSingBoxAsync())return;token.ThrowIfCancellationRequested();if(_sleepCore.HasValue)_state.SelectedCore=_sleepCore.Value;if(await StartSingBoxAsync(profile)){if(token.IsCancellationRequested){await StopSingBoxAsync();return;}Diagnostics.Record("network","POWER_RESUME_RESTORED");return;}}
                finally{_connectionTransitionInProgress=false;_powerButton.Enabled=true;PushWebSnapshot();}
            }
            Log("Не удалось восстановить VPN после сна. Конфиг сохранён; подключитесь повторно.");
        }catch(OperationCanceledException){}catch(Exception error){Log("Восстановление после сна: "+ConnectionDiagnostics.Redact(error.Message));}finally{_recoveringSleep=false;if(!_waitingForSleepNetwork)_sleepProfile=null;}
    }
}
