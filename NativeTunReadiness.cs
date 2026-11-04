using System.Diagnostics;
using System.Net.Sockets;

namespace HappBox;

public sealed record TunProbeStatus(string Status, string Code, long DurationMs);
public sealed record NativeTunReadinessResult(bool Ready, TunProbeStatus Https, TunProbeStatus Dns, TunProbeStatus Udp);

/// <summary>HTTPS is mandatory; optional probes have a shared start and bounded lifetimes.</summary>
public static class NativeTunReadiness
{
    public static async Task<NativeTunReadinessResult> CheckAsync(
        Func<CancellationToken,Task> https, Func<CancellationToken,Task> dns,
        Func<CancellationToken,Task> udp, CancellationToken token,
        TimeSpan? optionalTimeout = null, bool requireDns = false)
    {
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task<TunProbeStatus> Probe(string name,Func<CancellationToken,Task> probe,bool optional)
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            if(optional)deadline.CancelAfter(optionalTimeout??TimeSpan.FromSeconds(2));
            var watch=Stopwatch.StartNew();
            try {await probe(deadline.Token);return new("ready",name+"_OK",watch.ElapsedMilliseconds);}
            catch(OperationCanceledException)
            {return new("unverified",name+(lifetime.IsCancellationRequested?"_CANCELLED":"_TIMEOUT"),watch.ElapsedMilliseconds);}
            catch(Exception error) when(error is IOException or SocketException or HttpRequestException or InvalidOperationException)
            {
                var code=ProbeErrorCode(error);
                return new("error",code.Length==0?name+"_"+error.GetType().Name.ToUpperInvariant():code,watch.ElapsedMilliseconds);
            }
        }
        var dnsTask=Probe("DNS",dns,!requireDns);
        var udpTask=Probe("UDP",udp,true);
        var httpsTask=Probe("HTTPS",https,false);
        var result=await httpsTask;
        if(result.Status!="ready")lifetime.Cancel();
        await Task.WhenAll(dnsTask,udpTask);
        token.ThrowIfCancellationRequested();
        var dnsResult=await dnsTask;
        return new(result.Status=="ready"&&(!requireDns||dnsResult.Status=="ready"),result,dnsResult,await udpTask);
    }
    internal static string ProbeErrorCode(Exception error)
    {
        for(var depth=0;depth<8;depth++) {
            var code=System.Text.RegularExpressions.Regex.Match(error.Message,@"^[A-Z][A-Z0-9_]{3,80}$").Value;
            if(code.Length>0)return code;
            if(error.InnerException is not { } inner)break;
            error=inner;
        }
        return "";
    }
}
