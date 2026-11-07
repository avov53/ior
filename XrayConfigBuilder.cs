using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace HappBox;

/// <summary>Xray configuration adapter. Unsupported combinations fail before launch.</summary>
public static class XrayConfigBuilder
{
    public static bool CanBuild(AppState state, ProxyProfile? profile, out string reason)
        => CanBuild(state, profile, allowTunCandidate: false, out reason);

    public static bool CanBuildTun(AppState state, ProxyProfile? profile, out string reason)
    {
        if (!state.TunEnabled) { reason = "Сначала выберите режим TUN."; return false; }
        if (!CanBuild(state, profile, allowTunCandidate: true, out reason)) return false;
        if (profile!.Protocol == ProxyProtocol.Http)
        { reason = "HTTP-прокси не передаёт UDP. Xray TUN для него закрыт до отдельной интеграции DNS; используйте Proxy."; return false; }
        var route = RoutingSelectionPolicy.Effective(state);
        if (state.RoutingEnabled && !string.IsNullOrWhiteSpace(state.SelectedHappRoutingProfileId) && route is null)
        { reason = "Выбранный маршрут не найден. Xray TUN не может запускаться с другим маршрутом без подтверждения."; return false; }
        if (route is not null)
        {
            if (!XrayClashRuleCompiler.TryCompile(route, out _, out var hasDirect, out reason)) return false;
        }
        reason = "";
        return true;
    }

    private static bool CanBuild(AppState state, ProxyProfile? profile, bool allowTunCandidate, out string reason)
    {
        if (profile is null) { reason = "Сначала выберите сервер."; return false; }
        if(RemovedProtocolPolicy.IsRemoved(profile)){reason=RemovedProtocolPolicy.Message;return false;}
        if (state.TunEnabled && !allowTunCandidate) { reason = "Xray TUN пока не прошёл проверку восстановления маршрутов Windows 10/11. Используйте Proxy."; return false; }
        if (!state.MixedProxyEnabled) { reason = "Для Xray нужен включённый локальный прокси."; return false; }
        if (state.MixedProxyPort >= 65535) { reason = "Для Xray нужны два соседних порта HTTP и SOCKS; выберите порт до 65535."; return false; }
        if (string.IsNullOrWhiteSpace(profile.Server) || profile.Port is < 1 or > 65535) { reason = "У сервера неверный адрес или порт."; return false; }
        if (profile.Protocol is not (ProxyProtocol.Vless or ProxyProtocol.Vmess or ProxyProtocol.Trojan or ProxyProtocol.Shadowsocks or ProxyProtocol.Hysteria2 or ProxyProtocol.WireGuard or ProxyProtocol.Socks or ProxyProtocol.Http))
        { reason = $"Xray Proxy пока не проверен для {profile.Protocol}."; return false; }
        if (profile.Protocol is ProxyProtocol.Http or ProxyProtocol.Socks &&
            ((!string.IsNullOrEmpty(profile.Username) && profile.Password is null) ||
             (string.IsNullOrEmpty(profile.Username) && !string.IsNullOrEmpty(profile.Password))))
        { reason = "Для авторизации HTTP/SOCKS укажите имя пользователя и пароль вместе."; return false; }
        if (profile.RealityEnabled && profile.Protocol != ProxyProtocol.Vless)
        { reason = "Reality в этой модели профилей поддерживается только для VLESS."; return false; }
        if (profile.Protocol == ProxyProtocol.WireGuard)
        {
            if (!ValidWireGuardKey(profile.PrivateKey) || !ValidWireGuardKey(profile.PeerPublicKey) ||
                (!string.IsNullOrWhiteSpace(profile.PreSharedKey) && !ValidWireGuardKey(profile.PreSharedKey)))
            { reason = "WireGuard: ключи должны быть Base64 длиной 32 байта."; return false; }
            var addresses = SplitWireGuardList(profile.LocalAddress);
            var networks = SplitWireGuardList(profile.AllowedIps);
            if (addresses.Length == 0 || addresses.Any(value => !IPAddress.TryParse(value.Split('/')[0], out _) ||
                (value.Contains('/') && !IPNetwork.TryParse(value, out _))) ||
                networks.Any(value => !IPNetwork.TryParse(value, out _)))
            { reason = "WireGuard: проверьте локальные адреса и AllowedIPs."; return false; }
            if (profile.PersistentKeepalive is < 0 or > 65535 || profile.Mtu is < 576 or > 9000)
            { reason = "WireGuard: некорректные MTU или keepalive."; return false; }
        }
        if (profile.Protocol is ProxyProtocol.Vless or ProxyProtocol.Vmess && !Guid.TryParse(profile.UserId, out _))
        { reason = "Для Xray нужен корректный UUID сервера."; return false; }
        if (profile.Protocol is ProxyProtocol.Trojan or ProxyProtocol.Shadowsocks && string.IsNullOrWhiteSpace(profile.Password))
        { reason = "В профиле отсутствует пароль."; return false; }
        if (profile.Protocol == ProxyProtocol.Shadowsocks && string.IsNullOrWhiteSpace(profile.Method))
        { reason = "В профиле Shadowsocks не указан метод шифрования."; return false; }
        if (profile.Protocol == ProxyProtocol.Hysteria2 && string.IsNullOrWhiteSpace(profile.Password))
        { reason = "В профиле Hysteria2 отсутствует пароль."; return false; }
        if (profile.Protocol == ProxyProtocol.Hysteria2 && !string.IsNullOrWhiteSpace(profile.ObfsType))
        { reason = "Xray не поддерживает obfs этого Hysteria2-профиля."; return false; }
        if (profile.AllowInsecure)
        { reason = "Xray 26.9.9 не поддерживает allowInsecure. Для такого сервера требуется закрепить SHA-256 сертификата, чего профиль пока не хранит."; return false; }
        var network = (profile.Network ?? "tcp").ToLowerInvariant();
        if (profile.Protocol != ProxyProtocol.Hysteria2 && network is not ("tcp" or "ws" or "websocket" or "grpc"))
        { reason = $"Транспорт {network} пока не преобразуется для Xray."; return false; }
        if (profile.RealityEnabled && (string.IsNullOrWhiteSpace(profile.PublicKey) || string.IsNullOrWhiteSpace(profile.ServerName)))
        { reason = "Для Xray Reality нужны public key и SNI."; return false; }
        if (profile.Protocol is ProxyProtocol.Vless or ProxyProtocol.Trojan && !profile.TlsEnabled && !profile.RealityEnabled && !IPAddress.TryParse(profile.Server, out _))
        { reason = "Незащищённый VLESS/Trojan для публичного домена отключён."; return false; }
        var route = RoutingSelectionPolicy.Effective(state);
        if (route is { Source: RoutingProfileSource.Happ } && route.Links.Count > 0)
        {
            foreach (var link in route.Links)
            {
                var config = SingBoxConfigBuilder.TryParseHappRoutingConfig(link);
                if (config is null) { reason = "Этот Happ-маршрут содержит ссылку или JSON, который Xray пока не может применить."; return false; }
                if (!CanBuildHappRules(config.Value, out reason)) return false;
            }
        }
        if (route is not null && !XrayClashRuleCompiler.TryCompile(route, out _, out _, out reason)) return false;
        reason = "";
        return true;
    }

