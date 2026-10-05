using System.Text.Json;

namespace HappBox;

/// <summary>Imports explicit JSON server collections, sing-box and Xray outbounds.</summary>
public static class JsonSubscriptionParser
{
    public static ProfileParseResult Parse(string text)
    {
        if(text.Length>2_000_000)throw new FormatException("JSON-подписка слишком велика.");
        using var document=JsonDocument.Parse(text,new JsonDocumentOptions {MaxDepth=48});
        var root=document.RootElement;
        var nodes=root.ValueKind==JsonValueKind.Array?root:
            Get(root,"outbounds").ValueKind==JsonValueKind.Array?Get(root,"outbounds"):
            Get(root,"profiles").ValueKind==JsonValueKind.Array?Get(root,"profiles"):
            Get(root,"servers").ValueKind==JsonValueKind.Array?Get(root,"servers"):default;
        var profiles=new List<ProxyProfile>();var rejected=0;var removed=0;
        foreach(var node in nodes.ValueKind==JsonValueKind.Array?nodes.EnumerateArray().ToArray():new[]{root})
        {
            try
            {
                if(node.ValueKind==JsonValueKind.String)
                { var parsed=ProfileParser.ParseManyDetailed(node.GetString()!);profiles.AddRange(parsed.Profiles);rejected+=parsed.RejectedCount;removed+=parsed.RemovedProtocolCount;continue; }
                if (RemovedProtocolPolicy.IsJson(node)) { rejected++; removed++; continue; }
                var type=String(node,"type")??String(node,"protocol")??"";
                if(type is "direct" or "block" or "dns" or "freedom" or "blackhole" or "selector" or "urltest")continue;
                var result=type is "wireguard" ? Tunnel(node,type) : String(node,"protocol") is not null?Xray(node,type):SingBox(node,type);
                if(result is null){rejected++;continue;}
                if(result.Port is <1 or >65535 || string.IsNullOrWhiteSpace(result.Server))throw new FormatException("Некорректный JSON-сервер.");
                profiles.Add(result);
            }
            catch(Exception error) when(error is FormatException or InvalidOperationException or JsonException){rejected++;}
        }
        if(profiles.Count==0 && removed==0)throw new FormatException("JSON-подписка не содержит поддерживаемых серверов.");
        return new ProfileParseResult(profiles,rejected,removed);
    }
    private static ProxyProfile? SingBox(JsonElement node,string type)
    {
        var kind=type switch {"vless"=>ProxyProtocol.Vless,"vmess"=>ProxyProtocol.Vmess,"trojan"=>ProxyProtocol.Trojan,
            "shadowsocks"=>ProxyProtocol.Shadowsocks,"hysteria2"=>ProxyProtocol.Hysteria2,"socks"=>ProxyProtocol.Socks,"http"=>ProxyProtocol.Http,_=>(ProxyProtocol?)null};
        if(kind is null)return null;
        var tls=Get(node,"tls");var reality=Get(tls,"reality");var transport=Get(node,"transport");
        if(Get(node,"server_ports").ValueKind!=JsonValueKind.Undefined)
            throw new FormatException("JSON port hopping ещё не представлен в профиле.");
        var profile=new ProxyProfile {Protocol=kind.Value,Name=String(node,"tag")??String(node,"name")??String(node,"server")??type,
            Server=String(node,"server")??"",Port=Number(node,"server_port"),UserId=String(node,"uuid"),
            Password=String(node,"password"),Username=String(node,"username"),Method=String(node,"method"),Plugin=String(node,"plugin"),Security=String(node,"security"),AlterId=Number(node,"alter_id"),
            Flow=String(node,"flow"),TlsEnabled=Boolean(tls,"enabled"),RealityEnabled=Boolean(reality,"enabled"),
            AllowInsecure=Boolean(tls,"insecure"),ServerName=String(tls,"server_name")??String(node,"server"),
            PublicKey=String(reality,"public_key"),ShortId=String(reality,"short_id"),Fingerprint=String(Get(tls,"utls"),"fingerprint"),
            Network=String(transport,"type")??"tcp",Path=String(transport,"path"),ServiceName=String(transport,"service_name"),
            ObfsType=String(Get(node,"obfs"),"type"),ObfsPassword=String(Get(node,"obfs"),"password")};
        var known=new HashSet<string>(["type","tag","name","server","server_port","uuid","password","username","method","plugin","security","alter_id","flow","tls","transport","obfs"],StringComparer.Ordinal);
        profile.UnsupportedParameters.AddRange(node.EnumerateObject().Where(field=>!known.Contains(field.Name)).Select(field=>field.Name));
        return profile;
    }
    private static ProxyProfile Tunnel(JsonElement node,string type)
    {
        string? List(string key) { var value=Get(node,key);return value.ValueKind==JsonValueKind.Array ? string.Join(",",value.EnumerateArray().Select(item=>item.GetString())) : String(node,key); }
        var profile=new ProxyProfile { Name=String(node,"tag")??String(node,"name")??type,Protocol=ProxyProtocol.WireGuard,
            Server=String(node,"server")??"",Port=Number(node,"server_port"),ServerName=String(node,"server"),PrivateKey=String(node,"private_key"),
            PeerPublicKey=String(node,"peer_public_key"),PreSharedKey=String(node,"pre_shared_key"),LocalAddress=List("local_address"),AllowedIps=List("allowed_ips")??"0.0.0.0/0,::/0",
            TunnelDns=List("dns"),Mtu=Number(node,"mtu") is var mtu && mtu!=0?mtu:null,PersistentKeepalive=Number(node,"persistent_keepalive_interval") };
        if(Get(node,"peers").ValueKind!=JsonValueKind.Undefined)profile.UnsupportedParameters.Add("peers");
        var settings=Get(node,"settings");
        if(settings.ValueKind==JsonValueKind.Object)
        {
            var peers=Get(settings,"peers");
            if(peers.ValueKind!=JsonValueKind.Array || peers.GetArrayLength()!=1)profile.UnsupportedParameters.Add("settings.peers");
            else {var peer=peers[0];var endpoint=String(peer,"endpoint");if(endpoint is not null && Uri.TryCreate("udp://"+endpoint,UriKind.Absolute,out var uri)){profile.Server=uri.Host;profile.ServerName=uri.Host;profile.Port=uri.Port;}
                profile.PrivateKey=String(settings,"secretKey");profile.PeerPublicKey=String(peer,"publicKey");profile.PreSharedKey=String(peer,"preSharedKey");
                var addresses=Get(settings,"address");profile.LocalAddress=addresses.ValueKind==JsonValueKind.Array?string.Join(",",addresses.EnumerateArray().Select(item=>item.GetString())):null;
                var allowed=Get(peer,"allowedIPs");profile.AllowedIps=allowed.ValueKind==JsonValueKind.Array?string.Join(",",allowed.EnumerateArray().Select(item=>item.GetString())):"0.0.0.0/0,::/0";
                profile.Mtu=Number(settings,"mtu") is var m && m!=0?m:null;profile.PersistentKeepalive=Number(peer,"keepAlive");
                foreach(var field in settings.EnumerateObject().Where(field=>field.Name is not ("secretKey" or "peers" or "address" or "mtu")))profile.UnsupportedParameters.Add("settings."+field.Name);
                foreach(var field in peer.EnumerateObject().Where(field=>field.Name is not ("endpoint" or "publicKey" or "preSharedKey" or "allowedIPs" or "keepAlive")))profile.UnsupportedParameters.Add("settings.peers."+field.Name);
            }
        }
        var known=new HashSet<string>(["type","protocol","tag","name","server","server_port","private_key","peer_public_key","pre_shared_key","local_address","allowed_ips","dns","mtu","persistent_keepalive_interval","settings"],StringComparer.Ordinal);
        foreach(var pair in ProfileParser.AwgParameterNames){known.Add(pair.Key);known.Add(pair.Value);}
        profile.UnsupportedParameters.AddRange(node.EnumerateObject().Where(field=>!known.Contains(field.Name) && field.Name!="peers").Select(field=>field.Name));
        return profile;
    }
    private static ProxyProfile? Xray(JsonElement node,string type)
    {
        var kind=type switch {"vless"=>ProxyProtocol.Vless,"vmess"=>ProxyProtocol.Vmess,"trojan"=>ProxyProtocol.Trojan,
            "shadowsocks"=>ProxyProtocol.Shadowsocks,"hysteria" or "hysteria2"=>ProxyProtocol.Hysteria2,
            "socks"=>ProxyProtocol.Socks,"http"=>ProxyProtocol.Http,_=>(ProxyProtocol?)null};
        if(kind is null)return null;
        var settings=Get(node,"settings");var stream=Get(node,"streamSettings");
        var servers=Get(settings,kind is ProxyProtocol.Vless or ProxyProtocol.Vmess?"vnext":"servers");
        if(servers.ValueKind==JsonValueKind.Array && servers.GetArrayLength()!=1)
            throw new FormatException("Многосерверный Xray outbound требует отдельной модели группы.");
        var server=servers.ValueKind==JsonValueKind.Array && servers.GetArrayLength()==1?servers[0]:settings;
        var users=Get(server,"users");
        if(users.ValueKind==JsonValueKind.Array && users.GetArrayLength()>1)throw new FormatException("Несколько пользователей Xray не поддерживаются одним профилем.");
        var user=users.ValueKind==JsonValueKind.Array && users.GetArrayLength()==1?users[0]:server;
        var security=String(stream,"security");var tls=Get(stream,security=="reality"?"realitySettings":"tlsSettings");
        var network=String(stream,"network")??String(stream,"method")??"tcp";
        var transport=Get(stream,network=="grpc"?"grpcSettings":"wsSettings");
        return new ProxyProfile {Protocol=kind.Value,Name=String(node,"tag")??String(server,"address")??type,
            Server=String(server,"address")??"",Port=Number(server,"port"),UserId=String(user,"id"),
            Password=kind==ProxyProtocol.Hysteria2?String(Get(stream,"hysteriaSettings"),"auth"):String(user,"password"),
            Username=String(user,"user"),Method=String(user,"method"),Flow=String(user,"flow"),
            Security=String(user,"security"),AlterId=Number(user,"alterId"),TlsEnabled=security is "tls" or "reality",
            RealityEnabled=security=="reality",AllowInsecure=Boolean(tls,"allowInsecure"),
            ServerName=String(tls,"serverName")??String(server,"address"),PublicKey=String(tls,"publicKey")??String(tls,"password"),
            ShortId=String(tls,"shortId"),Fingerprint=String(tls,"fingerprint"),Network=network,
            Path=String(transport,"path"),Host=String(Get(transport,"headers"),"Host"),ServiceName=String(transport,"serviceName")};
    }
    private static JsonElement Get(JsonElement node,string property)=>node.ValueKind==JsonValueKind.Object && node.TryGetProperty(property,out var value)?value:default;
    private static string? String(JsonElement node,string property)=>Get(node,property) is var value && value.ValueKind==JsonValueKind.String?value.GetString():null;
    private static int Number(JsonElement node,string property)=>Get(node,property) is var value && value.ValueKind==JsonValueKind.Number && value.TryGetInt32(out var number)?number:0;
    private static bool Boolean(JsonElement node,string property)=>Get(node,property).ValueKind==JsonValueKind.True;
}
