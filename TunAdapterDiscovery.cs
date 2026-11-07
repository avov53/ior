using System.Net.NetworkInformation;
namespace HappBox;

public sealed record TunAdapterState(string Name, OperationalStatus Status);
public static class TunAdapterDiscovery
{
    // Windows can publish an installed Wintun interface after the core's local
    // proxy is ready. Observe the exact owned name; never substitute another VPN.
    public static async Task<bool> WaitAsync(string name, CancellationToken token=default,
        Func<TunAdapterState[]>? inspect=null, TimeSpan? limit=null, Func<bool>? coreAlive=null)
    {
        inspect ??=()=>NetworkInterface.GetAllNetworkInterfaces().Select(n=>new TunAdapterState(n.Name,n.OperationalStatus)).ToArray();
        var watch=System.Diagnostics.Stopwatch.StartNew();
        var budget=limit??TimeSpan.FromSeconds(10);
        while(true){
            token.ThrowIfCancellationRequested();
            if(coreAlive is not null&&!coreAlive())return false;
            if(inspect().Any(n=>n.Name.Equals(name,StringComparison.OrdinalIgnoreCase)&&n.Status==OperationalStatus.Up))return true;
            var remaining=budget-watch.Elapsed;if(remaining<=TimeSpan.Zero)return false;
            await Task.Delay(remaining<TimeSpan.FromMilliseconds(100)?remaining:TimeSpan.FromMilliseconds(100),token);
        }
    }
    public static string DescribeOwnedAdapters()=>string.Join(", ",NetworkInterface.GetAllNetworkInterfaces()
        .Where(n=>n.Name.StartsWith("ior-",StringComparison.OrdinalIgnoreCase))
        .Select(n=>n.Name+"="+n.OperationalStatus+"; id="+n.Id+"; type="+n.NetworkInterfaceType));
}
