using System.Net;
using System.Text.Json;

namespace HappBox;

/// <summary>Version 3 of the private C# ↔ Rust payload: DNS lists and physical addresses of both families.</summary>
public static class CoreRuntimeContract
{
    public const int ApiVersion = 3;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool TryCreateStart(AppState state, ProxyProfile profile, out CoreStartRequest? request,
        out string reason)
    {
        request = null;
        if (RemovedProtocolPolicy.IsRemoved(profile)) { reason = RemovedProtocolPolicy.Message; return false; }
        if (profile.UnsupportedParameters.Count > 0)
        { reason = "Неподдерживаемый параметр: " + string.Join(", ", profile.UnsupportedParameters); return false; }
        var protocol = profile.Protocol switch
        {
            ProxyProtocol.Vless when profile.RealityEnabled => "vless_reality",
            ProxyProtocol.Vless when profile.TlsEnabled => "vless_tls",
            ProxyProtocol.Hysteria2 => "hysteria2",
            ProxyProtocol.Vmess => "vmess",
            ProxyProtocol.Shadowsocks => "shadowsocks",
            ProxyProtocol.Trojan => "trojan",
            ProxyProtocol.WireGuard => "wireguard",
            ProxyProtocol.AmneziaWG => "amneziawg",
            _ => null
        };
        if (protocol is null)
        { reason = $"Сетевой контракт ior-core пока не поддерживает {profile.Protocol} без TLS/Reality."; return false; }
        if (protocol.StartsWith("vless", StringComparison.Ordinal))
        {
            var transport = (profile.Network ?? "tcp").ToLowerInvariant();
            if (transport is "" or "tcp") transport = "raw";
            if (transport == "websocket") transport = "ws";
            if (transport is not ("raw" or "xhttp" or "grpc" or "ws" or "httpupgrade"))
            { reason = "Транспорт VLESS не поддерживается: " + transport; return false; }
            if (profile.RealityEnabled && transport is "ws" or "httpupgrade")
            { reason = "REALITY несовместим с WebSocket/HTTPUpgrade."; return false; }
            if (!string.IsNullOrWhiteSpace(profile.Flow) && (transport != "raw" || !profile.RealityEnabled))
            { reason = "Vision этого выпуска доступен для RAW/REALITY."; return false; }
        }
        var tunnel = protocol is "wireguard" or "amneziawg";
        if(protocol=="amneziawg" && !Enum.IsDefined(state.AwgEngine)){reason="Неизвестный движок AWG.";return false;}
        Dictionary<string,string>? awg=null;
        if(protocol=="amneziawg")try{awg=AwgProfileParameters.Normalize(profile.Awg);}catch(FormatException error){reason=error.Message;return false;}
        var identity = protocol is "vless_tls" or "vless_reality" or "vmess";
        if (string.IsNullOrWhiteSpace(profile.Server) || profile.Port is < 1 or > 65535 ||
            (identity ? !Guid.TryParse(profile.UserId, out _) : tunnel ? !IsKey(profile.PrivateKey) : string.IsNullOrWhiteSpace(profile.Password)))
        { reason = "Профиль ior-core не содержит корректный адрес, SNI или учётные данные."; return false; }
        if (protocol is "vmess" or "trojan" && !string.IsNullOrEmpty(profile.Network) && !profile.Network.Equals("tcp", StringComparison.OrdinalIgnoreCase))
        { reason = "Транспорт ior-core пока не поддерживается: " + profile.Network; return false; }
        if (protocol is "vmess" or "trojan" && (!string.IsNullOrEmpty(profile.Host) || !string.IsNullOrEmpty(profile.Path) || !string.IsNullOrEmpty(profile.ServiceName)))
        { reason = "Параметры Host/Path/ServiceName требуют поддерживаемого транспорта."; return false; }
        if (protocol == "vmess" && (profile.AlterId ?? 0) != 0)
        { reason = "VMess alterId: поддерживается AEAD с alterId=0."; return false; }
        if (protocol == "vmess" && (profile.Security ?? "auto") is not ("auto" or "aes-128-gcm" or "chacha20-poly1305" or "none"))
        { reason = "VMess security не поддерживается: " + profile.Security; return false; }
        if (protocol == "trojan" && !profile.TlsEnabled)
        { reason = "Trojan требует TLS."; return false; }
        if (protocol is "vmess" or "trojan" && profile.RealityEnabled)
        { reason = "Reality поддерживается для VLESS; параметры этого профиля не поддерживаются."; return false; }
        if (protocol == "shadowsocks" && !string.IsNullOrWhiteSpace(profile.Plugin))
        { reason = "Shadowsocks plugin не подключён: " + profile.Plugin.Split(';')[0]; return false; }
        if (protocol == "shadowsocks" && profile.Method is not ("aes-128-gcm" or "aes-256-gcm" or "chacha20-ietf-poly1305" or "2022-blake3-aes-128-gcm" or "2022-blake3-aes-256-gcm" or "2022-blake3-chacha20-poly1305"))
        { reason = "Shadowsocks cipher не поддерживается: " + profile.Method; return false; }
        if (protocol == "shadowsocks" && profile.Method!.StartsWith("2022-",StringComparison.Ordinal) && !IsShadowsocks2022Key(profile.Password,profile.Method))
        { reason = "Shadowsocks 2022: пароль должен быть Base64-ключом правильной длины; цепочки EIH пока не поддерживаются."; return false; }
        if (tunnel && (!IsKey(profile.PeerPublicKey) || (!string.IsNullOrEmpty(profile.PreSharedKey) && !IsKey(profile.PreSharedKey)) ||
            SplitList(profile.LocalAddress).Length == 0 || SplitList(profile.AllowedIps).Length == 0 ||
            profile.PersistentKeepalive is < 0 or > 65535 || profile.Mtu is < 1280 or > 1500))
        { reason = "WireGuard: некорректный ключ, локальный адрес, AllowedIPs, keepalive или MTU."; return false; }
        if (tunnel && (SplitList(profile.LocalAddress).Any(value => !IsCidr(value.Contains('/')?value:value+(value.Contains(':')?"/128":"/32"))) ||
            SplitList(profile.AllowedIps).Any(value => !IsCidr(value)) || !SplitList(profile.LocalAddress).Any(local=>SplitList(profile.AllowedIps).Any(cidr=>local.Contains(':')==cidr.Contains(':')))))
        { reason = "WireGuard требует корректные адреса и совместимую семью AllowedIPs."; return false; }
        if (protocol == "vless_reality" && string.IsNullOrWhiteSpace(profile.PublicKey))
        { reason = "VLESS Reality требует public key."; return false; }
        if (protocol == "hysteria2") {
            var obfs=profile.ObfsType?.ToLowerInvariant() ?? "";
            if(obfs is not ("" or "none" or "salamander")){reason="Тип obfs Hysteria2 не поддерживается.";return false;}
            var bytes=System.Text.Encoding.UTF8.GetByteCount(profile.ObfsPassword ?? "");
            if(obfs=="salamander" ? bytes is <4 or >4096 : bytes!=0){reason="Неверный пароль obfs Hysteria2.";return false;}
        }
        var awgTun=protocol=="amneziawg" && state.SelectedCore==CoreKind.IorCore && state.TunEnabled;
        var route = RoutingSelectionPolicy.Effective(state);
        if (state.RoutingEnabled && !string.IsNullOrWhiteSpace(state.SelectedHappRoutingProfileId) && route is null)
        { reason = "Выбранный маршрут не найден."; return false; }
        IReadOnlyList<OrderedRouteRule> rules;
        if (route is null || awgTun && AwgClientPolicy.EmptyRoute(route))
            rules = [new OrderedRouteRule { Kind = "MATCH", Target = "PROXY", Source = RoutingProfileSource.Ior }];
        else if (!OrderedRoutePlan.TryCreate(route, out rules, out reason)) return false;
        // AWG is the default VPN route. Copy the final rule; saved profiles retain their original policy.
        if (awgTun) rules=rules.Select(rule=>rule.Kind.Equals("MATCH",StringComparison.OrdinalIgnoreCase)
            ? new OrderedRouteRule {Kind="MATCH",Target="PROXY",Source=rule.Source,Raw="AWG final"} : rule).ToArray();
        if(rules.Any(rule=>rule.Parameters.Any(parameter=>!parameter.Equals("no-resolve",StringComparison.OrdinalIgnoreCase))))
        { reason="Неподдерживаемый параметр правила маршрутизации.";return false; }
        try { rules=rules.SelectMany(rule=>NativeRuleProviderCache.Expand(rule,route)).SelectMany(rule=>NativeGeoData.Expand(rule,Path.Combine(AppContext.BaseDirectory,"core","xray"))).ToArray(); }
        catch(Exception error) when(error is FormatException or IOException or OverflowException)
        { reason=error.Message;return false; }
        var fullAwgProxy=awgTun && rules.Count==1 && rules[0].Kind.Equals("MATCH",StringComparison.OrdinalIgnoreCase) && rules[0].Target.Equals("PROXY",StringComparison.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if(route?.RoutePlan?.Groups.Any(group=>group.Name.Equals(rule.Target,StringComparison.OrdinalIgnoreCase))==true)
            { reason="Группа Clash пока не исполняется собственным ядром: "+rule.Target;return false; }
            if(rule.Parameters.Any(parameter=>!parameter.Equals("no-resolve",StringComparison.OrdinalIgnoreCase)))
            { reason="Неподдерживаемый параметр правила: "+rule.Raw;return false; }
            if (rule.Kind.ToUpperInvariant() is not ("MATCH" or "DOMAIN" or "DOMAIN-SUFFIX" or "DOMAIN-KEYWORD" or "IP-CIDR" or "IP-SET" or "REGEXP") ||
                rule.Target.ToUpperInvariant() is not ("PROXY" or "DIRECT" or "REJECT"))
            { reason = $"Правило пока не имеет точного ior-core-эквивалента: {rule.Raw}"; return false; }
        }
        var bootstrap = BootstrapDnsResolver.ServerAddress(state.BootstrapDnsServer).ToString();
        string[] upstreams;
        try {
            upstreams = TunnelDnsPolicy.Effective(state, profile).Servers;
        }
        catch (InvalidOperationException error) { reason=error.Message;return false; }
        if(upstreams.Length is <1 or >4 || upstreams.Any(value=>!IPAddress.TryParse(value,out _)))
        { reason="ior-core DNS: требуется от одного до четырёх IP-адресов серверов.";return false; }
        var upstreamValue = upstreams[0];
        if(tunnel && upstreams.Any(value=> {
            var address=IPAddress.Parse(value);
            return !SplitList(profile.LocalAddress).Any(local=>local.Contains(':')==(address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetworkV6)) ||
                !SplitList(profile.AllowedIps).Any(cidr=>IPNetwork.Parse(cidr).Contains(address));
        }))
        {reason="DNS_UPSTREAM_OUTSIDE_TUNNEL: DNS-сервер несовместим с локальными адресами или AllowedIPs профиля.";return false;}
        if (!IPAddress.TryParse(upstreamValue, out var upstream))
        { reason = "Первый сетевой контракт ior-core требует IP-адрес DNS upstream."; return false; }
        request = new CoreStartRequest
        {
            ApiVersion = ApiVersion,
            RequestId = Guid.NewGuid().ToString("N"),
            Operation = "start",
            Profile = new CoreWireProfile
            {
                Protocol = protocol, Server = profile.Server, Port = profile.Port,
                Credential = tunnel ? profile.PrivateKey! : identity ? profile.UserId! : profile.Password!,
                ServerName = profile.ServerName ?? profile.Server,
                PublicKey = protocol == "vless_reality" ? profile.PublicKey : null,
                ShortId = protocol == "vless_reality" ? profile.ShortId : null,
                Flow = protocol == "vless_reality" ? (state.SelectedCore == CoreKind.IorCore && state.RealityUdp443Enabled && profile.Flow == "xtls-rprx-vision" ? "xtls-rprx-vision-udp443" : profile.Flow) : null,
                Fingerprint = protocol == "vless_reality" ? profile.Fingerprint : null,
                Options = new CoreProtocolOptions {
                    Obfs = protocol=="hysteria2" ? profile.ObfsType?.ToLowerInvariant() : null,
                    ObfsPassword = protocol=="hysteria2" ? profile.ObfsPassword : null,
                    Method = protocol == "vmess" ? profile.Security ?? "auto" : profile.Method,
                    AlterId = protocol == "vmess" ? profile.AlterId : null, Tls = profile.TlsEnabled, Transport = profile.Network,
                    Host = profile.Host, Path = profile.Path, ServiceName = profile.ServiceName, VlessTransport = profile.VlessTransport, Alpn=profile.Alpn,
                    LocalAddresses = tunnel ? SplitList(profile.LocalAddress) : null,
                    AllowedIps = tunnel ? SplitList(profile.AllowedIps) : null, PeerPublicKey = profile.PeerPublicKey,
                    Awg = awg,
                    AwgRoutingMode = protocol=="amneziawg" ? fullAwgProxy ? "fullProxy" : "rules" : null,
                    AwgEngine = protocol=="amneziawg" ? state.AwgEngine==HappBox.AwgEngine.Rust ? "rust" : "go" : null,
                    AwgAdapterPath = protocol == "amneziawg" && state.AwgEngine==HappBox.AwgEngine.Go ? NativeProtocolComponents.AwgPath : null,
                    AwgAdapterSha256 = protocol == "amneziawg" && state.AwgEngine==HappBox.AwgEngine.Go ? NativeProtocolComponents.AwgSha256 : null,
                    PresharedKey = profile.PreSharedKey, Keepalive = profile.PersistentKeepalive, Mtu = profile.Mtu
                }
            },
            Dns = new CoreWireDns { Bootstrap = bootstrap, Upstream = upstream.ToString(), Upstreams=upstreams.Distinct().ToList(), Ipv4Only = !tunnel ||
                !SplitList(profile.LocalAddress).Any(value => IPAddress.TryParse(value.Split('/')[0], out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) ||
                !SplitList(profile.AllowedIps).Any(value => value.Contains(':')) },
            Routes = rules.Select(rule => new CoreWireRule { Kind = rule.Kind.ToUpperInvariant(), Value = rule.Value,
                Target = rule.Target.ToUpperInvariant(), NoResolve = rule.NoResolve,
                Source = rule.Source.ToString().ToLowerInvariant() }).ToList()
        };
        if(JsonSerializer.SerializeToUtf8Bytes(request,JsonOptions).Length>8*1024*1024-16384)
        { request=null;reason="Маршрут превышает лимит приватного канала ядра (8 МБ).";return false; }
        reason = "";
        return true;
    }
    private static string[] SplitList(string? value) => (value ?? "").Split(',',StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static bool IsKey(string? value) { try { return value is not null && Convert.FromBase64String(value).Length == 32; } catch (FormatException) { return false; } }
    private static bool IsShadowsocks2022Key(string? value,string method) {try{return value is not null && Convert.FromBase64String(value).Length==(method.Contains("aes-128")?16:32);}catch(FormatException){return false;}}

    private static bool IsCidr(string value) { var parts=value.Split('/');return parts.Length==2 && IPAddress.TryParse(parts[0],out var ip) && int.TryParse(parts[1],out var prefix) && prefix>=0 && prefix<=(ip.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork?32:128); }
}

public sealed class CoreStartRequest
{
    public int ApiVersion { get; set; }
    public string RequestId { get; set; } = "";
    public string Operation { get; set; } = "";
    public CoreWireProfile Profile { get; set; } = new();
    public CoreWireDns Dns { get; set; } = new();
    public List<CoreWireRule> Routes { get; set; } = [];
    public CoreWireNetwork? Network { get; set; }
    public CoreWireTun? Tun { get; set; }
    public CoreIngressAuth? IngressAuth { get; set; }
}

public sealed class CoreIngressAuth
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public override string ToString() => "Private ingress authentication";
}

public sealed class CoreWireNetwork
{
    public string PhysicalAddress { get; set; } = "";
    public uint InterfaceIndex { get; set; }
    public string? PhysicalAddress6 { get; set; }
    public uint? InterfaceIndex6 { get; set; }
}
public sealed class CoreWireTun
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string Name { get; set; } = "";
    public int Mtu { get; set; } = 1400;
    public string WintunPath { get; set; } = "";
}

