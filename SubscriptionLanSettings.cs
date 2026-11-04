using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Diagnostics;

namespace HappBox;

public sealed partial class MainForm
{
    private readonly CancellationTokenSource _subscriptionStartupCancellation = new();
    private bool _lanUpdating;
    private string _lanSignature = "";
    private string _lanStatus = "Отключён";
    private static object[] LanInterfaces() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && IsLanAddress(a.Address))
            .Select(a => (object)new { id = n.Id, name = n.Name, address = a.Address.ToString(), prefix = System.Numerics.BitOperations.PopCount(BitConverter.ToUInt32(a.IPv4Mask.GetAddressBytes())) })).ToArray();
    private static bool IsLanAddress(System.Net.IPAddress address) { var b=address.GetAddressBytes();return b.Length==4&&(b[0]==10||b[0]==172&&b[1] is >=16 and <=31||b[0]==192&&b[1]==168||b[0]==169&&b[1]==254); }
    private object LanConfiguration()
    {
        var interfaces=LanInterfaces().Select(i=>JsonSerializer.SerializeToElement(i)).ToArray();
        var item=interfaces.FirstOrDefault(i=>i.GetProperty("id").GetString()==_state.Lan.InterfaceId);
        if(item.ValueKind==JsonValueKind.Undefined&&string.IsNullOrEmpty(_state.Lan.InterfaceId))item=interfaces.FirstOrDefault();
        if(_state.Lan.Enabled&&item.ValueKind==JsonValueKind.Undefined)throw new InvalidOperationException("LAN_NETWORK_UNAVAILABLE");
        return new { enabled=_state.Lan.Enabled,address=item.ValueKind==JsonValueKind.Undefined?"192.168.0.1":item.GetProperty("address").GetString(),prefix=item.ValueKind==JsonValueKind.Undefined?24:item.GetProperty("prefix").GetInt32(),port=_state.Lan.Port,allowedClientIps=_state.Lan.AllowedClientIps??[],auth=new {username=_state.Lan.Username,password=_state.Lan.Password} };
    }
    private async Task RefreshLanAsync(bool force=false)
    {
        if(_lanUpdating)return;
        if(_iorControlClient is null||!IsConnectionRunning()){_lanSignature="";_lanStatus=_state.Lan.Enabled?"LAN доступен после подключения ior-core":"Отключён";return;}
        _lanUpdating=true;
        try { var config=LanConfiguration();var signature=JsonSerializer.Serialize(config);if(force||signature!=_lanSignature){_lanSignature=signature;await _iorControlClient.ConfigureLanAsync(config);using var json=JsonDocument.Parse(signature);_lanStatus=_state.Lan.Enabled?json.RootElement.GetProperty("address").GetString()+":"+_state.Lan.Port:"Отключён";} }
        catch(Exception error){_lanStatus=error is InvalidOperationException?error.Message:"LAN_CONFIGURATION_FAILED";if(error is InvalidOperationException&&_iorControlClient is not null){_lanSignature="";try{await _iorControlClient.ConfigureLanAsync(new{enabled=false,address="192.168.0.1",prefix=24,port=2080,auth=(object?)null});}catch{}}}
        finally{_lanUpdating=false;}
    }
    private void SaveSubscriptionOptions(JsonElement input)
    {
        var sub=FindSubscription(RequiredString(input,"id"));
        var enabled=input.GetProperty("autoRefreshEnabled").GetBoolean();var interval=input.GetProperty("refreshIntervalHours").GetInt32();var timeout=input.GetProperty("requestTimeoutMs").GetInt32();var mode=RequiredString(input,"startupSelection");
        if(interval is <1 or >168||timeout is <5000 or >15000||mode is not ("last" or "lowest" or "random"))throw new InvalidOperationException("Неверные параметры подписки.");
        if(sub.ServerRefreshIntervalHours is {} required&&(!enabled||interval!=required))throw new InvalidOperationException("Интервал задан сервером.");
        sub.AutoRefreshEnabled=enabled;if(!sub.ServerRefreshIntervalHours.HasValue){sub.RefreshIntervalHours=interval;sub.RefreshIntervalIsCustom=true;}sub.RequestTimeoutMs=timeout;sub.RefreshOnStartup=input.GetProperty("refreshOnStartup").GetBoolean();sub.PingOnStartup=input.GetProperty("pingOnStartup").GetBoolean();sub.ConnectOnStartup=input.GetProperty("connectOnStartup").GetBoolean();sub.StartupSelection=mode;
        if(sub.ConnectOnStartup)foreach(var other in _state.Subscriptions.Where(s=>s.Id!=sub.Id))other.ConnectOnStartup=false;
        SubscriptionRefreshSchedule.ScheduleNext(sub,DateTime.UtcNow);SaveState();PushWebSnapshot();
    }
    private async Task RunSubscriptionStartupAsync()
    {
        var token=_subscriptionStartupCancellation.Token;
        var networkSignature=JsonSerializer.Serialize(LanInterfaces());
        try {
            foreach(var sub in _state.Subscriptions.Where(s=>s.RefreshOnStartup).ToArray()){token.ThrowIfCancellationRequested();await RefreshSubscriptionAsync(sub);}
            var chosen=_state.Subscriptions.FirstOrDefault(s=>s.ConnectOnStartup);
            var results=new System.Collections.Concurrent.ConcurrentDictionary<string,long>();using var gate=new SemaphoreSlim(2);
            var targets=_state.Profiles.Where(p=>_state.Subscriptions.Any(s=>s.Id==p.SubscriptionId&&(s.PingOnStartup||s.ConnectOnStartup&&s.StartupSelection=="lowest"))).ToArray();
            await Task.WhenAll(targets.Select(async p=>{await gate.WaitAsync(token);try{await _latencyMonitor.RefreshAsync(p).WaitAsync(token);var latency=_latencyMonitor.SnapshotFor(p)?.Current;if(latency.HasValue)results[p.Id]=latency.Value;}catch(OperationCanceledException){throw;}catch{}finally{gate.Release();}}));
            if(chosen is null||IsConnectionRunning()||_connectionTransitionInProgress)return;
            token.ThrowIfCancellationRequested();if(networkSignature!=JsonSerializer.Serialize(LanInterfaces()))return;var candidates=_state.Profiles.Where(p=>p.SubscriptionId==chosen.Id&&CoreEngineService.CanRun(_state,p,out _)).ToArray();
            var selected=chosen.StartupSelection switch {"random"=>candidates.Length==0?null:candidates[Random.Shared.Next(candidates.Length)],"lowest"=>candidates.Where(p=>results.ContainsKey(p.Id)).OrderBy(p=>results[p.Id]).FirstOrDefault(),_=>candidates.FirstOrDefault(p=>p.Id==chosen.LastUsedServerId)??candidates.FirstOrDefault()};
            if(selected is null){Log("Нет доступного сервера для автоподключения.");return;}
            _state.SelectedProfileId=selected.Id;SaveState();await ToggleConnectionAsync(fromStartup:true);
        }catch(OperationCanceledException){}catch(Exception){Log("Стартовые действия подписки не выполнены.");}
    }
    private async Task SaveLanAsync(JsonElement input)
    {
        var value=JsonSerializer.Deserialize<LanProxySettings>(input.GetRawText(),CoreRuntimeContract.JsonOptions)??throw new InvalidOperationException("LAN_INVALID_CONFIG");
        value.AuthEnabled=true;
        value.AllowedClientIps=(value.AllowedClientIps??[]).Select(v=>v.Trim()).Distinct().ToList();
        if(value.AllowedClientIps.Any(v=>!System.Net.IPAddress.TryParse(v,out var ip)||ip.AddressFamily!=AddressFamily.InterNetwork||!IsLanAddress(ip)))throw new InvalidOperationException("Укажите локальные IPv4-адреса разрешённых устройств.");
        if(value.Enabled&&value.AllowedClientIps.Count==0)throw new InvalidOperationException("Добавьте разрешённые IP-адреса устройств.");
        if(value.Port is <1 or >65535||value.Password.Length is <8 or >128||string.IsNullOrWhiteSpace(value.Username)||value.Username.Contains(':'))throw new InvalidOperationException("LAN_INVALID_CONFIG");
        var selected=LanInterfaces().Select(v=>JsonSerializer.SerializeToElement(v)).FirstOrDefault(v=>v.GetProperty("id").GetString()==value.InterfaceId);
        if(value.Enabled&&selected.ValueKind==JsonValueKind.Undefined)throw new InvalidOperationException("Выберите доступный локальный интерфейс.");
        if(value.Enabled){var address=System.Net.IPAddress.Parse(selected.GetProperty("address").GetString()!);var prefix=selected.GetProperty("prefix").GetInt32();var mask=uint.MaxValue<<(32-prefix);uint Ip(System.Net.IPAddress ip)=>System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());if(value.AllowedClientIps.Any(v=>(Ip(System.Net.IPAddress.Parse(v))&mask)!=(Ip(address)&mask)))throw new InvalidOperationException("Разрешённые адреса должны быть в выбранной подсети.");}
        if(value.AllowedClientIps.Count>64)throw new InvalidOperationException("Разрешено не более 64 адресов.");
        _state.Lan=value;SaveState();await RefreshLanAsync(true);if(_state.LanFirewallManaged)ApplyLanFirewallRules();PushWebSnapshot();
    }
    private void ConfigureLanFirewall()
    {
        if(!_state.Lan.Enabled||_state.Lan.AllowedClientIps is not {Count:>0})throw new InvalidOperationException("Сначала настройте и включите LAN со списком разрешённых IP.");
        var executable=CoreEngineService.IorCorePath(_state);
        if(!File.Exists(executable))throw new InvalidOperationException("ior-core не найден.");
        ApplyLanFirewallRules();_state.LanFirewallManaged=true;SaveState();
    }
    private void ApplyLanFirewallRules()
    {
        void Run(string[] args,bool optional=false){var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"netsh.exe")){UseShellExecute=false,CreateNoWindow=true};foreach(var argument in args)start.ArgumentList.Add(argument);using var process=Process.Start(start)??throw new IOException("Не удалось изменить собственное правило LAN.");if(!process.WaitForExit(10000)){try{process.Kill();}catch{}throw new IOException("Превышено время настройки брандмауэра.");}if(process.ExitCode!=0&&!optional)throw new IOException("Не удалось ограничить правило брандмауэра Windows.");}
        foreach(var name in new[]{"ior LAN proxy","ior LAN proxy restricted"})Run(["advfirewall","firewall","delete","rule","name="+name],true);
        if(!_state.Lan.Enabled)return;
        Run(["advfirewall","firewall","add","rule","name=ior LAN proxy restricted","dir=in","action=allow","protocol=TCP","localport="+_state.Lan.Port,"remoteip="+string.Join(',',_state.Lan.AllowedClientIps??[]),"profile=private","program="+CoreEngineService.IorCorePath(_state)]);
    }
}
