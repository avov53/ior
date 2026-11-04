using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HappBox;

public sealed class LatencyMonitor : IDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly Func<ProxyProfile,CancellationToken,Task<long?>> _probe;
    private readonly CancellationTokenSource _cts=new();
    private readonly Dictionary<string,EndpointState> _states=new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync=new();
    private List<ProxyProfile> _profiles=[];
    private string? _selectedProfileId;
    private Task? _loop;
    private bool _disposed;
    public bool LowPowerMode {get;set;}
    public event EventHandler<string>? Updated;

    public LatencyMonitor(Func<ProxyProfile,CancellationToken,Task<long?>>? probe=null,int maximumConcurrency=2)
    {
        _gate=new SemaphoreSlim(Math.Max(1,maximumConcurrency),Math.Max(1,maximumConcurrency));
        _probe=probe??MeasureAsync;
    }
    public void SetProfiles(IEnumerable<ProxyProfile> profiles,string? selectedProfileId)
    {
        lock(_sync){if(_disposed)return;_profiles=profiles.ToList();_selectedProfileId=selectedProfileId;
            foreach(var key in _profiles.Select(ServerTreeView.EndpointKey).Distinct(StringComparer.OrdinalIgnoreCase))_states.TryAdd(key,new());}
    }
    public void Start(){lock(_sync){if(!_disposed)_loop??=Task.Run(RunAsync);}}
    public void Select(string? profileId)
    {
        CancellationTokenSource? previous=null;ProxyProfile? selected;CancellationToken token;
        lock(_sync){if(_disposed)return;
            var old=_profiles.FirstOrDefault(p=>p.Id==_selectedProfileId);selected=_profiles.FirstOrDefault(p=>p.Id==profileId);
            if(old is not null && (selected is null || ServerTreeView.EndpointKey(old)!=ServerTreeView.EndpointKey(selected)) && _states.TryGetValue(ServerTreeView.EndpointKey(old),out var state))previous=state.Cancellation;
            _selectedProfileId=profileId;token=_cts.Token;
        }
        try{previous?.Cancel();}catch(ObjectDisposedException){}
        if(selected is not null)_=ProbeAsync(selected,token);
    }
    public Task RefreshAllAsync()
    {
        List<ProxyProfile> profiles;CancellationToken token;lock(_sync){if(_disposed)return Task.CompletedTask;token=_cts.Token;profiles=_profiles.GroupBy(ServerTreeView.EndpointKey,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();}
        return Task.WhenAll(profiles.Select(p=>ProbeAsync(p,token)));
    }
    public Task RefreshAsync(ProxyProfile profile) => ProbeAsync(profile, _cts.Token);
    public IReadOnlyDictionary<string,LatencySnapshot> Snapshot(){lock(_sync)return _states.ToDictionary(p=>p.Key,p=>p.Value.ToSnapshot(),StringComparer.OrdinalIgnoreCase);}
    public LatencySnapshot? SnapshotFor(ProxyProfile? profile){lock(_sync)return profile is not null && _states.TryGetValue(ServerTreeView.EndpointKey(profile),out var state)?state.ToSnapshot():null;}
    public static bool UsesIcmpOnly(ProxyProtocol protocol)=>protocol is ProxyProtocol.Hysteria2 or ProxyProtocol.WireGuard or ProxyProtocol.AmneziaWG;
    public static async Task<long?> MeasureAsync(ProxyProfile profile,CancellationToken cancellationToken=default)
    {
        if(!UsesIcmpOnly(profile.Protocol))try{
            using var client=new TcpClient();var watch=Stopwatch.StartNew();
            await client.ConnectAsync(profile.Server,profile.Port).WaitAsync(TimeSpan.FromMilliseconds(1200),cancellationToken);
            return Math.Max(1,watch.ElapsedMilliseconds);
        }catch when(!cancellationToken.IsCancellationRequested){}
        try{using var ping=new Ping();var reply=await ping.SendPingAsync(profile.Server,1200).WaitAsync(TimeSpan.FromMilliseconds(1300),cancellationToken);
            return reply.Status==IPStatus.Success?Math.Max(1,reply.RoundtripTime):null;
        }catch when(!cancellationToken.IsCancellationRequested){return null;}
    }
    private async Task RunAsync()
    {
        ProxyProfile? selected;lock(_sync)selected=_profiles.FirstOrDefault(p=>p.Id==_selectedProfileId);
        if(selected is not null)await ProbeAsync(selected,_cts.Token);
        try{while(!_cts.IsCancellationRequested){await ProbeDueAsync(_cts.Token);await Task.Delay(1000,_cts.Token);}}catch(OperationCanceledException){}
    }
    // Background polling excludes the selected endpoint and never queues a whole subscription ahead of a click.
    internal Task ProbeDueAsync(CancellationToken token)
    {
        List<ProxyProfile> due;lock(_sync){if(_disposed)return Task.CompletedTask;
            var selected=_profiles.FirstOrDefault(p=>p.Id==_selectedProfileId);var selectedKey=selected is null?null:ServerTreeView.EndpointKey(selected);
            due=_profiles.GroupBy(ServerTreeView.EndpointKey,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).Where(p=>{
                var key=ServerTreeView.EndpointKey(p);var state=_states[key];return key!=selectedKey && !state.Running && DateTime.UtcNow-state.LastProbeUtc>=TimeSpan.FromSeconds(LowPowerMode?120:30);
            }).Take(_gate.CurrentCount).ToList();}
        return Task.WhenAll(due.Select(p=>ProbeAsync(p,token)));
    }
    private Task ProbeAsync(ProxyProfile profile,CancellationToken token)
    {
        lock(_sync){if(_disposed)return Task.CompletedTask;var key=ServerTreeView.EndpointKey(profile);
            if(!_states.TryGetValue(key,out var state))return Task.CompletedTask;
            if(state.Running){var task=state.Task??Task.CompletedTask;return state.Cancellation?.IsCancellationRequested==true?RetrySelectionAfterAsync(task,profile,token):task;}
            state.Running=true;var cancellation=CancellationTokenSource.CreateLinkedTokenSource(token);state.Cancellation=cancellation;
            // Run asynchronously so Task is installed before callbacks or completion.
            state.Task=Task.Run(()=>ProbeCoreAsync(profile,key,state,cancellation));return state.Task;}
    }
    private async Task RetrySelectionAfterAsync(Task previous,ProxyProfile profile,CancellationToken token)
    {await previous;lock(_sync){if(_disposed||_selectedProfileId!=profile.Id)return;}await ProbeAsync(profile,token);}
    private async Task ProbeCoreAsync(ProxyProfile profile,string key,EndpointState state,CancellationTokenSource cancellation)
    {
        var acquired=false;Notify(key);
        try{await _gate.WaitAsync(cancellation.Token);acquired=true;
            long? result;
            try{result=await _probe(profile,cancellation.Token);}catch(OperationCanceledException) when(cancellation.IsCancellationRequested){return;}catch{result=null;}
            cancellation.Token.ThrowIfCancellationRequested();
            lock(_sync){if(_disposed||cancellation.IsCancellationRequested)return;state.Current=result;state.LastProbeUtc=DateTime.UtcNow;state.Samples.Enqueue(result);while(state.Samples.Count>60)state.Samples.Dequeue();}
        }catch(OperationCanceledException) when(cancellation.IsCancellationRequested){}
        finally{if(acquired)_gate.Release();lock(_sync){state.Running=false;state.Cancellation=null;}cancellation.Dispose();Notify(key);}
    }
    private void Notify(string key){lock(_sync){if(!_disposed)Updated?.Invoke(this,key);}}
    public void Dispose()
    {
        Task[] pending;lock(_sync){if(_disposed)return;_disposed=true;pending=_states.Values.Select(s=>s.Task).Where(t=>t is not null).Cast<Task>().Concat(_loop is null?[]:new[]{_loop}).ToArray();}
        _cts.Cancel();_=FinishDisposeAsync(pending);
    }
    private async Task FinishDisposeAsync(Task[] pending){try{await Task.WhenAll(pending);}catch(OperationCanceledException){}finally{_gate.Dispose();_cts.Dispose();}}
    private sealed class EndpointState
    {
        public long? Current;public DateTime LastProbeUtc=DateTime.MinValue;public bool Running;public Task? Task;public CancellationTokenSource? Cancellation;
        public Queue<long?> Samples {get;}=new();
        public LatencySnapshot ToSnapshot(){var samples=Samples.ToArray();var valid=samples.Where(v=>v.HasValue).Select(v=>v!.Value).ToArray();return new(Current,valid.Length==0?null:valid.Min(),valid.Length==0?null:(long)Math.Round(valid.Average()),valid.Length==0?null:valid.Max(),samples,Running,LastProbeUtc!=DateTime.MinValue);}
    }
}
