using YamlDotNet.RepresentationModel;

namespace HappBox;

/// <summary>Reads a Clash subscription as one document, without executing YAML tags or provider URLs.</summary>
public static class ClashSubscriptionParser
{
    public sealed record Result(List<ProxyProfile> Profiles, int RejectedCount, HappRoutingProfile? Routing, int RemovedProtocolCount = 0);

    public static bool LooksLikeClash(string body) => IsJsonClash(body) || body.TrimStart('\uFEFF', ' ', '\t', '\r', '\n')
        .Split('\n').Any(line =>
        line.TrimStart('\uFEFF', ' ', '\t').StartsWith("proxies:", StringComparison.Ordinal) ||
        line.TrimStart('\uFEFF', ' ', '\t').StartsWith("proxy-providers:", StringComparison.Ordinal) ||
        line.TrimStart('\uFEFF', ' ', '\t').StartsWith("proxy-groups:", StringComparison.Ordinal));

    public static Result Parse(string yaml, string? name = null)
    {
        if (yaml.Length > 2_000_000) throw new FormatException("Clash-подписка слишком велика.");
        if(IsJsonClash(yaml))yaml=NormalizeJson(yaml);
        var stream = new YamlStream();
        try { stream.Load(new StringReader(yaml)); }
        catch (Exception ex) { throw new FormatException("Некорректный YAML Clash-подписки.", ex); }
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new FormatException("Ожидается один YAML-документ Clash.");

        var nodes = Get(root, "proxies") as YamlSequenceNode;
        if (nodes is null)
            throw new FormatException("Clash-подписка не содержит встроенного списка proxies. Подписки только с proxy-providers пока не импортируются.");

        var profiles = new List<ProxyProfile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejected = 0;
        var removed = 0;
        foreach (var node in nodes.Children)
        {
            if (node is not YamlMappingNode item) { rejected++; continue; }

            try
            {
                if(Get(item,"type") is YamlScalarNode type&&type.Value?.ToLowerInvariant() is "vmess" or "ss" or "shadowsocks" or "trojan"){removed++;rejected++;continue;}
                var profile = ParseProxy(item);
                if (!names.Add(profile.Name)) throw new FormatException("Повторяющееся имя Clash-сервера.");
                if(RemovedProtocolPolicy.IsRemoved(profile)){removed++;rejected++;}else profiles.Add(profile);
            }
            catch (FormatException) { rejected++; }
        }
        if (profiles.Count == 0 && removed == 0)
            throw new FormatException($"В Clash-подписке нет поддерживаемых встроенных серверов (отклонено: {rejected}). Предыдущее состояние сохранено.");

        HappRoutingProfile? routing = null;
        if (Get(root, "rules") is YamlSequenceNode rules && rules.Children.Count > 0)
            routing = ClashProfileParser.Parse(yaml, name);
        return new Result(profiles, rejected, routing, removed);
    }

    private static bool IsJsonClash(string body)
    {
        if(!body.TrimStart('\uFEFF',' ','\t','\r','\n').StartsWith('{'))return false;
        try { using var json=System.Text.Json.JsonDocument.Parse(body); return json.RootElement.TryGetProperty("proxies",out _) || json.RootElement.TryGetProperty("proxy-providers",out _); }
        catch(System.Text.Json.JsonException){return false;}
    }
    private static string NormalizeJson(string body)
    {
        using var json=System.Text.Json.JsonDocument.Parse(body);
        object? Convert(System.Text.Json.JsonElement node)=>node.ValueKind switch {
            System.Text.Json.JsonValueKind.Object=>node.EnumerateObject().ToDictionary(item=>item.Name,item=>Convert(item.Value)),
            System.Text.Json.JsonValueKind.Array=>node.EnumerateArray().Select(Convert).ToArray(),
            System.Text.Json.JsonValueKind.String=>node.GetString(),
            System.Text.Json.JsonValueKind.Number=>node.GetDecimal(),
            System.Text.Json.JsonValueKind.True=>true,System.Text.Json.JsonValueKind.False=>false,_=>null };
        return new YamlDotNet.Serialization.SerializerBuilder().Build().Serialize(Convert(json.RootElement));
    }

