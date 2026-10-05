using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HappBox;

/// <summary>Owned Windows PowerShell session; inherited pipes have no public listener.</summary>
internal sealed class WindowsNetworkWorker : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stderr;
    public int ProcessId => _process.Id;
    public bool Alive => !_process.HasExited;
    private sealed record Reply(int Schema, string Id, string RunId, string Operation, bool Ok,
        string Output, string Error, long DurationMs);
    private const string Bootstrap = """
        [Console]::InputEncoding=[Text.UTF8Encoding]::new($false)
        [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
        $ProgressPreference='SilentlyContinue'; $WarningPreference='SilentlyContinue'; $ErrorActionPreference='Stop'
        Import-Module NetTCPIP,DnsClient -ErrorAction Stop
        [Console]::Out.WriteLine('IOR_NETWORK_READY_V1'); [Console]::Out.Flush()
        while ($null -ne ($line=[Console]::In.ReadLine())) {
          if ($line.Length -gt 131072) { break }
          try {
            $request=ConvertFrom-Json -InputObject $line -ErrorAction Stop
            if ($request.Schema -ne 1 -or $request.Id -notmatch '^[a-f0-9]{32}$' -or $request.RunId -notmatch '^[a-f0-9]{32}$' -or
                $request.Operation -notin @('Inspect','Apply','Verify','Rollback','Snapshot')) { throw 'NETWORK_REQUEST_INVALID' }
            $script=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($request.Script))
            if ($script.Length -gt 65536) { throw 'NETWORK_REQUEST_TOO_LARGE' }
            $watch=[Diagnostics.Stopwatch]::StartNew(); $output=''; $detail=''; $ok=$false
            try { $output=(& ([ScriptBlock]::Create($script)) | Out-String -Width 4096); $ok=$true }
            catch { $detail=$_.FullyQualifiedErrorId + ': ' + $_.Exception.Message }
            $watch.Stop()
            $reply=@{Schema=1;Id=$request.Id;RunId=$request.RunId;Operation=$request.Operation;Ok=$ok;Output=$output;Error=$detail;DurationMs=$watch.ElapsedMilliseconds}
            [Console]::Out.WriteLine(($reply | ConvertTo-Json -Depth 4 -Compress)); [Console]::Out.Flush()
          } catch { break }
        }
        """;

    private WindowsNetworkWorker(Process process)
    {
        _process=process;
        // Do not retain raw PowerShell diagnostics, which could include command parameters.
        _stderr=Task.Run(async()=> { while(await process.StandardError.ReadLineAsync() is not null){} });
    }
    public static async Task<WindowsNetworkWorker> StartAsync(CancellationToken token)
    {
        var start=new ProcessStartInfo("powershell.exe") {UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(var argument in new[]{"-NoLogo","-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(Bootstrap))})start.ArgumentList.Add(argument);
        var worker=new WindowsNetworkWorker(Process.Start(start)??throw new IOException("WINDOWS_NETWORK_PROCESS_START_FAILED"));
        try {
            if(await worker._process.StandardOutput.ReadLineAsync(token)!="IOR_NETWORK_READY_V1")throw new IOException("WINDOWS_NETWORK_WORKER_NOT_READY");
            return worker;
        } catch {await worker.DisposeAsync();throw;}
    }
    public async Task<string> ExecuteAsync(string script, WindowsNetworkOperation operation, string runId, CancellationToken token)
    {
        if(script.Length>65536 || !Enum.IsDefined(operation))throw new InvalidOperationException("WINDOWS_NETWORK_REQUEST_INVALID");
        var id=Guid.NewGuid().ToString("N");
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new {Schema=1,Id=id,RunId=runId,Operation=operation.ToString(),Script=Convert.ToBase64String(Encoding.UTF8.GetBytes(script))}));
        await _process.StandardInput.FlushAsync(token);
        var line=await _process.StandardOutput.ReadLineAsync(token);
        if(line is null || line.Length>1048576)throw new IOException("WINDOWS_NETWORK_WORKER_LOST");
        Reply reply;
        try { reply=JsonSerializer.Deserialize<Reply>(line)??throw new IOException("WINDOWS_NETWORK_RESPONSE_INVALID"); }
        catch (JsonException error) { throw new IOException("WINDOWS_NETWORK_RESPONSE_INVALID", error); }
        if(reply.Schema!=1 || reply.Id!=id || reply.RunId!=runId || reply.Operation!=operation.ToString())throw new IOException("WINDOWS_NETWORK_RESPONSE_MISMATCH");
        if(!reply.Ok)throw new InvalidOperationException($"WINDOWS_NETWORK_COMMAND_FAILED: {operation}; exit=1; {ConnectionDiagnostics.Redact(reply.Error)[..Math.Min(1024,ConnectionDiagnostics.Redact(reply.Error).Length)]}");
        return reply.Output;
    }
    public async ValueTask DisposeAsync()
    {
        try {
            _process.StandardInput.Close();
            try {await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));}
            catch(TimeoutException){if(!_process.HasExited)_process.Kill(entireProcessTree:true);await _process.WaitForExitAsync();}
            await _stderr;
        } finally {_process.Dispose();}
    }
}

public enum WindowsNetworkOperation { Inspect, Apply, Verify, Rollback, Snapshot }