public sealed class CoreWireProfile
{
    public string Protocol { get; set; } = "";
    public string Server { get; set; } = "";
    public int Port { get; set; }
    public string Credential { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string? PublicKey { get; set; }
    public string? ShortId { get; set; }
    public string? Flow { get; set; }
    public string? Fingerprint { get; set; }
    public CoreProtocolOptions Options { get; set; } = new();
    public override string ToString() => $"{Protocol} {Server}:{Port} [credentials redacted]";
}

public sealed class CoreProtocolOptions
{
    public string? Obfs {get;set;}
    public string? ObfsPassword {get;set;}
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AwgEngine { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AwgRoutingMode { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string,string>? Awg { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AwgAdapterPath { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AwgAdapterSha256 { get; set; }
    public string? Method { get; set; }
    public int? AlterId { get; set; }
    public bool? Tls { get; set; }
    public string? Transport { get; set; }
    public string? Host { get; set; }
    public string? Path { get; set; }
    public string? ServiceName { get; set; }
    public string[]? Alpn { get; set; }
    public CoreVlessTransportOptions? VlessTransport { get; set; }
    public string[]? LocalAddresses { get; set; }
    public string[]? AllowedIps { get; set; }
    public string? PeerPublicKey { get; set; }
    public string? PresharedKey { get; set; }
    public int? Keepalive { get; set; }
    public int? Mtu { get; set; }
}

public sealed class CoreWireDns
{
    public string Bootstrap { get; set; } = "";
    public string Upstream { get; set; } = "";
    public bool Ipv4Only { get; set; }
    public List<string> Upstreams { get; set; } = [];
}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed class CoreVlessTransportOptions
{
    public string? Mode { get; set; }
    public bool MultiMode { get; set; }
    public Dictionary<string,string> Headers { get; set; } = [];
    public CoreWireProfile? Download { get; set; }
}

public sealed class CoreWireRule
{
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
    public string Target { get; set; } = "";
    public bool NoResolve { get; set; }
    public string Source { get; set; } = "";
}