    private static ProxyProfile ParseProxy(YamlMappingNode item)
    {
        var type = Required(item, "type").ToLowerInvariant();
        var server = Required(item, "server");
        var port = int.TryParse(Required(item, "port"), out var parsedPort) ? parsedPort : 0;
        if (port is < 1 or > 65535) throw new FormatException("Некорректный порт Clash-сервера.");
        var profile = new ProxyProfile
        {
            Name = Value(item, "name") ?? server,
            Server = server,
            Port = port,
            ServerName = Value(item, "servername") ?? Value(item, "sni") ?? server,
            AllowInsecure = Bool(item, "skip-cert-verify")
        };
        switch (type)
        {
            case "socks5":
            case "http":
                profile.Protocol = type == "http" ? ProxyProtocol.Http : ProxyProtocol.Socks;
                profile.Username = Value(item, "username");
                profile.Password = Value(item, "password");
                profile.TlsEnabled = Bool(item, "tls");
                break;
            case "ss":
                profile.Protocol = ProxyProtocol.Shadowsocks;
                profile.Method = Required(item, "cipher"); profile.Password = Required(item, "password");
                profile.Plugin = Value(item, "plugin");
                if (Get(item,"plugin-opts") is YamlMappingNode pluginOptions) profile.UnsupportedParameters.Add("plugin-opts");
                break;
            case "vmess":
                profile.Protocol = ProxyProtocol.Vmess;
                profile.UserId = Required(item, "uuid");
                if (!Guid.TryParse(profile.UserId, out _)) throw new FormatException("Некорректный UUID VMess.");
                profile.Security = Value(item, "cipher") ?? "auto";
                profile.AlterId = int.TryParse(Value(item, "alterId"), out var alterId) ? alterId : 0;
                profile.TlsEnabled = Bool(item, "tls");
                ApplyStreamOptions(item, profile);
                break;
            case "trojan":
                profile.Protocol = ProxyProtocol.Trojan; profile.Password = Required(item, "password"); profile.TlsEnabled = true;
                ApplyStreamOptions(item, profile);
                break;
            case "awg":
            case "amneziawg":
            case "wireguard":
                profile.Protocol = ProxyProtocol.WireGuard;
                profile.PrivateKey = Required(item, "private-key"); profile.PeerPublicKey = Required(item, "public-key");
                profile.PreSharedKey = Value(item, "pre-shared-key");
                profile.LocalAddress = string.Join(",", new[] { Value(item, "ip"), Value(item, "ipv6") }.Where(value => !string.IsNullOrWhiteSpace(value)));
                if (string.IsNullOrEmpty(profile.LocalAddress)) throw new FormatException("WireGuard требует локальный адрес.");
                if (Get(item, "allowed-ips") is YamlSequenceNode allowed)
                    profile.AllowedIps = string.Join(",", allowed.Children.OfType<YamlScalarNode>().Select(node => node.Value));
                else profile.AllowedIps = "0.0.0.0/0,::/0";
                profile.Mtu = int.TryParse(Value(item, "mtu"), out var mtu) ? mtu : null;
                profile.PersistentKeepalive = int.TryParse(Value(item, "persistent-keepalive"), out var keepalive) ? keepalive : null;
                if (Get(item,"dns") is YamlSequenceNode tunnelDns) profile.TunnelDns = string.Join(",",tunnelDns.Children.OfType<YamlScalarNode>().Select(node=>node.Value));
                if (Get(item, "peers") is not null || Get(item, "reserved") is not null)
                    throw new FormatException("WireGuard peers/reserved ещё не представлены в модели.");
                break;
            case "vless":
                profile.Protocol = ProxyProtocol.Vless;
                profile.UserId = Required(item, "uuid");
                if (!Guid.TryParse(profile.UserId, out _)) throw new FormatException("Некорректный UUID VLESS.");
                profile.Flow = Value(item, "flow");
                if (profile.Flow is not (null or "xtls-rprx-vision" or "xtls-rprx-vision-udp443"))
                    throw new FormatException("Неподдерживаемый VLESS flow.");
                profile.TlsEnabled = Bool(item, "tls") || Get(item, "reality-opts") is not null;
                if (Get(item, "reality-opts") is not null and not YamlMappingNode)
                    throw new FormatException("Некорректные reality-opts.");
                if (Get(item, "reality-opts") is YamlMappingNode reality)
                {
                    profile.RealityEnabled = true;
                    profile.TlsEnabled = true;
                    profile.PublicKey = Required(reality, "public-key");
                    profile.ShortId = Value(reality, "short-id");
                }
                profile.Fingerprint = Value(item, "client-fingerprint") ?? "chrome";
                ApplyStreamOptions(item, profile);
                break;
            case "hysteria2":
            case "hy2":
                profile.Protocol = ProxyProtocol.Hysteria2;
                profile.Password = Required(item, "password");
                profile.TlsEnabled = true;
                profile.ObfsType = Value(item, "obfs");
                profile.ObfsPassword = Value(item, "obfs-password");
                if (Get(item, "ports") is not null)
                    throw new FormatException("Переключение портов Hysteria2 пока не представлено в модели ior.");
                break;
            default:
                throw new FormatException($"Неподдерживаемый тип Clash-сервера: {type}.");
        }
        profile.Awg=item.Children.Where(field=>field.Key is YamlScalarNode key && RemovedProtocolPolicy.IsParameter(key.Value??"")).ToDictionary(field=>ProfileParser.AwgParameterNames.First(pair=>pair.Key.Equals(((YamlScalarNode)field.Key).Value,StringComparison.OrdinalIgnoreCase)||pair.Value.Equals(((YamlScalarNode)field.Key).Value,StringComparison.OrdinalIgnoreCase)).Value,field=>((YamlScalarNode)field.Value).Value??"");
        if(type is "awg" or "amneziawg" || profile.Awg.Count>0)profile.Protocol=ProxyProtocol.AmneziaWG;
        var known=new HashSet<string>(["name","type","server","port","servername","sni","skip-cert-verify","udp","network","tls","client-fingerprint","ws-opts","grpc-opts","xhttp-opts","httpupgrade-opts","username","password","cipher","uuid","alterId","plugin","plugin-opts","flow","reality-opts","private-key","public-key","pre-shared-key","ip","ipv6","allowed-ips","mtu","persistent-keepalive","dns","obfs","obfs-password"],StringComparer.Ordinal);
        foreach(var pair in ProfileParser.AwgParameterNames){known.Add(pair.Key);known.Add(pair.Value);}
        profile.UnsupportedParameters.AddRange(item.Children.Keys.OfType<YamlScalarNode>().Select(key=>key.Value??"").Where(key=>!known.Contains(key)));
        return profile;
    }

