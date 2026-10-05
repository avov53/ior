using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace HappBox;

// Evidence belongs to one core process. A packet path must match the probe's
// actual local and remote endpoints; a SYN/ACK or unrelated flow is insufficient.
internal sealed class NativeProxyTrafficEvidence
{
    internal static string Endpoint(IPEndPoint endpoint)=>new IPEndPoint(
        endpoint.Address.IsIPv4MappedToIPv6?endpoint.Address.MapToIPv4():endpoint.Address,endpoint.Port).ToString();
    private readonly ConcurrentDictionary<ulong,string> _proxyFlows=new();
    private readonly ConcurrentDictionary<ulong,byte> _remoteData=new();
    private readonly ConcurrentDictionary<ulong,byte> _dnsData=new();

    public bool HasProxiedTraffic(string host)=>_proxyFlows.Any(item=>item.Value.StartsWith(host+":",StringComparison.OrdinalIgnoreCase)&&_remoteData.ContainsKey(item.Key));
    public bool HasProxiedDnsTraffic(string host)=>_proxyFlows.Any(item=>item.Value.Equals(host+":53",StringComparison.OrdinalIgnoreCase)&&_dnsData.ContainsKey(item.Key));
    public bool HasProxyRoute(string host)=>_proxyFlows.Values.Any(value=>value.StartsWith(host+":",StringComparison.OrdinalIgnoreCase));
    public bool HasProxiedPacketTraffic(string source,string destination)=>false;

    private static string? Safe(JsonElement json,string property)=>json.TryGetProperty(property,out var value)&&value.ValueKind==JsonValueKind.String&&
        value.GetString() is {Length:>0 and <=255} text&&text.All(c=>char.IsAsciiLetterOrDigit(c)||".:_-[]".Contains(c))?text:null;

    public void Observe(JsonElement json,string code)
    {
        if(!json.TryGetProperty("flowId",out var identity)||!identity.TryGetUInt64(out var id))return;
        var destination=Safe(json,"destination");
        var proxied=json.TryGetProperty("target",out var target)&&target.ValueKind==JsonValueKind.String&&target.GetString()=="PROXY";
        if(code=="ROUTE_SELECTED"&&proxied&&destination is not null)_proxyFlows[id]=destination;
        if(code=="TUN_TCP_FIRST_REMOTE_BYTE"&&_proxyFlows.ContainsKey(id))_remoteData[id]=0;
        if(code is "DNS_RESPONSE_READY" or "DNS_CACHE_HIT" or "DNS_IPV6_NODATA"&&_proxyFlows.ContainsKey(id))_dnsData[id]=0;
        if(_proxyFlows.Count>4096)foreach(var old in _proxyFlows.Keys.Where(key=>key<id&&id-key>2048)) {
            _proxyFlows.TryRemove(old,out _);_remoteData.TryRemove(old,out _);_dnsData.TryRemove(old,out _);
        }
    }
}
