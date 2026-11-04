using System.Diagnostics;
using System.Text.Json;

namespace HappBox;

/// <summary>Private inherited pipes; server credentials never appear in arguments or event logs.</summary>
public sealed class IorCoreControlClient : IAsyncDisposable
{
    public sealed record RuntimeEvent(int ApiVersion, string RequestId, string Operation, string Code, bool Tcp, bool Udp, bool Tun);
    private readonly Process _process;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly Task _stderr;
    private int _disposed;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _diagnostics = new();
    public string DiagnosticCodes => string.Join(", ",_diagnostics);
    public event Action<string>? Diagnostic;
    public event Action<string, (string Key, string Value)[]>? DiagnosticDetails;
    private readonly object _messageLock=new();
    private readonly Queue<string> _pendingMessages=new();
    private readonly NativeProxyTrafficEvidence _traffic=new();
    public bool HasProxiedTraffic(string host)=>_traffic.HasProxiedTraffic(host);
    public bool HasProxiedDnsTraffic(string host)=>_traffic.HasProxiedDnsTraffic(host);
    public bool HasProxyRoute(string host)=>_traffic.HasProxyRoute(host);
    public bool HasProxiedPacketTraffic(string source,string destination)=>_traffic.HasProxiedPacketTraffic(source,destination);
    private Action<string>? _messageHandlers;
    public event Action<string> DiagnosticMessage {
        add { lock(_messageLock){_messageHandlers+=value;while(_pendingMessages.TryDequeue(out var message))value(message);} }
        remove { lock(_messageLock){_messageHandlers-=value;} }
    }
    private void PublishMessage(string message){lock(_messageLock){if(_messageHandlers is { } handlers)handlers(message);else{_pendingMessages.Enqueue(message);while(_pendingMessages.Count>128)_pendingMessages.Dequeue();}}}
    public RuntimeEvent Ready { get; private set; } = null!;
    public Process Process => _process;
    private IorCoreControlClient(Process process)
    { _process = process; _stderr = DrainDiagnosticsAsync(); }