    private static void ApplyStreamOptions(YamlMappingNode item, ProxyProfile profile)
    {
        profile.Network = Value(item, "network") ?? "tcp";
        switch (profile.Network.ToLowerInvariant())
        {
            case "tcp": case "raw": break;
            case "ws":
                if (Get(item, "ws-opts") is YamlMappingNode ws)
                {
                    profile.Path = Value(ws, "path") ?? "/";
                    if (Get(ws, "headers") is YamlMappingNode headers)
                    {
                        profile.VlessTransport ??= new();
                        foreach(var pair in headers.Children){var key=((YamlScalarNode)pair.Key).Value??"";if(!key.Equals("Host",StringComparison.OrdinalIgnoreCase))profile.VlessTransport.Headers[key]=((YamlScalarNode)pair.Value).Value??"";}
                        profile.Host = headers.Children.FirstOrDefault(pair => (pair.Key as YamlScalarNode)?.Value?.Equals("Host", StringComparison.OrdinalIgnoreCase) == true).Value is YamlScalarNode host ? host.Value : null;
                    }
                    if (Get(ws, "max-early-data") is not null) throw new FormatException("WS early data пока не поддерживается.");
                }
                break;
            case "grpc":
                if (Get(item, "grpc-opts") is YamlMappingNode grpc){profile.ServiceName = Value(grpc, "grpc-service-name");profile.Host=Value(grpc,"authority");profile.VlessTransport=new(){MultiMode=Bool(grpc,"multi-mode")};}
                break;
            case "httpupgrade": case "xhttp":
                if(Get(item,profile.Network.ToLowerInvariant()+"-opts") is YamlMappingNode options){
                    profile.Path=Value(options,"path")??"/";profile.Host=Value(options,"host");
                    var query=new Dictionary<string,string>{{"type",profile.Network.ToLowerInvariant()}};
                    if(Value(options,"mode") is {} mode)query["mode"]=mode;
                    if(Get(options,"extra") is {} extra)query["extra"]=TransportJson(extra).ToJsonString();
                    profile.VlessTransport=NativeVlessTransportSettings.Parse(query,profile);
                    if(Get(options,"headers") is YamlMappingNode headers)foreach(var pair in headers.Children){var name=((YamlScalarNode)pair.Key).Value??"";var value=((YamlScalarNode)pair.Value).Value??"";if(name.Equals("Host",StringComparison.OrdinalIgnoreCase))profile.Host=value;else profile.VlessTransport.Headers[name]=value;}
                }
                break;
            default: throw new FormatException($"Неподдерживаемый Clash-транспорт {profile.Network}.");
        }
    }
    private static System.Text.Json.Nodes.JsonNode TransportJson(YamlNode node)=>node switch {
        YamlMappingNode map=>new System.Text.Json.Nodes.JsonObject(map.Children.Select(p=>new KeyValuePair<string,System.Text.Json.Nodes.JsonNode?>(((YamlScalarNode)p.Key).Value??"",TransportJson(p.Value)))),
        YamlSequenceNode sequence=>new System.Text.Json.Nodes.JsonArray(sequence.Children.Select(TransportJson).ToArray()),
        YamlScalarNode scalar when int.TryParse(scalar.Value,out var number)=>System.Text.Json.Nodes.JsonValue.Create(number)!,
        YamlScalarNode scalar when bool.TryParse(scalar.Value,out var boolean)=>System.Text.Json.Nodes.JsonValue.Create(boolean)!,
        YamlScalarNode scalar=>System.Text.Json.Nodes.JsonValue.Create(scalar.Value??"")!,_=>throw new FormatException("Некорректный транспорт")};

    private static YamlNode? Get(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    private static string? Value(YamlMappingNode map, string key) =>
        (Get(map, key) as YamlScalarNode)?.Value?.Trim();

    private static string Required(YamlMappingNode map, string key) =>
        !string.IsNullOrWhiteSpace(Value(map, key)) ? Value(map, key)! : throw new FormatException($"Отсутствует поле {key} в Clash-сервере.");

    private static bool Bool(YamlMappingNode map, string key) =>
        bool.TryParse(Value(map, key), out var value) && value;
}
