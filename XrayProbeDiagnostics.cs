namespace HappBox;

public static class XrayProbeDiagnostics
{
    public static void Record(ConnectionDiagnostics diagnostics,string phase,int attempt,long durationMs,string result)
    {
        try
        {
            diagnostics.Record("probe","XRAY_TUN_PROBE_MEASURED",("phase",phase),("durationMs",durationMs.ToString()),("reason",result));
        }
        catch(Exception error)when(error is ArgumentException or IOException or UnauthorizedAccessException or ObjectDisposedException)
        { /* Optional measurements must never terminate an otherwise valid VPN connection. */ }
    }
}