    private async Task DrainDiagnosticsAsync()
    {
        while(await _process.StandardError.ReadLineAsync() is { } line)
        {
            if(line.Length>2048)continue;
            try
            {
                using var json=JsonDocument.Parse(line);
                var code=json.RootElement.GetProperty("code").GetString()??"";
                if(!System.Text.RegularExpressions.Regex.IsMatch(code,"^[A-Z0-9_]{1,80}$"))continue;
                _traffic.Observe(json.RootElement,code);
                _diagnostics.Enqueue(code);
                Diagnostic?.Invoke(code);
                var fields = new List<(string Key, string Value)>();
                foreach(var field in new[]{"flowId","elapsedMs","queryType","durationMs","count","sentBytes","receivedBytes","waitCount","readWaitUs","sendWaitUs","receiveWaitUs","queueWaitUs","maxWaitUs","rxQueuedBytes","txQueuedBytes","rxZeroWindowUs","peerZeroWindowUs","reassemblyDroppedPackets","active","fallback","forwardedPackets","receivedPackets","droppedPackets","packetBudgetBytes","packetBudgetUsedBytes","packetBudgetPeakBytes","budgetDroppedPackets","queueDroppedPackets","totalUs","maxUs","rcode","answerCount","dnsNameCount","ttlSeconds","tunDeliveredBytes","maxPacketBytes","mtu","icmpType","icmpCode"}.Distinct(StringComparer.Ordinal))
                    if(json.RootElement.TryGetProperty(field,out var value) && value.ValueKind==JsonValueKind.Number && value.TryGetUInt64(out var number))
                        fields.Add((field=="flowId"?"flow":field,number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                foreach(var field in new[]{"destination","destinationName","resolver","source"}.Distinct(StringComparer.Ordinal))
                    if(json.RootElement.TryGetProperty(field,out var value) && value.ValueKind==JsonValueKind.String && IsSafeDestination(value.GetString()))
                        fields.Add((field,value.GetString()!));
                foreach(var field in new[]{"reason","operation","side","transport","nameSource"}.Distinct(StringComparer.Ordinal))
                    if(json.RootElement.TryGetProperty(field,out var value) && value.ValueKind==JsonValueKind.String && value.GetString() is { } text && System.Text.RegularExpressions.Regex.IsMatch(text,"^[a-zA-Z0-9_-]{1,80}$"))
                        fields.Add((field,text));
                foreach(var field in new[]{"readEof","writeClosed","truncated"}.Distinct(StringComparer.Ordinal))
                    if(json.RootElement.TryGetProperty(field,out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        fields.Add((field,value.GetBoolean().ToString()));
                DiagnosticDetails?.Invoke(code,fields.ToArray());
                var message=code;
                if(json.RootElement.TryGetProperty("flowId",out var flow) && flow.TryGetUInt64(out var flowId))message+=" · flow="+flowId;
                if(json.RootElement.TryGetProperty("elapsedMs",out var elapsed) && elapsed.TryGetUInt64(out var elapsedMs))message+=" · elapsed="+elapsedMs+"ms";
                if(json.RootElement.TryGetProperty("destination",out var destination) && destination.ValueKind==JsonValueKind.String &&
                    IsSafeDestination(destination.GetString()))
                    message+=" · "+destination.GetString();
                if(json.RootElement.TryGetProperty("source",out var source) && source.ValueKind==JsonValueKind.String &&
                    IsSafeDestination(source.GetString()))message+=" · source="+source.GetString();
                if(json.RootElement.TryGetProperty("target",out var target) && target.ValueKind==JsonValueKind.String &&
                    target.GetString() is "PROXY" or "DIRECT" or "REJECT")message+=" → "+target.GetString();
                foreach(var field in new[]{"ruleIndex","sentBytes","receivedBytes","durationMs","parentFlowId","count","queueWaitUs","sendWaitUs","receiveWaitUs","maxWaitUs","durationUs","droppedPackets","attempt","queryType","mtu","rxBufferBytes","txBufferBytes","extraBudgetBytes","proxyQueueDrops","tunQueueDrops","queuedDatagrams","queuedBytes","receiveQueueDrops","cwndBytes","lostPackets","sentPackets","rttEstimateMs","rxWindowBytes","peerWindowBytes","rxQueuedBytes","txQueuedBytes","rxZeroWindowUs","peerZeroWindowUs","applicationWaitUs","reassemblyDroppedPackets","active","fallback","forwardedPackets","receivedPackets","droppedPackets","packetBudgetBytes","packetBudgetUsedBytes","packetBudgetPeakBytes","budgetDroppedPackets","queueDroppedPackets","totalUs","maxUs","rcode","answerCount","reassemblySegments","active","closing","retired","flowLimit","closingLimit","packetBudgetBytes","packetBudgetUsedBytes","packetBudgetPeakBytes","budgetDroppedPackets","queueDroppedPackets","forwardedPackets","forwardedBytes","receivedPackets","fallbackSyns"}.Distinct(StringComparer.Ordinal))
                    if(json.RootElement.TryGetProperty(field,out var metric) && metric.ValueKind==JsonValueKind.Number && metric.TryGetUInt64(out var number))
                        message+=" · "+field+"="+number;
                if(json.RootElement.TryGetProperty("selectedAddress",out var selectedAddress) && selectedAddress.ValueKind==JsonValueKind.String && IsSafeDestination(selectedAddress.GetString()))message+=" · selectedAddress="+selectedAddress.GetString();
                foreach(var field in new[]{"readCalls","readWaitUs","waitCount"}.Distinct(StringComparer.Ordinal))
                    if(json.RootElement.TryGetProperty(field,out var metric) && metric.ValueKind==JsonValueKind.Number && metric.TryGetUInt64(out var number)) message+=" · "+field+"="+number;
                foreach(var field in new[]{"stage","transport","kind","reason","classification","side","direction","operation","addressFamily","nameSource","resolver","nagle","congestion","addressSource"}.Distinct(StringComparer.Ordinal))
                    if(json.RootElement.TryGetProperty(field,out var detail) && detail.ValueKind==JsonValueKind.String &&
                        detail.GetString() is { } value && System.Text.RegularExpressions.Regex.IsMatch(value,"^[a-zA-Z0-9_-]{1,80}$"))
                        message+=" · "+field+"="+value;
                foreach(var field in new[]{"resolver","destinationName"}.Distinct(StringComparer.Ordinal))
                    if(json.RootElement.TryGetProperty(field,out var detail) && detail.ValueKind==JsonValueKind.String && IsSafeDestination(detail.GetString()))
                        message+=" · "+field+"="+detail.GetString();
                foreach(var field in new[]{"readEof","writeClosed","truncated"}.Distinct(StringComparer.Ordinal))
                    if(json.RootElement.TryGetProperty(field,out var detail) && detail.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        message+=" · "+field+"="+detail.GetBoolean();
                PublishMessage(message);
                while(_diagnostics.Count>64)_diagnostics.TryDequeue(out _);
            }
            catch(JsonException) { }
            catch(KeyNotFoundException) { }
        }
    }

    private static bool IsSafeDestination(string? value)=>!string.IsNullOrEmpty(value) && value.Length<=255 &&
        value.All(character=>char.IsAsciiLetterOrDigit(character) || ".:_-[]".Contains(character));

    public static async Task<IorCoreControlClient> StartAsync(string binary, int port, CoreStartRequest request,
        CancellationToken token = default)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("control-stdin"); start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var client = new IorCoreControlClient(Process.Start(start) ?? throw new IOException("ior-core could not start."));
        try
        {
            await client._process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, CoreRuntimeContract.JsonOptions));
            await client._process.StandardInput.FlushAsync(token);
            var ready = await client.ReadAsync(token);
            if (ready.Operation == "error") throw new IOException($"ior-core: {ready.Code}");
            var validating=request.Operation=="validate";
            if (ready.RequestId != request.RequestId || ready.Operation != (validating ? "validated" : "ready") || ready.Tcp != !validating ||
                ready.Udp != (!validating && request.Profile.Protocol != "vless_tls") || ready.Tun != (!validating && request.Tun is not null))
                throw new InvalidDataException("ior-core capabilities do not match the requested runtime.");
            client.Ready = ready; return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    public static async Task ValidateProfileAsync(string binary,CoreStartRequest request,CancellationToken token=default)
    {
        var validation=JsonSerializer.Deserialize<CoreStartRequest>(JsonSerializer.Serialize(request,CoreRuntimeContract.JsonOptions),CoreRuntimeContract.JsonOptions)!;
        validation.Operation="validate";validation.Tun=null;
        await using var client=await StartAsync(binary,1,validation,token);
        if(client.Ready.Code!="PROFILE_VALIDATED")throw new IOException("NATIVE_PROFILE_VALIDATION_FAILED");
        await client.Process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5),token);
    }

    public async Task<RuntimeEvent> StatusAsync(CancellationToken token = default)
    {
        await _commands.WaitAsync(token);
        try { return await CommandAsync("status", token); }
        finally { _commands.Release(); }
    }

    private async Task<RuntimeEvent> CommandAsync(string operation, CancellationToken token, object? lan=null)
    {
        var id = Guid.NewGuid().ToString("N");
        object command=lan is null?new { apiVersion=CoreRuntimeContract.ApiVersion,requestId=id,operation }:(object)new {apiVersion=CoreRuntimeContract.ApiVersion,requestId=id,operation,lan};
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
        await _process.StandardInput.FlushAsync(token);
        var result = await ReadAsync(token);
        if (result.RequestId != id || result.Operation != (operation == "stop" ? "stopped" : operation))
            throw new InvalidDataException("ior-core response identity mismatch.");
        return result;
    }

    public async Task ConfigureLanAsync(object settings,CancellationToken token=default){await _commands.WaitAsync(token);try{var result=await CommandAsync("configureLan",token,settings);if(result.Code!="LAN_CONFIGURED")throw new IOException("LAN_CONFIGURATION_FAILED");}finally{_commands.Release();}}
    public async Task CheckDirectEgressAsync(CancellationToken token = default)
    {
        await _commands.WaitAsync(token);
        try
        {
            var result = await CommandAsync("probeDirect", token);
            if (result.Code != "DIRECT_TCP_UDP_VERIFIED")
                throw new IOException("DIRECT_TCP_UDP_PROBE_FAILED: физический TCP/UDP-выход собственного ядра не подтверждён.");
        }
        finally { _commands.Release(); }
    }

    private async Task<RuntimeEvent> ReadAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        // Bounded framing, including a malicious/misconfigured executable that never writes a newline.
        var text = new System.Text.StringBuilder(); var character = new char[1];
        while (true)
        {
            if (await _process.StandardOutput.ReadAsync(character, timeout.Token) == 0)
                throw new EndOfStreamException("ior-core closed its control channel.");
            if (character[0] == '\n') break;
            if (text.Length >= 8192) throw new InvalidDataException("ior-core event exceeds control limit.");
            text.Append(character[0]);
        }
        var result = JsonSerializer.Deserialize<RuntimeEvent>(text.ToString(), CoreRuntimeContract.JsonOptions)
            ?? throw new InvalidDataException("ior-core returned an empty event.");
        if (result.ApiVersion is not (1 or 2 or 3)) throw new InvalidDataException("Unsupported ior-core event version.");
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        await _commands.WaitAsync();
        try
        {
            if (!_process.HasExited)
            {
                using var stopTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await CommandAsync("stop", stopTimeout.Token); }
                catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException) { }
                _process.StandardInput.Close();
                // The stop acknowledgement and process exit share one grace period.
                // A stalled control channel must not add another full timeout.
                try { await _process.WaitForExitAsync(stopTimeout.Token); }
                catch (OperationCanceledException) { if (!_process.HasExited) _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
            }
            await _stderr;
            _process.Dispose();
        }
        finally { _commands.Release(); }
    }
}