    public static string Build(AppState state, ProxyProfile profile)
        => Build(state, profile, allowTunCandidate: false);

    /// <summary>Creates an offline-test-only TUN configuration; the application does not start it until recovery tests pass.</summary>
    public static string BuildTunCandidateForValidation(AppState state, ProxyProfile profile,
        string? resolvedServer = null, string? outboundInterface = null, DirectEgressHelper? directHelper = null)
    {
        if (!state.TunEnabled) throw new ArgumentException("TUN must be selected for the candidate.", nameof(state));
        if (!IPAddress.TryParse(profile.Server, out _))
        {
            if (!IPAddress.TryParse(resolvedServer, out var endpoint))
                throw new ArgumentException("Xray TUN требует предварительно разрешённый IP сервера.", nameof(resolvedServer));
            var originalHost = profile.Server;
            profile = JsonSerializer.Deserialize<ProxyProfile>(JsonSerializer.Serialize(profile))!;
            profile.Server = endpoint.ToString();
            if (string.IsNullOrWhiteSpace(profile.ServerName)) profile.ServerName = originalHost;
        }
        var json = Build(state, profile, allowTunCandidate: true);
        if (string.IsNullOrWhiteSpace(outboundInterface)) return json;
        var root = JsonNode.Parse(json)!;
        if (directHelper is not null)
        {
            root["outbounds"]![1] = new JsonObject
            {
                ["tag"] = "direct", ["protocol"] = "socks", ["sendThrough"] = "127.0.0.1",
                ["settings"] = new JsonObject { ["servers"] = new JsonArray { new JsonObject
                {
                    ["address"] = "127.0.0.1", ["port"] = directHelper.Port,
                    ["users"] = new JsonArray { new JsonObject { ["user"] = directHelper.User, ["pass"] = directHelper.Password } }
                } } }
            };
        }
        root["inbounds"]![2]!["settings"]!["autoOutboundsInterface"] = outboundInterface;
        // Explicit socket binding makes the candidate's physical egress auditable.
        // autoOutboundsInterface remains set for Xray's internal DNS and other sockets.
        foreach (var outbound in root["outbounds"]!.AsArray().Take(2))
        {
            if (directHelper is not null && outbound!["tag"]!.GetValue<string>() == "direct") continue;
            var settings = outbound!["streamSettings"] as JsonObject;
            if (settings is null)
            {
                settings = new JsonObject();
                outbound["streamSettings"] = settings;
            }
            var sockopt = settings["sockopt"] as JsonObject;
            if (sockopt is null)
            {
                sockopt = new JsonObject();
                settings["sockopt"] = sockopt;
            }
            sockopt["interface"] = outboundInterface;
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() });
    }

    private static string Build(AppState state, ProxyProfile profile, bool allowTunCandidate)
    {
        if (!CanBuild(state, profile, allowTunCandidate, out var reason)) throw new InvalidOperationException(reason);
        var outbounds = new JsonArray
        {
            BuildProxy(profile),
            new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" },
            new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" }
        };
        var rules = new JsonArray();
        if (IPAddress.TryParse(profile.Server, out var endpoint))
            AddIpRule(rules, [endpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"{endpoint}/128" : $"{endpoint}/32"], "direct");
        else AddDomainRule(rules, [$"full:{profile.Server}"], "direct");
        AddIpRule(rules, ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "169.254.0.0/16", "::1/128", "fc00::/7", "fe80::/10"], "direct");
        var route = RoutingSelectionPolicy.Effective(state);
        if (route is not null)
        {
            if (!XrayClashRuleCompiler.TryCompile(route, out var orderedRules, out _, out var clashReason))
                throw new InvalidOperationException(clashReason);
            foreach (var rule in orderedRules) rules.Add(rule?.DeepClone());
        }
        var inbounds = new JsonArray
        {
            new JsonObject { ["tag"] = "http-in", ["listen"] = "127.0.0.1", ["port"] = state.MixedProxyPort, ["protocol"] = "http", ["settings"] = new JsonObject() },
            new JsonObject { ["tag"] = "socks-in", ["listen"] = "127.0.0.1", ["port"] = state.MixedProxyPort + 1, ["protocol"] = "socks", ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true } }
        };
        if (state.TunEnabled)
        {
            var bootstrap = BootstrapDnsResolver.ServerAddress(state.BootstrapDnsServer);
            var dnsAddress = IPAddress.TryParse(state.DnsServer, out var configuredDns) &&
                configuredDns.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !configuredDns.Equals(bootstrap)
                ? configuredDns.ToString()
                : bootstrap.Equals(IPAddress.Parse("1.1.1.1")) ? "8.8.8.8" : "1.1.1.1";
            inbounds.Add(new JsonObject
            {
                ["tag"] = "tun-in", ["protocol"] = "tun",
                ["settings"] = new JsonObject
                {
                    ["name"] = "ior-xray", ["desc"] = "ior Xray", ["mtu"] = Math.Clamp(state.TunMtu, 1280, 1500),
                    ["gateway"] = new JsonArray(state.TunAddress), ["dns"] = new JsonArray(dnsAddress),
                    ["autoSystemRoutingTable"] = new JsonArray("0.0.0.0/0"), ["autoOutboundsInterface"] = "auto"
                }
            });
        }
        if (state.SniffEnabled)
        {
            // TUN and SOCKS CONNECT commonly carry only an IP. Match domain rules against
            // HTTP Host/TLS SNI/QUIC SNI without re-resolving or replacing that destination.
            // routeOnly also preserves the original IP for subsequent GEOIP/CIDR rules.
            foreach (var inbound in inbounds)
                inbound!["sniffing"] = new JsonObject
                {
                    ["enabled"] = true, ["destOverride"] = new JsonArray("http", "tls", "quic"),
                    ["metadataOnly"] = false, ["routeOnly"] = true
                };
        }
        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = state.LogLevel == "debug" ? "debug" : state.LogLevel == "info" ? "info" : state.LogLevel == "error" ? "error" : "warning" },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["routing"] = new JsonObject { ["domainStrategy"] = HappDomainStrategy(route), ["rules"] = rules }
        };
        if (state.TunEnabled) root["dns"] = new JsonObject { ["servers"] = new JsonArray(state.BootstrapDnsServer), ["queryStrategy"] = "UseIPv4" };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() });
    }

    private static JsonObject BuildProxy(ProxyProfile profile)
    {
        var proxy = new JsonObject { ["tag"] = "proxy" };
        switch (profile.Protocol)
        {
            case ProxyProtocol.Vless:
                proxy["protocol"] = "vless";
                proxy["settings"] = new JsonObject { ["vnext"] = new JsonArray { new JsonObject
                {
                    ["address"] = profile.Server, ["port"] = profile.Port,
                    ["users"] = new JsonArray { new JsonObject { ["id"] = profile.UserId, ["encryption"] = "none", ["flow"] = profile.Flow ?? "" } }
                } } };
                break;
            case ProxyProtocol.Vmess:
                proxy["protocol"] = "vmess";
                proxy["settings"] = new JsonObject { ["vnext"] = new JsonArray { new JsonObject
                {
                    ["address"] = profile.Server, ["port"] = profile.Port,
                    ["users"] = new JsonArray { new JsonObject { ["id"] = profile.UserId, ["alterId"] = profile.AlterId ?? 0, ["security"] = profile.Security ?? "auto" } }
                } } };
                break;
            case ProxyProtocol.Trojan:
                proxy["protocol"] = "trojan";
                proxy["settings"] = new JsonObject { ["servers"] = new JsonArray { new JsonObject { ["address"] = profile.Server, ["port"] = profile.Port, ["password"] = profile.Password } } };
                break;
            case ProxyProtocol.Shadowsocks:
                proxy["protocol"] = "shadowsocks";
                proxy["settings"] = new JsonObject { ["servers"] = new JsonArray { new JsonObject { ["address"] = profile.Server, ["port"] = profile.Port, ["method"] = profile.Method, ["password"] = profile.Password } } };
                break;
            case ProxyProtocol.Hysteria2:
                proxy["protocol"] = "hysteria";
                proxy["settings"] = new JsonObject { ["version"] = 2, ["address"] = profile.Server, ["port"] = profile.Port };
                proxy["streamSettings"] = new JsonObject
                {
                    ["method"] = "hysteria",
                    ["security"] = "tls",
                    ["tlsSettings"] = new JsonObject { ["serverName"] = profile.ServerName ?? profile.Server, ["alpn"] = new JsonArray("h3") },
                    ["hysteriaSettings"] = new JsonObject { ["version"] = 2, ["auth"] = profile.Password }
                };
                return proxy;
            case ProxyProtocol.WireGuard:
                proxy["protocol"] = "wireguard";
                var peer = new JsonObject
                {
                    ["endpoint"] = profile.Server.Contains(':') ? $"[{profile.Server}]:{profile.Port}" : $"{profile.Server}:{profile.Port}",
                    ["publicKey"] = profile.PeerPublicKey,
                    ["allowedIPs"] = new JsonArray((string.IsNullOrWhiteSpace(profile.AllowedIps) ? new[] { "0.0.0.0/0", "::/0" } : SplitWireGuardList(profile.AllowedIps)).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                    ["keepAlive"] = profile.PersistentKeepalive ?? 0
                };
                if (!string.IsNullOrWhiteSpace(profile.PreSharedKey)) peer["preSharedKey"] = profile.PreSharedKey;
                proxy["settings"] = new JsonObject
                {
                    ["secretKey"] = profile.PrivateKey,
                    ["address"] = new JsonArray(SplitWireGuardList(profile.LocalAddress).Select(value => (JsonNode?)JsonValue.Create(value.Split('/')[0])).ToArray()),
                    ["peers"] = new JsonArray(peer),
                    ["noKernelTun"] = true,
                    ["mtu"] = profile.Mtu ?? 1420
                };
                return proxy;
            case ProxyProtocol.Http:
            case ProxyProtocol.Socks:
                proxy["protocol"] = profile.Protocol == ProxyProtocol.Http ? "http" : "socks";
                var forwardServer = new JsonObject { ["address"] = profile.Server, ["port"] = profile.Port };
                if (!string.IsNullOrEmpty(profile.Username))
                    forwardServer["users"] = new JsonArray(new JsonObject { ["user"] = profile.Username, ["pass"] = profile.Password });
                proxy["settings"] = new JsonObject { ["servers"] = new JsonArray(forwardServer) };
                break;
        }
        var network = (profile.Network ?? "tcp").ToLowerInvariant() switch { "websocket" => "ws", var value => value };
        var stream = new JsonObject { ["network"] = network, ["security"] = profile.RealityEnabled ? "reality" : profile.TlsEnabled ? "tls" : "none" };
        if (profile.RealityEnabled)
            stream["realitySettings"] = new JsonObject { ["serverName"] = profile.ServerName, ["publicKey"] = profile.PublicKey, ["shortId"] = profile.ShortId ?? "", ["fingerprint"] = profile.Fingerprint ?? "chrome" };
        else if (profile.TlsEnabled)
            stream["tlsSettings"] = new JsonObject { ["serverName"] = profile.ServerName ?? profile.Server };
        if (network == "ws") stream["wsSettings"] = new JsonObject { ["path"] = profile.Path ?? "/", ["headers"] = new JsonObject { ["Host"] = profile.Host ?? profile.ServerName ?? profile.Server } };
        if (network == "grpc") stream["grpcSettings"] = new JsonObject { ["serviceName"] = profile.ServiceName ?? "" };
        proxy["streamSettings"] = stream;
        return proxy;
    }

    private static string[] SplitWireGuardList(string? value) => (value ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool ValidWireGuardKey(string? key)
    {
        try { return !string.IsNullOrWhiteSpace(key) && Convert.FromBase64String(key).Length == 32; }
        catch (FormatException) { return false; }
    }

    private static void AddDomainRule(JsonArray rules, IEnumerable<string> domains, string outbound)
    {
        var items = domains.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (items.Length == 0) return;
        var values = new JsonArray();
        foreach (var item in items) values.Add(item);
        rules.Add(new JsonObject { ["type"] = "field", ["domain"] = values, ["outboundTag"] = outbound });
    }

    private static void AddIpRule(JsonArray rules, IEnumerable<string> addresses, string outbound)
    {
        var items = addresses.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (items.Length == 0) return;
        var values = new JsonArray();
        foreach (var item in items) values.Add(item);
        rules.Add(new JsonObject { ["type"] = "field", ["ip"] = values, ["outboundTag"] = outbound });
    }

    private static bool CanBuildHappRules(JsonElement config, out string reason)
    {
        foreach (var key in new[] { "Geoipurl", "Geositeurl" })
        {
            if (!string.IsNullOrWhiteSpace(SingBoxConfigBuilder.TryGetString(config, key)))
            {
                reason = $"Happ-маршрут задаёт собственную базу {key}. Xray пока не скачивает её, поэтому правила нельзя применять без изменения смысла.";
                return false;
            }
        }
        var order = SingBoxConfigBuilder.TryGetString(config, "RouteOrder");
        if (!string.IsNullOrWhiteSpace(order) && order.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => part.ToLowerInvariant() is not ("block" or "proxy" or "direct")))
        { reason = "Неизвестный порядок правил Happ для Xray."; return false; }
        var strategy = SingBoxConfigBuilder.TryGetString(config, "DomainStrategy");
        if (!string.IsNullOrWhiteSpace(strategy) && strategy is not ("AsIs" or "IPIfNonMatch" or "IPOnDemand"))
        { reason = "Xray не поддерживает указанную DNS-стратегию Happ."; return false; }
        foreach (var action in new[] { "Block", "Proxy", "Direct" })
        {
            foreach (var value in SingBoxConfigBuilder.GetStringArray(config, action + "Sites"))
            {
                if (value.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase) &&
                    !File.Exists(Path.Combine(AppContext.BaseDirectory, "core", "xray", "geosite.dat")))
                { reason = "Для правил Geosite нужен файл core/xray/geosite.dat."; return false; }
                if (string.IsNullOrWhiteSpace(NormalizeHappDomain(value)))
                { reason = $"Некорректное доменное правило Happ: {value}"; return false; }
            }
            foreach (var value in SingBoxConfigBuilder.GetStringArray(config, action + "Ip"))
            {
                if (value.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "core", "xray", "geoip.dat")))
                    { reason = "Для правил GeoIP нужен файл core/xray/geoip.dat."; return false; }
                }
                else if (!IPAddress.TryParse(value, out _) && !IPNetwork.TryParse(value, out _))
                { reason = $"Некорректное IP/CIDR-правило Happ: {value}"; return false; }
            }
        }
        reason = "";
        return true;
    }

    private static string NormalizeHappDomain(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("domain:", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("full:", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("keyword:", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase)) return normalized;
        return "domain:" + normalized;
    }

    private static string HappDomainStrategy(HappRoutingProfile? route)
    {
        if (route?.Source != RoutingProfileSource.Happ) return "AsIs";
        foreach (var link in route.Links)
        {
            var config = SingBoxConfigBuilder.TryParseHappRoutingConfig(link);
            if (config is null) continue;
            var strategy = SingBoxConfigBuilder.TryGetString(config.Value, "DomainStrategy");
            if (!string.IsNullOrWhiteSpace(strategy)) return strategy;
        }
        return "AsIs";
    }
}
