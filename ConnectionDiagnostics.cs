using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace HappBox;

/// <summary>Allowlisted events only. Never serialize AppState, configs, profiles or exception messages.</summary>
public sealed class ConnectionDiagnostics : IDisposable
{
    public sealed record Event(int Schema, DateTimeOffset Utc, string RunId, string Stage, string Code,
        IReadOnlyDictionary<string, string> Fields);
    private readonly ConcurrentQueue<Event> _events = new();
    private sealed record Work(Event? Event=null,Func<Task>? Control=null);
    private readonly JournalFileStore _files;
    private readonly Channel<Work> _pending = Channel.CreateBounded<Work>(new BoundedChannelOptions(2048)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Task _writer;
    private string _runId = Guid.NewGuid().ToString("N");
    private long _dropped;
    private long _clearGeneration;
    private readonly ConcurrentQueue<(string RunId, NetworkDiagnosticSnapshot Snapshot)> _snapshots = new();
    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
        { "engine", "mode", "protocol", "interface", "interfaceIndex", "address", "destination", "ruleId", "target", "durationMs", "count", "exitCode", "errorType", "socketError", "sha256", "version", "phase", "operation", "side", "direction", "sentBytes", "receivedBytes", "flow", "elapsedMs", "slowStage", "windowsBuild", "tunSupported", "destinationName", "resolver", "queryType", "reason", "waitCount", "readEof", "writeClosed", "source", "readWaitUs", "sendWaitUs", "receiveWaitUs", "queueWaitUs", "maxWaitUs", "rxQueuedBytes", "txQueuedBytes", "rxZeroWindowUs", "peerZeroWindowUs", "reassemblyDroppedPackets", "active", "fallback", "forwardedPackets", "receivedPackets", "droppedPackets", "packetBudgetBytes", "packetBudgetUsedBytes", "packetBudgetPeakBytes", "budgetDroppedPackets", "queueDroppedPackets", "totalUs", "maxUs", "rcode", "answerCount", "truncated" };

    static ConnectionDiagnostics()
    {
        AllowedFields.UnionWith(["proxyQueueDrops","tunQueueDrops","queuedDatagrams","queuedBytes","receiveQueueDrops","cwndBytes","lostPackets","sentPackets","rttEstimateMs","ttlSeconds","dnsNameCount","tunDeliveredBytes","maxPacketBytes","mtu","icmpType","icmpCode","transport","nameSource"]);
    }
    public ConnectionDiagnostics(string directory, string? runId = null)
    {
        _files=new(directory);
        if (runId is not null && Guid.TryParseExact(runId, "N", out _)) _runId = runId;
        _writer = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "events.jsonl");
                var resourcePath=Path.Combine(directory,"resources.jsonl");
                string? resourceRun=null;
                _files.Prune(true);
                await foreach (var work in _pending.Reader.ReadAllAsync())
                {
                    if(work.Control is { } control){await control();continue;}
                    try {
                    var item=work.Event!;
                    _events.Enqueue(item);while(_events.Count>2048)_events.TryDequeue(out _);
                    _files.Append(JsonSerializer.Serialize(item)+"\n");
                    if(item.Code.StartsWith("RESOURCE_",StringComparison.Ordinal))
                    {
                        if(resourceRun!=item.RunId||!File.Exists(resourcePath)){await File.WriteAllTextAsync(resourcePath,"");resourceRun=item.RunId;}
                        var line=JsonSerializer.Serialize(item)+"\n";
                        if(new FileInfo(resourcePath).Length+System.Text.Encoding.UTF8.GetByteCount(line)>256*1024)
                        {
                            var rows=await File.ReadAllLinesAsync(resourcePath);
                            var compact=CompactResourceRows(rows);
                            await File.WriteAllTextAsync(resourcePath,compact);
                            var marker=item with {Code="RESOURCE_REPORT_COMPACTED",Fields=new Dictionary<string,string>{{"count",Math.Max(0,rows.Length-compact.Count(c=>c=='\n')).ToString()}}};
                            await File.AppendAllTextAsync(resourcePath,JsonSerializer.Serialize(marker)+"\n");
                        }
                        if(new FileInfo(resourcePath).Length+System.Text.Encoding.UTF8.GetByteCount(line)<=256*1024)
                            await File.AppendAllTextAsync(resourcePath,line);
                    }
                    _files.Prune();
                    }catch(IOException){Interlocked.Increment(ref _dropped);}catch(UnauthorizedAccessException){Interlocked.Increment(ref _dropped);}
                }
            }
            catch (IOException) { Interlocked.Increment(ref _dropped); }
            catch (UnauthorizedAccessException) { Interlocked.Increment(ref _dropped); }
        });
    }

    public string RunId => Volatile.Read(ref _runId);
    internal static string CompactResourceRows(string[] rows)
    {
        var head=new List<string>();var bytes=0;
        foreach(var row in rows){var size=System.Text.Encoding.UTF8.GetByteCount(row)+1;if(bytes+size>32*1024)break;head.Add(row);bytes+=size;}
        var tail=new List<string>();bytes=0;
        for(var i=rows.Length-1;i>=head.Count;i--){var size=System.Text.Encoding.UTF8.GetByteCount(rows[i])+1;if(bytes+size>128*1024)break;tail.Add(rows[i]);bytes+=size;}
        tail.Reverse();return string.Join('\n',head.Concat(tail))+"\n";
    }
    public async Task CaptureNetworkAsync(string phase)
    {
        var generation=Interlocked.Read(ref _clearGeneration);
        try
        {
            var snapshot=await NetworkDiagnosticSnapshot.CaptureAsync(phase);if(generation!=Interlocked.Read(ref _clearGeneration))return;_snapshots.Enqueue((RunId,snapshot));
            while (_snapshots.Count > 12) _snapshots.TryDequeue(out _);
            Record("snapshot", "NETWORK_SNAPSHOT_OK", ("phase", phase));
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or System.ComponentModel.Win32Exception or JsonException)
        { Record("snapshot", "NETWORK_SNAPSHOT_FAILED", ("phase", phase), ("errorType", error.GetType().Name)); }
    }
    public string Begin(string engine, string mode, string protocol)
    {
        Volatile.Write(ref _runId, Guid.NewGuid().ToString("N"));
        Record("start", "START_REQUESTED", ("engine", engine), ("mode", mode), ("protocol", protocol));
        return RunId;
    }

    public void Record(string stage, string code, params (string Key, string Value)[] fields)
    {
        if (!Regex.IsMatch(stage, "^[a-z_.-]{1,40}$") || !Regex.IsMatch(code, "^[A-Z0-9_]{1,64}$"))
            throw new ArgumentException("Diagnostic stage/code must be constants, not external input.");
        var safe = fields.Where(item => AllowedFields.Contains(item.Key))
            .ToDictionary(item => item.Key, item => Redact(item.Value).Substring(0, Math.Min(Redact(item.Value).Length, 256)));
        var item = new Event(1, DateTimeOffset.UtcNow, RunId, stage, code, safe);
        if (!_pending.Writer.TryWrite(new Work(item))) Interlocked.Increment(ref _dropped);
    }
    private async Task ControlAsync(Func<Task> action)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _pending.Writer.WriteAsync(new Work(Control:async()=>{try{await action();done.SetResult();}catch(Exception error){done.SetException(error);}})).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
    public Task ConfigureAsync(JournalPolicy policy){policy.Validate();return ControlAsync(()=>{_files.Policy=new(){MaxMb=policy.MaxMb,Days=policy.Days};_files.Prune(true);return Task.CompletedTask;});}
    public Task ClearAsync()=>ControlAsync(()=>{Interlocked.Increment(ref _clearGeneration);_files.Clear();_events.Clear();_snapshots.Clear();Interlocked.Exchange(ref _dropped,0);return Task.CompletedTask;});
    public async Task<long> SizeAsync(){long size=0;await ControlAsync(()=>{_files.Prune(true);size=_files.Size();return Task.CompletedTask;});return size;}

    public static string Redact(string text, IEnumerable<string?>? secrets = null)
    {
        text = Regex.Replace(text, @"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", "[uuid]");
        foreach (var secret in (secrets ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct().OrderByDescending(value => value!.Length))
            text = text.Replace(secret!, "[secret]", StringComparison.Ordinal);
        text = Regex.Replace(text, @"(?i)\b(?:https?|socks5?|vless|vmess|trojan|ss|hysteria2|hy2|happ|clash)://[^\s\""<>]+", "[url]");
        text = Regex.Replace(text, @"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", "[uuid]");
        text = Regex.Replace(text, @"(?i)(password|pass|publicKey|privateKey|shortId|auth|token|psk|uuid)[\""']?\s*[=:]\s*[\""']?[^\s,;\""}]+", "$1=[secret]");
        return text;
    }

    public async Task ExportAsync(string path, IEnumerable<(string Engine, string Path)> binaries)
    {
        var snapshot=Array.Empty<Event>();
        await ControlAsync(()=>{_files.Prune(true);snapshot=_files.Files().Where(p=>p.EndsWith(".jsonl")&&Path.GetFileName(p)!="resources.jsonl"||p.EndsWith(".jsonl.previous")).OrderBy(File.GetLastWriteTimeUtc).SelectMany(File.ReadLines).Select(line=>{try{return JsonSerializer.Deserialize<Event>(line);}catch{return null;}}).OfType<Event>().ToArray();return Task.CompletedTask;});
        var salt = RandomNumberGenerator.GetBytes(32);
        string Alias(string value) => "anon-" + Convert.ToHexString(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(value)))[..16];
        var events = snapshot.Select(item => item with { Fields = item.Fields.ToDictionary(pair => pair.Key,
            pair => pair.Key is "interface" or "address" or "destination" or "ruleId" or "destinationName" or "resolver" or "source" ? Alias(pair.Value) : pair.Value) }).ToArray();
        var hashes = new List<object>();
        foreach (var binary in binaries)
        {
            if (!File.Exists(binary.Path)) continue;
            await using var stream = File.OpenRead(binary.Path);
            hashes.Add(new { engine = binary.Engine, sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream)) });
        }
        // No raw logs, config, state, profile names, paths or registry dumps in the archive.
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        async Task Entry(string name, object value)
        {
            await using var entry = archive.CreateEntry(name).Open();
            await JsonSerializer.SerializeAsync(entry, value, new JsonSerializerOptions { WriteIndented = true });
        }
        await Entry("events.json", events);
        await Entry("network.json", _snapshots.ToArray().Select(item => new
        {
            runId = item.RunId, utc = item.Snapshot.Utc, phase = item.Snapshot.Phase,
            routes = item.Snapshot.Routes.Select(route => new { prefix = Alias(route.DestinationPrefix),
                nextHop = Alias(route.NextHop), interfaceIndex = Alias(route.InterfaceIndex.ToString()), route.RouteMetric }),
            dns = item.Snapshot.DnsServers.Select(dns => new { interfaceIndex = Alias(dns.InterfaceIndex.ToString()),
                servers = dns.ServerAddresses.Select(Alias) })
        }).ToArray());
        await Entry("manifest.json", new { schema = 1, runId = RunId, utc = DateTimeOffset.UtcNow,
            os = Environment.OSVersion.Version.ToString(), droppedEvents = Interlocked.Read(ref _dropped), binaries = hashes,
            scope = "allowlisted events only; addresses and interfaces pseudonymized; no config or credentials" });
    }

    public void Dispose()
    {
        _pending.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
    }
}
