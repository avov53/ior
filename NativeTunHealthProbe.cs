using System.Net;
using System.Net.NetworkInformation;

namespace HappBox;

public static class NativeTunHealthProbe
{
    public static IPAddress TrafficResolver(IPAddress adapterDns, IPAddress? upstream)
    {
        var resolver=upstream ?? IPAddress.Parse("8.8.8.8");
        if(resolver.Equals(adapterDns) || IPAddress.IsLoopback(resolver) || resolver.Equals(IPAddress.Any) || resolver.Equals(IPAddress.IPv6Any))
            throw new IOException("NATIVE_TUN_UDP_PROBE_ENDPOINT_INVALID");
        return resolver;
    }
    public static async Task<(bool Ready, string Reason)> CheckAsync(IPAddress server, string physical,
        string adapterName, IPAddress dns, CancellationToken token = default, bool dnsPolicyAlreadyVerified = false, bool ipv6Only = false,
        Action<string,int,long,string>? measured=null, Func<string,bool>? proxyTrafficConfirmed=null,bool hedged=true,
        IPAddress? trafficDnsEndpoint=null,bool fastStart=false,Action<NativeTunReadinessResult>? readiness=null,
        Func<string,bool>? proxyDnsConfirmed=null,Func<string,string,bool>? proxyPacketTrafficConfirmed=null)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(item => item.Name == adapterName);
        if (adapter is null || adapter.OperationalStatus != OperationalStatus.Up)
            return (false, "NATIVE_TUN_ADAPTER_NOT_READY");
        if (!adapter.GetIPProperties().DnsAddresses.Contains(dns))
            return (false, "NATIVE_TUN_DNS_NOT_APPLIED");
        if (!dnsPolicyAlreadyVerified && !await XrayTunRouteLease.VerifyNativeDnsPolicyAsync(adapterName,dns,token))
            return (false, "NATIVE_TUN_DNS_POLICY_NOT_APPLIED");
        // A port or an adapter alone does not prove that Windows routes traffic through it.
        if (!PhysicalInterfaceResolver.UsesInterfaceForIpv4(IPAddress.Parse("1.0.0.1"), adapterName))
            return (false, "NATIVE_TUN_DEFAULT_ROUTE_NOT_APPLIED");
        // The adapter DNS address is a synthetic on-link address, not an external
        // UDP destination. Keep checking its configuration above, but exercise
        // actual routed UDP against the profile's upstream resolver.
        var probeDns = TrafficResolver(dns,trafficDnsEndpoint);
        var routeValid=PhysicalInterfaceResolver.UsesInterfaceForAddress(probeDns,adapterName);
        if (!routeValid && !fastStart)
            return (false, "NATIVE_TUN_UDP_PROBE_ROUTE_INVALID");
        return await XrayTunHealthProbe.CheckAsync(server, physical, token, adapterName, probeDns, "ior-core",ipv6Only,measured,proxyTrafficConfirmed,hedged,
            fastStart,readiness,routeValid?null:"NATIVE_TUN_UDP_PROBE_ROUTE_INVALID",proxyDnsConfirmed,proxyPacketTrafficConfirmed);
    }
}
