using System.Security.Cryptography;
using System.Text.Json;

namespace HappBox;

/// <summary>Qualification belongs to the exact native binary and tested Reality flow.</summary>
public static class NativeTunQualification
{
    public static readonly string[] RequiredChecks = ["tcp", "dns", "udp", "proxy", "direct", "reject", "websocket",
        "udp443Flow", "stopRollback", "crashRollback"];
    public static readonly string[] ExtendedChecks = ["load30", "sleepResume", "networkChange", "discord", "youtube"];
    public sealed record Status(bool Ready, string Reason, string[] Missing, string[] ExtendedPassed);
    private sealed record Proof(int Schema, string Sha256, int WindowsBuild, string Protocol, string Flow,
        string[] Checks, DateTime UpdatedUtc);
    public static string ReportPath(string binary) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(binary))!, "tun-validation.json");

    public static bool CanRun(string binary, string flow, out string reason)
    {
        var status = Inspect(binary, flow);
        reason = status.Ready ? "" : status.Reason;
        return status.Ready;
    }

    public static Status Inspect(string binary, string flow)
    {
        var reason = "NATIVE_TUN_VALIDATION_PENDING: базовый прогон Reality TUN для этой сборки ещё не пройден.";
        if (!OperatingSystem.IsWindowsVersionAtLeast(10,0,22000))
            return new(false, "ior-core TUN требует отдельного испытания Windows 10; первая проверка рассчитана на Windows 11.", RequiredChecks, []);
        try
        {
            var path = ReportPath(binary);
            if (!File.Exists(path) || new FileInfo(path).Length > 16384 || !File.Exists(binary)) return new(false, reason, RequiredChecks, []);
            var proof = JsonSerializer.Deserialize<Proof>(File.ReadAllText(path));
            if (proof is null || proof.Schema != 1 || proof.Protocol != "vless_reality" || proof.WindowsBuild < 22000 ||
                proof.Flow != flow || proof.Checks is null || proof.Sha256 != Hash(binary))
                return new(false, "NATIVE_TUN_VALIDATION_PENDING: отчёт не соответствует текущей сборке или Reality flow.", RequiredChecks, []);
            var missing = RequiredChecks.Except(proof.Checks, StringComparer.Ordinal).ToArray();
            var extended = ExtendedChecks.Intersect(proof.Checks, StringComparer.Ordinal).ToArray();
            if (missing.Length != 0)
            {
                reason = "NATIVE_TUN_VALIDATION_PENDING: ещё не подтверждены " + string.Join(", ", missing.Select(Describe)) + ".";
                return new(false, reason, missing, extended);
            }
            return new(true, "Базовая проверка пройдена. Reality TUN доступен экспериментально на Windows 11.", [], extended);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new(false, reason, RequiredChecks, []); }
    }
    private static string Describe(string check) => check switch
    {
        "tcp" => "TCP", "dns" => "DNS", "udp" => "UDP", "proxy" => "выход через Reality", "direct" => "прямой выход", "reject" => "блокировка правилами",
        "websocket" => "WebSocket", "udp443Flow" => "UDP/443 выбранного flow", "stopRollback" => "откат после остановки",
        "crashRollback" => "откат после сбоя", "load30" => "30 минут нагрузки", "sleepResume" => "сон и пробуждение",
        "networkChange" => "смена сети", "discord" => "загрузка Discord", "youtube" => "воспроизведение YouTube", _ => check
    };

    public static void Record(string binary, string flow, IEnumerable<string> checks)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            throw new InvalidOperationException("Native TUN qualification is restricted to Windows 11.");
        var path = ReportPath(binary);
        var hash = Hash(binary);
        Proof? previous = null;
        if (File.Exists(path))
            try { previous = JsonSerializer.Deserialize<Proof>(File.ReadAllText(path)); } catch (JsonException) { }
        var verified = new HashSet<string>(StringComparer.Ordinal);
        if (previous is { Schema: 1, Protocol: "vless_reality" } && previous.Sha256 == hash && previous.Flow == flow && previous.Checks is not null)
            verified.UnionWith(previous.Checks);
        verified.UnionWith(checks.Intersect(RequiredChecks.Concat(ExtendedChecks), StringComparer.Ordinal));
        var proof = new Proof(1, hash, Environment.OSVersion.Version.Build, "vless_reality", flow,
            verified.Order(StringComparer.Ordinal).ToArray(), DateTime.UtcNow);
        var pending = path + ".pending";
        File.WriteAllText(pending, JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(pending, path, overwrite: true);
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Hashes = new();
    private static string Hash(string binary)
    {
        var info = new FileInfo(binary);
        var key = info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        return Hashes.GetOrAdd(key, _ => { using var stream = File.OpenRead(binary); return Convert.ToHexString(SHA256.HashData(stream)); });
    }
}
