using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace HappBox;

/// <summary>Runs network cmdlets with explicit completion and a plain, bounded error channel.</summary>
public static class WindowsNetworkCommand
{
    private static readonly SemaphoreSlim Gate=new(1,1);
    private static readonly AsyncLocal<string?> Run=new();
    private static WindowsNetworkWorker? _worker;
    public static bool WorkerEnabled {get;set;}=true;
    public static int? WorkerProcessId => _worker is {Alive:true} worker?worker.ProcessId:null;
    public static IDisposable BeginRun(string runId) {var previous=Run.Value;Run.Value=Guid.TryParseExact(runId,"N",out _)?runId:throw new ArgumentException("Invalid run ID");return new RunScope(previous);}
    private sealed class RunScope(string? previous):IDisposable {public void Dispose()=>Run.Value=previous;}
    public static async Task WarmupAsync(CancellationToken token=default)
    {
        if(!WorkerEnabled)return;
        await Gate.WaitAsync(token);
        try {if(_worker is null || !_worker.Alive){if(_worker is not null){var previous=_worker;_worker=null;await previous.DisposeAsync();}using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(15));_worker=await WindowsNetworkWorker.StartAsync(deadline.Token);}}
        finally {Gate.Release();}
    }
    public static async Task ShutdownAsync()
    {
        await Gate.WaitAsync();try {if(_worker is not null){var worker=_worker;_worker=null;await worker.DisposeAsync();}}finally{Gate.Release();}
    }
    private const string ErrorPrefix = "IOR_NETWORK_ERROR:";
    private static event Action<string,long>? Measured;
    public static IDisposable Observe(Action<string,long> handler){Measured+=handler;return new Subscription(handler);}
    private sealed class Subscription(Action<string,long> handler):IDisposable{public void Dispose()=>Measured-=handler;}
    private sealed class Measurement(string operation):IDisposable {
        private readonly Stopwatch _time=Stopwatch.StartNew();
        public void Dispose(){foreach(var handler in Measured?.GetInvocationList()??[]){try{((Action<string,long>)handler)(operation,_time.ElapsedMilliseconds);}catch(Exception error) when(error is IOException or ObjectDisposedException){}}}
    }

    public static async Task<string> RunAsync(string script, CancellationToken token = default, WindowsNetworkOperation kind=WindowsNetworkOperation.Inspect)
    {
        if(!WorkerEnabled)return await RunIsolatedAsync(script,token);
        await WarmupAsync(token);
        await Gate.WaitAsync(token);
        var operation=Regex.Match(script,@"\b(?:Get|Set|New|Remove|Add|Clear)-[A-Za-z]+\b").Value;
        using var measurement=new Measurement(operation.Length==0?kind.ToString():operation);
        try {
            // Shutdown may have run between prewarming and acquiring the command gate.
            if (_worker is null || !_worker.Alive)
            {
                await DropWorkerAsync();
                using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token);
                preparation.CancelAfter(TimeSpan.FromSeconds(15));
                _worker = await WindowsNetworkWorker.StartAsync(preparation.Token);
            }
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try {return await _worker!.ExecuteAsync(script,kind,Run.Value??Guid.NewGuid().ToString("N"),timeout.Token);}
            catch(OperationCanceledException){await DropWorkerAsync();token.ThrowIfCancellationRequested();throw new TimeoutException("WINDOWS_NETWORK_TIMEOUT: операция не завершилась за 15 секунд.");}
            catch(IOException){await DropWorkerAsync();throw;}
            // A failed mutating command is never repeated automatically: rollback reconciles the journal.
        } finally {Gate.Release();}
    }
    private static async Task DropWorkerAsync(){var worker=_worker;_worker=null;if(worker is not null)await worker.DisposeAsync();}
    public static async Task<string> RunIsolatedAsync(string script, CancellationToken token = default)
    {
        var operation = Regex.Match(script, @"\b(?:Get|Set|New|Remove|Add|Clear)-[A-Za-z]+\b").Value;
        if (operation.Length == 0) operation = "PowerShell";
        using var measurement=new Measurement(operation);
        var wrapped = "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); " +
            "$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; try {\n" + script +
            "\n} catch { $detail=$_.FullyQualifiedErrorId + ': ' + $_.Exception.Message; " +
            "Write-Output ('" + ErrorPrefix + "' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($detail))); exit 1 }; exit 0";
        // PowerShell otherwise derives its exit status from $?: a deliberately
        // suppressed lookup of an absent route can make a successful rollback exit 1.
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped)) }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("WINDOWS_NETWORK_PROCESS_START_FAILED");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException($"WINDOWS_NETWORK_TIMEOUT: {operation} не завершилась за 15 секунд.");
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode == 0) return output;
        var encoded = output.Split('\n').Select(line => line.Trim())
            .LastOrDefault(line => line.StartsWith(ErrorPrefix, StringComparison.Ordinal));
        string detail;
        if (encoded is not null)
        {
            try { detail = Encoding.UTF8.GetString(Convert.FromBase64String(encoded[ErrorPrefix.Length..])); }
            catch (FormatException) { detail = "Windows вернула повреждённое описание ошибки"; }
        }
        else if (!string.IsNullOrWhiteSpace(error) && !error.TrimStart().StartsWith("#< CLIXML", StringComparison.Ordinal))
            detail = error.Trim();
        else detail = "Windows завершила сетевую команду без описания ошибки";
        detail = ConnectionDiagnostics.Redact(detail);
        if (detail.Length > 1024) detail = detail[..1024];
        throw new InvalidOperationException($"WINDOWS_NETWORK_COMMAND_FAILED: {operation}; exit={process.ExitCode}; {detail}");
    }
}
