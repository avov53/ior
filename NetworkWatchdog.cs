using System.Diagnostics;
using System.Text.Json;

namespace HappBox;

/// <summary>Separate process; receives only ownership metadata through an inherited private pipe.</summary>
public sealed class NetworkWatchdog : IAsyncDisposable
{
    private sealed record Message(string Kind, int ParentPid = 0, long ParentStartTicks = 0,
        string? AppDirectory = null, string? RunId = null, int ChildPid = 0, long ChildStartTicks = 0);
    private readonly Process _process;
    private NetworkWatchdog(Process process) => _process = process;

    public static async Task<NetworkWatchdog> StartAsync(string directory, string runId, string? executable = null)
    {
        var start = new ProcessStartInfo(executable ?? Environment.ProcessPath!) { UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("--network-watchdog");
        var process = Process.Start(start) ?? throw new IOException("Network watchdog could not start.");
        try
        {
            using var parent = Process.GetCurrentProcess();
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new Message("start", parent.Id,
                parent.StartTime.ToUniversalTime().Ticks, directory, runId)));
            await process.StandardInput.FlushAsync();
            var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (ready != "READY") throw new IOException("Network watchdog did not become ready.");
            return new NetworkWatchdog(process);
        }
        catch { if (!process.HasExited) process.Kill(); process.Dispose(); throw; }
    }

    public async Task OwnChildAsync(Process child)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new Message("child",
            ChildPid: child.Id, ChildStartTicks: child.StartTime.ToUniversalTime().Ticks)));
        await _process.StandardInput.FlushAsync();
        if (await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) != "OWNED")
            throw new IOException("Watchdog did not accept core ownership.");
    }
    public async Task OwnNetworkWorkerAsync(int processId)
    {
        using var child=Process.GetProcessById(processId);
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new Message("networkWorker",ChildPid:child.Id,ChildStartTicks:child.StartTime.ToUniversalTime().Ticks)));
        await _process.StandardInput.FlushAsync();
        if(await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))!="OWNED")throw new IOException("Watchdog did not accept network worker ownership.");
    }

    public async ValueTask DisposeAsync()
    {
        // Disarm only after the caller has stopped the core and restored all owned routes.
        if (!_process.HasExited)
        {
            await _process.StandardInput.WriteLineAsync("{\"Kind\":\"stop\"}");
            await _process.StandardInput.FlushAsync();
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        _process.Dispose();
    }

    public static async Task RunAsync()
    {
        // Console.In is a synchronized TextReader: its ReadLineAsync can block synchronously,
        // preventing the parent-exit task from winning while stdin remains open.
        using var inputReader = new StreamReader(Console.OpenStandardInput());
        var line = await inputReader.ReadLineAsync();
        if (line is null || line.Length > 8192) return;
        var initial = JsonSerializer.Deserialize<Message>(line);
        if (initial is not { Kind: "start", AppDirectory: not null, RunId: not null } ||
            !Guid.TryParseExact(initial.RunId, "N", out _) || !Path.IsPathFullyQualified(initial.AppDirectory)) return;
        Process parent;
        try { parent = Process.GetProcessById(initial.ParentPid); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            if (parent.StartTime.ToUniversalTime().Ticks != initial.ParentStartTicks) return;
            Console.WriteLine("READY"); await Console.Out.FlushAsync();
            var parentExit = parent.WaitForExitAsync();
            Message? child = null;
            Message? networkWorker=null;
            while (!parentExit.IsCompleted)
            {
                var input = inputReader.ReadLineAsync();
                if (await Task.WhenAny(parentExit, input) == parentExit) break;
                var update = await input;
                if (update is null) { await parentExit; break; }
                if (update.Length > 8192) break;
                var message = JsonSerializer.Deserialize<Message>(update);
                if (message?.Kind == "stop") return;
                if (message?.Kind == "child")
                {
                    child = message;
                    Console.WriteLine("OWNED"); await Console.Out.FlushAsync();
                }
                if(message?.Kind=="networkWorker") {networkWorker=message;Console.WriteLine("OWNED");await Console.Out.FlushAsync();}
            }
            using var diagnostics = new ConnectionDiagnostics(Path.Combine(initial.AppDirectory, "diagnostics"), initial.RunId);
            // Do not kill a reused PID or touch a journal belonging to a newer connection.
            if(networkWorker is {ChildPid:>0}) {
                try {using var worker=Process.GetProcessById(networkWorker.ChildPid);if(worker.StartTime.ToUniversalTime().Ticks==networkWorker.ChildStartTicks&&!worker.HasExited){worker.Kill(entireProcessTree:true);await worker.WaitForExitAsync();}}
                catch(ArgumentException){}
            }
            if (child is { ChildPid: > 0 })
            {
                try
                {
                    using var process = Process.GetProcessById(child.ChildPid);
                    if (process.StartTime.ToUniversalTime().Ticks == child.ChildStartTicks && !process.HasExited)
                    { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                }
                catch (ArgumentException) { }
            }
            var lease = XrayTunRouteLease.LoadForRecovery(initial.AppDirectory, initial.RunId);
            if (lease is not null) await lease.ReleaseAsync();
            diagnostics.Record("watchdog", "PARENT_EXIT_ROLLBACK_COMPLETE");
        }
    }
}
