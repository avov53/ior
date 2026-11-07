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

    public static string Level(string text,bool connected=false,bool expectedExit=false)
    {
        var annotated=Tagged.Match(text);
        if(annotated.Success)return annotated.Groups[1].Value;
        if(Critical.IsMatch(text))return "error";
        if(text.Contains("exited with code",StringComparison.OrdinalIgnoreCase))return expectedExit?"info":"error";
        if(text.Contains("LOCAL_PROXY_PROBE_CLOSED",StringComparison.Ordinal)||text.Contains("DIRECT_IPV4_FALLBACK_READY",StringComparison.Ordinal))return "info";
        if(Flow.IsMatch(text))return connected?"warning":"error";
        if(HttpEof.IsMatch(text))return "warning";
        if(text.Contains("endpoint is closed for send",StringComparison.OrdinalIgnoreCase))return connected?"warning":"error";
        if(ErrorWords.IsMatch(text))return "error";
        return WarningWords.IsMatch(text)?"warning":"info";
    }
    public static string Annotate(string text,bool connected,bool expectedExit)
    {
        var level=Level(text,connected,expectedExit);
        var note=level=="warning"&&connected&&FailureNote.IsMatch(text)?" · Отдельное соединение не удалось; VPN продолжает работать":"";
        return $"[IOR:{level}] {text}{note}";
    }
}
