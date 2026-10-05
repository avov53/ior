using System.Diagnostics;
using System.Text.Json;

namespace HappBox;

/// <summary>Uses the normal journalled rollback without starting a VPN or changing profiles.</summary>
public static class NetworkRecovery
{
    public static async Task RunPackagedAsync(string runId)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ior");
        var reportDirectory = Path.Combine(directory, "diagnostics", "network-recovery", runId);
        Directory.CreateDirectory(reportDirectory);
        void Report(string stage, string reason = "") => File.WriteAllText(Path.Combine(reportDirectory, "progress.json"),
            JsonSerializer.Serialize(new { Stage = stage, Reason = reason, Utc = DateTime.UtcNow }));
        Report("preflight");
        try
        {
            // An already restored journal needs only read-only network lookups and
            // deletion of the user's file. Cmdlets still require elevation for any
            // actual route, DNS or forwarding changes and failures retain the journal.
            using var current = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName("ior"))
            {
                using (process)
                    if (process.Id != current.Id && process.MainWindowHandle != IntPtr.Zero)
                        throw new IOException("NETWORK_RECOVERY_APP_RUNNING: закройте окно ior перед восстановлением.");
            }
            var statePath = Path.Combine(directory, "state.json");
            if (File.Exists(statePath))
            {
                using var state = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
                if (state.RootElement.TryGetProperty("ActiveProcessId", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var id))
                {
                    Process? active = null;
                    try { active = Process.GetProcessById(id); } catch (ArgumentException) { }
                    using (active)
                        if (active is not null && active.ProcessName is "ior-core" or "xray" or "sing-box")
                            throw new IOException("NETWORK_RECOVERY_CORE_RUNNING: отключите VPN перед восстановлением.");
                }
            }
            Report("rollback");
            var lease = XrayTunRouteLease.LoadForRecovery(directory);
            if (lease is not null) await lease.ReleaseAsync();
            if (File.Exists(XrayTunRouteLease.JournalPath(directory))) throw new IOException("NETWORK_RECOVERY_PENDING");
            Report("passed");
        }
        catch (Exception error)
        {
            Report("failed", ConnectionDiagnostics.Redact(error.Message));
            Environment.ExitCode = 2;
        }
    }
}
