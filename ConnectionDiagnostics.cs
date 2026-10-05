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
    private readonly Channel<Event> _pending = Channel.CreateBounded<Event>(new BoundedChannelOptions(2048)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Task _writer;
    private string _runId = Guid.NewGuid().ToString("N");
    private long _dropped;
    private readonly ConcurrentQueue<(string RunId, NetworkDiagnosticSnapshot Snapshot)> _snapshots = new();
    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
        { "engine", "mode", "protocol", "interface", "interfaceIndex", "address", "destination", "ruleId", "target", "durationMs", "count", "exitCode", "errorType", "socketError", "sha256", "version", "phase", "operation", "side", "direction", "sentBytes", "receivedBytes", "flow", "elapsedMs", "slowStage" };

    public ConnectionDiagnostics(string directory, string? runId = null)
    {
        if (runId is not null && Guid.TryParseExact(runId, "N", out _)) _runId = runId;
        _writer = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "events.jsonl");
                await foreach (var item in _pending.Reader.ReadAllAsync())
                {
                    if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                        File.Move(path, path + ".previous", overwrite: true);
                    await File.AppendAllTextAsync(path, JsonSerializer.Serialize(item) + "\n");
                }
            }
            catch (IOException) { Interlocked.Increment(ref _dropped); }
            catch (UnauthorizedAccessException) { Interlocked.Increment(ref _dropped); }
        });
    }

    public string RunId => Volatile.Read(ref _runId);
    public async Task CaptureNetworkAsync(string phase)
    {
        try
        {
            _snapshots.Enqueue((RunId, await NetworkDiagnosticSnapshot.CaptureAsync(phase)));
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
        _events.Enqueue(item);
        while (_events.Count > 2048) _events.TryDequeue(out _);
        if (!_pending.Writer.TryWrite(item)) Interlocked.Increment(ref _dropped);
    }

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
        var snapshot = _events.ToArray();
        var salt = RandomNumberGenerator.GetBytes(32);
        string Alias(string value) => "anon-" + Convert.ToHexString(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(value)))[..16];
        var events = snapshot.Select(item => item with { Fields = item.Fields.ToDictionary(pair => pair.Key,
            pair => pair.Key is "interface" or "address" or "destination" or "ruleId" ? Alias(pair.Value) : pair.Value) }).ToArray();
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
