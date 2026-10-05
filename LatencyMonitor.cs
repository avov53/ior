using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HappBox;

public sealed class LatencyMonitor : IDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly Func<ProxyProfile, CancellationToken, Task<long?>> _probe;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, EndpointState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private List<ProxyProfile> _profiles = [];
    private string? _selectedProfileId;
    private Task? _loop;
    public bool LowPowerMode { get; set; }

    public event EventHandler<string>? Updated;

    public LatencyMonitor(Func<ProxyProfile, CancellationToken, Task<long?>>? probe = null, int maximumConcurrency = 4)
    {
        maximumConcurrency = Math.Max(1, maximumConcurrency);
        _gate = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        _probe = probe ?? MeasureAsync;
    }

    public void SetProfiles(IEnumerable<ProxyProfile> profiles, string? selectedProfileId)
    {
        lock (_sync)
        {
            _profiles = profiles.ToList();
            _selectedProfileId = selectedProfileId;
            foreach (var key in _profiles.Select(ServerTreeView.EndpointKey).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!_states.ContainsKey(key)) _states[key] = new EndpointState();
            }
        }
    }

    public void Start()
    {
        _loop ??= Task.Run(RunAsync);
    }

    public void Select(string? profileId)
    {
        lock (_sync) _selectedProfileId = profileId;
        _ = ProbeSelectedNowAsync();
    }

    public async Task RefreshAllAsync()
    {
        List<ProxyProfile> endpoints;
        lock (_sync)
        {
            endpoints = _profiles.GroupBy(ServerTreeView.EndpointKey, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First()).ToList();
        }
        await Task.WhenAll(endpoints.Select(x => ProbeAsync(x, _cts.Token)));
    }

    public IReadOnlyDictionary<string, LatencySnapshot> Snapshot()
    {
        lock (_sync)
        {
            return _states.ToDictionary(
                x => x.Key,
                x => x.Value.ToSnapshot(),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public LatencySnapshot? SnapshotFor(ProxyProfile? profile)
    {
        if (profile is null) return null;
        lock (_sync)
        {
            return _states.TryGetValue(ServerTreeView.EndpointKey(profile), out var state) ? state.ToSnapshot() : null;
        }
    }

    public static async Task<long?> MeasureAsync(ProxyProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.Protocol is not (ProxyProtocol.Hysteria2 or ProxyProtocol.WireGuard))
        {
            try
            {
                using var client = new TcpClient();
                var watch = Stopwatch.StartNew();
                await client.ConnectAsync(profile.Server, profile.Port)
                    .WaitAsync(TimeSpan.FromMilliseconds(1200), cancellationToken);
                watch.Stop();
                return Math.Max(1, watch.ElapsedMilliseconds);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(profile.Server, 1200)
                .WaitAsync(TimeSpan.FromMilliseconds(1300), cancellationToken);
            return reply.Status == IPStatus.Success ? Math.Max(1, reply.RoundtripTime) : null;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _gate.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await ProbeDueAsync(_cts.Token);
                await Task.Delay(1000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProbeDueAsync(CancellationToken cancellationToken)
    {
        List<ProxyProfile> due;
        lock (_sync)
        {
            var selected = _profiles.FirstOrDefault(x => x.Id == _selectedProfileId);
            due = _profiles.GroupBy(ServerTreeView.EndpointKey, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .Where(profile =>
                {
                    var state = _states[ServerTreeView.EndpointKey(profile)];
                    var interval = selected is not null && ServerTreeView.EndpointKey(selected) == ServerTreeView.EndpointKey(profile)
                        ? TimeSpan.FromSeconds(LowPowerMode ? 20 : 4)
                        : TimeSpan.FromSeconds(LowPowerMode ? 120 : 30);
                    return !state.Running && DateTime.UtcNow - state.LastProbeUtc >= interval;
                })
                .ToList();
        }
        await Task.WhenAll(due.Select(x => ProbeAsync(x, cancellationToken)));
    }

    private async Task ProbeSelectedNowAsync()
    {
        ProxyProfile? selected;
        lock (_sync) selected = _profiles.FirstOrDefault(x => x.Id == _selectedProfileId);
        if (selected is not null) await ProbeAsync(selected, _cts.Token);
    }

    private async Task ProbeAsync(ProxyProfile profile, CancellationToken cancellationToken)
    {
        var key = ServerTreeView.EndpointKey(profile);
        lock (_sync)
        {
            if (!_states.TryGetValue(key, out var state) || state.Running) return;
            state.Running = true;
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var latency = await _probe(profile, cancellationToken);
            lock (_sync)
            {
                var state = _states[key];
                state.Current = latency;
                state.LastProbeUtc = DateTime.UtcNow;
                state.Samples.Enqueue(latency);
                while (state.Samples.Count > 60) state.Samples.Dequeue();
            }
            Updated?.Invoke(this, key);
        }
        finally
        {
            _gate.Release();
            lock (_sync)
            {
                if (_states.TryGetValue(key, out var state)) state.Running = false;
            }
        }
    }

    private sealed class EndpointState
    {
        public long? Current { get; set; }
        public DateTime LastProbeUtc { get; set; } = DateTime.MinValue;
        public bool Running { get; set; }
        public Queue<long?> Samples { get; } = new();

        public LatencySnapshot ToSnapshot()
        {
            var samples = Samples.ToArray();
            var valid = samples.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
            return new LatencySnapshot(
                Current,
                valid.Length == 0 ? null : valid.Min(),
                valid.Length == 0 ? null : (long)Math.Round(valid.Average()),
                valid.Length == 0 ? null : valid.Max(),
                samples);
        }
    }
}
