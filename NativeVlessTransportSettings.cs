using System.Text.Json;
namespace HappBox;
public static class NativeVlessTransportSettings
{
    public static CoreVlessTransportOptions Parse(Dictionary<string,string> query,ProxyProfile profile)
    {
        query.TryGetValue("type", out var transport);
        var result = new CoreVlessTransportOptions();
        if (query.TryGetValue("mode", out var mode) && mode.Length>0)
        {
            if (transport == "grpc") result.MultiMode = mode == "multi";
            else result.Mode = mode;
        }
        if (query.TryGetValue("multiMode", out var multi)) result.MultiMode = multi is "1" or "true";
        if (query.TryGetValue("extra", out var extra))
        {
            var node=System.Text.Json.Nodes.JsonNode.Parse(extra)?.AsObject() ?? throw new FormatException("Некорректный extra VLESS.");
            var downloadSettings=node["downloadSettings"]?.DeepClone();node.Remove("downloadSettings");
            var settings = JsonSerializer.Deserialize<CoreVlessTransportOptions>(node.ToJsonString(), new JsonSerializerOptions { PropertyNameCaseInsensitive=true })
                ?? throw new FormatException("Пустые параметры extra VLESS.");
            result.Headers = settings.Headers;
            result.Download = settings.Download;
            result.Mode ??= settings.Mode;
            result.MultiMode |= settings.MultiMode;
            if(downloadSettings is not null)result.Download=Download(downloadSettings.AsObject(),profile);
        }
        return result;
    }
    public static CoreWireProfile Download(System.Text.Json.Nodes.JsonObject node,ProxyProfile parent)
    {
        string? Value(System.Text.Json.Nodes.JsonNode? n,string key)=>n?[key]?.GetValue<string>();
        if(node.Any(p=>p.Key is not ("address" or "port" or "network" or "security" or "tlsSettings" or "realitySettings" or "xhttpSettings")))throw new FormatException("Не поддержаны параметры downloadSettings.");
        var security=Value(node,"security") ?? (parent.RealityEnabled?"reality":"tls");
        if(security is not ("tls" or "reality"))throw new FormatException("Download требует TLS/REALITY.");
        var tls=node[security=="reality"?"realitySettings":"tlsSettings"];
        var transport=node["xhttpSettings"];
        if((Value(node,"network")??"xhttp")!="xhttp")throw new FormatException("Download этого выпуска требует XHTTP.");
        var extra=transport?["extra"]?.DeepClone()?.AsObject() ?? new System.Text.Json.Nodes.JsonObject();
        var options=JsonSerializer.Deserialize<CoreVlessTransportOptions>(extra.ToJsonString(),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
        options.Mode=Value(transport,"mode");
        if(options.Download is not null)throw new FormatException("Вложенный downloadSettings не поддерживается.");
        return new CoreWireProfile{Protocol=security=="reality"?"vless_reality":"vless_tls",Server=Value(node,"address")??parent.Server,Port=node["port"]?.GetValue<int>()??parent.Port,Credential=parent.UserId??"",ServerName=Value(tls,"serverName")??parent.ServerName??parent.Server,PublicKey=security=="reality"?Value(tls,"publicKey")??parent.PublicKey:null,ShortId=security=="reality"?Value(tls,"shortId")??parent.ShortId:null,Fingerprint=security=="reality"?Value(tls,"fingerprint")??parent.Fingerprint:null,Options=new CoreProtocolOptions{Transport="xhttp",Host=Value(transport,"host"),Path=Value(transport,"path")??parent.Path,VlessTransport=options}};
    }
}
