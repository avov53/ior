using System.Text.RegularExpressions;
namespace HappBox;
public static class WindowsLogPolicy
{
    private static readonly Regex Tagged=new(@"^(?:\[\d{2}:\d{2}:\d{2}\] )?\[IOR:(info|warning|error)\]",RegexOptions.Compiled);
    private static readonly Regex Critical=new(@"NATIVE_[A-Z_]*FAILED|TUN_TRAFFIC_FAILED|TUN_EGRESS_RECAPTURED|ROUTE_LOOP|ROLLBACK_PENDING|RECOVERY_REQUIRED|DNS_POLICY_CONFLICT|INVALID_JOURNAL",RegexOptions.Compiled);
    private static readonly Regex Flow=new(@"\b(TUN_TCP_FAILED|TUN_TCP_IO_FAILED|TUN_DNS_FAILED|DNS_RESOLVER_FAILED|LOCAL_FLOW_FAILED)\b|classification=(FLOW_IO_FAILURE|TRANSPORT_IO_FAILURE)|failed to process outbound traffic",RegexOptions.Compiled);
    private static readonly Regex HttpEof=new(@"proxy/http: failed to read http request > EOF\s*$",RegexOptions.Compiled);
    private static readonly Regex ErrorWords=new("error|ошибк|fail|cannot |rejected",RegexOptions.Compiled|RegexOptions.IgnoreCase);
    private static readonly Regex WarningWords=new("warn|предупреж",RegexOptions.Compiled|RegexOptions.IgnoreCase);
    private static readonly Regex FailureNote=new("FAILED|endpoint is closed for send|failed to process outbound traffic",RegexOptions.Compiled);

    public static string Level(string text,bool connected=false,bool expectedExit=false,bool windows10=false)
    {
        var annotated=Tagged.Match(text);
        if(annotated.Success)return annotated.Groups[1].Value;
        if(Critical.IsMatch(text))return "error";
        if(Regex.IsMatch(text,@"\bAWG_(PROXY|PACKET)_FAMILY_REJECTED\b"))return connected?"warning":"error";
        if(text.StartsWith("Country lookup failed for ",StringComparison.Ordinal))return "warning";
        // This code is emitted after returning ICMP unreachable for one failed UDP flow.
        // It does not mean that the TUN adapter or the VPN session was rejected.
        if(Regex.IsMatch(text,@"\bTUN_UDP_NETWORK_REJECTED\b"))return connected?"warning":"error";
        if(Regex.IsMatch(text,@"\bREALITY_RELAY_FAILED\b.*\bflow=\d+\b"))return connected?"warning":"error";
        if(Regex.IsMatch(text,@"\bAWG_FAMILY_DNS_OBSERVATION\b"))
            return Regex.IsMatch(text,@"\breason=(recorded|no_addresses)\b")?"info":"warning";
        if(text.Contains("TUN_UDP_POLICY_BLOCKED",StringComparison.Ordinal)&&text.Contains("reason=VISION_UDP443_BLOCKED",StringComparison.Ordinal))return "info";
        if(text.Contains("exited with code",StringComparison.OrdinalIgnoreCase))return expectedExit?"info":"error";
        if(text.Contains("LOCAL_PROXY_PROBE_CLOSED",StringComparison.Ordinal)||text.Contains("DIRECT_IPV4_FALLBACK_READY",StringComparison.Ordinal))return "info";
        if(Flow.IsMatch(text))return connected?"warning":"error";
        if(HttpEof.IsMatch(text))return "warning";
        if(text.Contains("endpoint is closed for send",StringComparison.OrdinalIgnoreCase))return connected?"warning":"error";
        if(ErrorWords.IsMatch(text))return "error";
        return WarningWords.IsMatch(text)?"warning":"info";
    }
    public static string Annotate(string text,bool connected,bool expectedExit,bool windows10=false)
    {
        var level=Level(text,connected,expectedExit,windows10);
        var note=level=="warning"&&connected&&(FailureNote.IsMatch(text)||text.Contains("TUN_UDP_NETWORK_REJECTED",StringComparison.Ordinal))?" · Отдельное соединение не удалось; VPN продолжает работать":"";
        return $"[IOR:{level}] {text}{note}";
    }
}
