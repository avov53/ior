using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace HappBox;

public static class ProfileParser
{
    public static List<ProxyProfile> ParseMany(string input)
    {
        return ParseManyDetailed(input).Profiles;
    }

    public static ProfileParseResult ParseManyDetailed(string input)
    {
        var text = input.Trim();
        var profiles = new List<ProxyProfile>();
        if(ClashSubscriptionParser.LooksLikeClash(text))
        { var clash=ClashSubscriptionParser.Parse(text);return new ProfileParseResult(clash.Profiles,clash.RejectedCount,clash.RemovedProtocolCount); }
        if(text.StartsWith('{') || text.StartsWith('[') && !text.StartsWith("[Interface]",StringComparison.OrdinalIgnoreCase))
            return JsonSubscriptionParser.Parse(text);

        if (text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase))
        {
            var wg = TryParseWireGuardConfig(text, "WireGuard");
            if (wg is not null) profiles.Add(wg);
            return new ProfileParseResult(profiles, wg is null ? 1 : 0, 0);
        }

        var rejected = 0;
        var removed = 0;
        foreach (var line in ExpandSubscription(text))
        {
            if(line.Trim().Split("://",2)[0].ToLowerInvariant() is "vmess" or "ss" or "trojan"){removed++;rejected++;continue;}
            var profile = TryParseLine(line.Trim());
            if (profile is not null)
            {
                if(RemovedProtocolPolicy.IsRemoved(profile)){removed++;rejected++;}else profiles.Add(profile);
            }
            else
            {
                rejected++;
            }
        }

        return new ProfileParseResult(profiles, rejected, removed);
    }

    private static bool HasRemovedConfiguration(string text) => text.Split(['\r', '\n']).Any(line => RemovedProtocolPolicy.IsParameter(line.Split('=', 2)[0].Trim()));
    private static bool IsRemovedLine(string line)
    {
        if (line.StartsWith("awg://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("amneziawg://", StringComparison.OrdinalIgnoreCase)) return true;
        if (!(line.StartsWith("vpn://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("wireguard://", StringComparison.OrdinalIgnoreCase))) return false;
        var decoded = TryBase64Decode(Uri.UnescapeDataString(line[(line.IndexOf("://", StringComparison.Ordinal)+3)..].Split('#',2)[0]));
        return decoded is not null && HasRemovedConfiguration(decoded);
    }

    private static IEnumerable<string> ExpandSubscription(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Any(x => x.Contains("://", StringComparison.Ordinal))) return lines;

        var decoded = TryBase64Decode(text);
        if (decoded is null) return lines;
        return decoded.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static ProxyProfile? TryParseLine(string line)
    {
        try
        {
            if (line.StartsWith("ior-profile://", StringComparison.OrdinalIgnoreCase)) return ProfileShareCodec.Import(line);
            if (line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) return ParseVless(line);
            if (line.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) return ParseVmess(line);
            if (line.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)) return ParseTrojan(line);
            if (line.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)) return ParseShadowsocks(line);
            if (line.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase)) return ParseHysteria2(line);
            if (line.StartsWith("wireguard://", StringComparison.OrdinalIgnoreCase)) return ParseWireGuardLink(line);
            if (line.StartsWith("vpn://", StringComparison.OrdinalIgnoreCase)) return ParseWireGuardLink(line);
            if (line.StartsWith("awg://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("amneziawg://", StringComparison.OrdinalIgnoreCase)) return ParseWireGuardLink(line);
            if (line.StartsWith("socks://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return ParseForwardProxy(line);
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static ProxyProfile ParseForwardProxy(string line)
    {
        var uri = new Uri(line);
        if (string.IsNullOrWhiteSpace(uri.Host) || uri.AbsolutePath is not ("" or "/") || !string.IsNullOrEmpty(uri.Query))
            throw new FormatException("Forward proxy URLs require a host and port, without a path or query.");
        var auth = uri.UserInfo.Split(':', 2);
        var socks = uri.Scheme is "socks" or "socks5";
        var port = uri.Port > 0 ? uri.Port : socks ? 1080 : uri.Scheme == "https" ? 443 : 80;
        return new ProxyProfile
        {
            Name = NameFromUri(uri, uri.Host), Protocol = socks ? ProxyProtocol.Socks : ProxyProtocol.Http,
            Server = uri.IdnHost, Port = port, TlsEnabled = uri.Scheme == "https", ServerName = uri.IdnHost,
            Username = auth[0].Length == 0 ? null : Uri.UnescapeDataString(auth[0]),
            Password = auth.Length > 1 ? Uri.UnescapeDataString(auth[1]) : null
        };
    }

    private static ProxyProfile ParseVless(string line)
    {
        var uri = new Uri(line);
        var q = Query(uri);
        var security = Get(q, "security");

        var profile = new ProxyProfile
        {
            Name = NameFromUri(uri, uri.Host),
            Protocol = ProxyProtocol.Vless,
            Server = uri.Host,
            Port = uri.Port,
            UserId = uri.UserInfo,
            Flow = Get(q, "flow"),
            TlsEnabled = security is "tls" or "reality",
            RealityEnabled = security == "reality",
            AllowInsecure = Get(q, "allowInsecure") == "1" || Get(q, "insecure") == "1",
            ServerName = Get(q, "sni") ?? Get(q, "serverName") ?? uri.Host,
            PublicKey = Get(q, "pbk") ?? Get(q, "publicKey"),
            ShortId = Get(q, "sid") ?? Get(q, "shortId"),
            Fingerprint = Get(q, "fp") ?? "chrome",
            SupportX25519Mlkem768 = Get(q, "support-x25519mlkem768"),
            Network = Get(q, "type") ?? Get(q, "network"),
            Host = Get(q, "host") ?? Get(q,"authority"),
            Path = Get(q, "path"),
            ServiceName = Get(q, "serviceName"),
            Alpn=Get(q,"alpn")?.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries),
            UnsupportedParameters = q.Keys.Where(key=>key is not ("security" or "allowInsecure" or "insecure" or "sni" or "serverName" or "fp" or "type" or "network" or "host" or "path" or "serviceName" or "flow" or "pbk" or "publicKey" or "sid" or "shortId" or "mode" or "multiMode" or "extra" or "encryption" or "packetEncoding" or "spx" or "authority" or "alpn" or "support-x25519mlkem768")).ToList()
        };
        profile.VlessTransport=NativeVlessTransportSettings.Parse(q,profile);
        if(Get(q,"encryption") is {Length:>0} encryption&&encryption!="none")profile.UnsupportedParameters.Add("encryption");
        if(Get(q,"packetEncoding") is {Length:>0} packet&&packet!="xudp")profile.UnsupportedParameters.Add("packetEncoding");
        return profile;
    }

    private static ProxyProfile ParseVmess(string line)
    {
        var body = line["vmess://".Length..].Trim();
        var json = TryBase64Decode(body) ?? throw new FormatException("Invalid vmess payload.");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var host = JsonString(root, "add") ?? JsonString(root, "server") ?? "";
        var portText = JsonString(root, "port") ?? "0";
        var security = JsonString(root, "scy") ?? JsonString(root, "security") ?? "auto";
        var tls = JsonString(root, "tls");
        var sni = JsonString(root, "sni") ?? JsonString(root, "host") ?? host;

        return new ProxyProfile
        {
            Name = JsonString(root, "ps") ?? host,
            Protocol = ProxyProtocol.Vmess,
            Server = host,
            Port = int.TryParse(portText, out var port) ? port : 0,
            UserId = JsonString(root, "id"),
            Security = string.IsNullOrWhiteSpace(security) ? "auto" : security,
            AlterId = int.TryParse(JsonString(root, "aid"), out var aid) ? aid : 0,
            TlsEnabled = tls is "tls" or "reality",
            RealityEnabled = tls == "reality",
            ServerName = sni,
            Network = JsonString(root, "net"),
            Host = JsonString(root, "host"),
            Path = JsonString(root, "path"),
            ServiceName = JsonString(root, "serviceName"),
            UnsupportedParameters = root.EnumerateObject().Where(field=>field.Name is not ("v" or "ps" or "add" or "server" or "port" or "id" or "aid" or "scy" or "security" or "tls" or "sni" or "net" or "host" or "path" or "serviceName" or "type"))
                .Select(field=>field.Name).Concat(JsonString(root,"type") is not (null or "" or "none") ? ["type"] : Array.Empty<string>()).ToList()
        };
    }

    private static ProxyProfile ParseTrojan(string line)
    {
        var uri = new Uri(line);
        var q = Query(uri);
        var security = Get(q, "security");

        return new ProxyProfile
        {
            Name = NameFromUri(uri, uri.Host),
            Protocol = ProxyProtocol.Trojan,
            Server = uri.Host,
            Port = uri.Port,
            Password = WebUtility.UrlDecode(uri.UserInfo),
            TlsEnabled = security != "none",
            AllowInsecure = Get(q, "allowInsecure") == "1" || Get(q, "insecure") == "1",
            ServerName = Get(q, "sni") ?? Get(q, "serverName") ?? uri.Host,
            Fingerprint = Get(q, "fp") ?? "chrome",
            Network = Get(q, "type") ?? Get(q, "network"),
            Host = Get(q, "host"),
            Path = Get(q, "path"),
            ServiceName = Get(q, "serviceName"),
            UnsupportedParameters = q.Keys.Where(key=>key is not ("security" or "allowInsecure" or "insecure" or "sni" or "serverName" or "fp" or "type" or "network" or "host" or "path" or "serviceName")).ToList()
        };
    }

    private static ProxyProfile ParseShadowsocks(string line)
    {
        var withoutScheme = line["ss://".Length..];
        var hashIndex = withoutScheme.IndexOf('#');
        var name = hashIndex >= 0 ? WebUtility.UrlDecode(withoutScheme[(hashIndex + 1)..]) : "Shadowsocks";
        var body = hashIndex >= 0 ? withoutScheme[..hashIndex] : withoutScheme;
        var queryIndex = body.IndexOf('?');
        var query = queryIndex >= 0 ? Query(new Uri("https://localhost/" + body[queryIndex..])) : new Dictionary<string,string>();
        var plugin = Get(query,"plugin");
        if (queryIndex >= 0) body = body[..queryIndex];

        string userInfo;
        string hostPort;
        if (body.Contains('@'))
        {
            var parts = body.Split('@', 2);
            userInfo = DecodeUserInfo(parts[0]);
            hostPort = parts[1];
        }
        else
        {
            var decoded = DecodeUserInfo(body);
            var at = decoded.LastIndexOf('@');
            userInfo = decoded[..at];
            hostPort = decoded[(at + 1)..];
        }

        var credentials = userInfo.Split(':', 2);
        var endpoint = SplitHostPort(hostPort.TrimEnd('/'));
        return new ProxyProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? endpoint.host : name,
            Protocol = ProxyProtocol.Shadowsocks,
            Plugin = plugin,
            UnsupportedParameters = query.Keys.Where(key=>key!="plugin").ToList(),
            Server = endpoint.host,
            Port = endpoint.port,
            Method = credentials[0],
            Password = credentials.Length > 1 ? credentials[1] : ""
        };
    }

    private static ProxyProfile ParseHysteria2(string line)
    {
        var uri = new Uri(line.Replace("hy2://", "hysteria2://", StringComparison.OrdinalIgnoreCase));
        var q = Query(uri);
        return new ProxyProfile
        {
            Name = NameFromUri(uri, uri.Host),
            Protocol = ProxyProtocol.Hysteria2,
            Server = uri.Host,
            Port = uri.Port,
            Password = WebUtility.UrlDecode(uri.UserInfo),
            TlsEnabled = true,
            ServerName = Get(q, "sni") ?? uri.Host,
            AllowInsecure = Get(q, "insecure") == "1" || Get(q, "allowInsecure") == "1",
            ObfsType = Get(q, "obfs"),
            ObfsPassword = Get(q, "obfs-password") ?? Get(q, "obfsPassword")
        };
    }

    private static ProxyProfile? ParseWireGuardLink(string line)
    {
        // Base64 is case-sensitive; Uri.Host normalizes case and corrupts keys.
        var parts=line[(line.IndexOf("://",StringComparison.Ordinal)+3)..].Split('#',2);
        var decoded=TryBase64Decode(Uri.UnescapeDataString(parts[0]));
        var name=parts.Length==2?WebUtility.UrlDecode(parts[1]):"WireGuard";
        var profile = decoded is null ? null : TryParseWireGuardConfig(decoded,string.IsNullOrWhiteSpace(name)?"WireGuard":name);
        if(profile is not null && (line.StartsWith("awg://",StringComparison.OrdinalIgnoreCase) || line.StartsWith("amneziawg://",StringComparison.OrdinalIgnoreCase))) profile.Protocol=ProxyProtocol.AmneziaWG;
        return profile;
    }

    private static ProxyProfile? TryParseWireGuardConfig(string text, string name)
    {
        var map = ParseWireGuardSections(text);
        var awg = map.Where(item => item.Key.StartsWith("Interface.",StringComparison.OrdinalIgnoreCase) && RemovedProtocolPolicy.IsParameter(item.Key[10..])).ToDictionary(item=>AwgParameterNames.First(pair=>pair.Key.Equals(item.Key[10..],StringComparison.OrdinalIgnoreCase)||pair.Value.Equals(item.Key[10..],StringComparison.OrdinalIgnoreCase)).Value,item=>item.Value,StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(["Interface.PrivateKey", "Interface.Address", "Interface.DNS", "Interface.MTU", "Peer.PublicKey", "Peer.PresharedKey", "Peer.Endpoint", "Peer.AllowedIPs", "Peer.PersistentKeepalive"], StringComparer.OrdinalIgnoreCase);
        var unknown = map.Keys.Where(key => !known.Contains(key) && !(key.StartsWith("Interface.",StringComparison.OrdinalIgnoreCase) && RemovedProtocolPolicy.IsParameter(key[10..]))).ToList();
        if (!map.TryGetValue("Interface.PrivateKey", out var privateKey) ||
            !map.TryGetValue("Peer.PublicKey", out var publicKey) ||
            !map.TryGetValue("Peer.Endpoint", out var endpoint))
        {
            return null;
        }

        var hostPort = SplitHostPort(endpoint);
        return new ProxyProfile
        {
            Name = name == "WireGuard" && awg.Count > 0 ? "AmneziaWG" : name,
            Protocol = awg.Count > 0 ? ProxyProtocol.AmneziaWG : ProxyProtocol.WireGuard,
            Awg = awg,
            UnsupportedParameters = unknown,
            TunnelDns = map.GetValueOrDefault("Interface.DNS"),
            ServerName = hostPort.host,
            Server = hostPort.host,
            Port = hostPort.port,
            PrivateKey = privateKey,
            PeerPublicKey = publicKey,
            PreSharedKey = map.GetValueOrDefault("Peer.PresharedKey"),
            LocalAddress = map.GetValueOrDefault("Interface.Address"),
            AllowedIps = map.GetValueOrDefault("Peer.AllowedIPs") ?? "0.0.0.0/0, ::/0",
            PersistentKeepalive = int.TryParse(map.GetValueOrDefault("Peer.PersistentKeepalive"), out var keepalive) ? keepalive : null,
            Mtu = int.TryParse(map.GetValueOrDefault("Interface.MTU"), out var mtu) ? mtu : null
        };
    }

    private static Dictionary<string, string> ParseWireGuardSections(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        var peerSeen = false;
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith('#') || raw.StartsWith(';')) continue;
            var header = Regex.Match(raw, @"^\[(.+)\]$");
            if (header.Success)
            {
                section = header.Groups[1].Value.Trim();
                if (section.Equals("Peer", StringComparison.OrdinalIgnoreCase)) { if (peerSeen) throw new FormatException("Несколько WireGuard пиров в одном профиле пока не поддерживаются."); peerSeen = true; }
                continue;
            }

            var parts = raw.Split('=', 2);
            if (parts.Length == 2 && !result.TryAdd($"{section}.{parts[0].Trim()}", parts[1].Trim())) throw new FormatException("Повторный параметр WireGuard: " + parts[0].Trim());
        }

        return result;
    }

    public static readonly IReadOnlyDictionary<string,string> AwgParameterNames = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Jc"]="jc",["Jmin"]="jmin",["Jmax"]="jmax",["S1"]="s1",["S2"]="s2",["S3"]="s3",["S4"]="s4",
        ["H1"]="h1",["H2"]="h2",["H3"]="h3",["H4"]="h4",["I1"]="i1",["I2"]="i2",["I3"]="i3",["I4"]="i4",["I5"]="i5",
        ["HeaderProtectionKey"]="header_protection_key",["ContentPaddingAddition"]="content_padding_addition",
        ["RekeyAfterTime"]="rekey_after_time",["RekeyTimeout"]="rekey_timeout",["RejectAfterTime"]="reject_after_time",
        ["KeepaliveTimeout"]="keepalive_timeout",["MaxHandshakeAttempts"]="max_handshake_attempts",["RandomTrailers"]="random_trailers",["DisableCookies"]="disable_cookies"
    };

    private static Dictionary<string, string> Query(Uri uri)
    {
        return uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2))
            .Where(x => x.Length == 2)
            .ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x[1]), StringComparer.OrdinalIgnoreCase);
    }

    private static string? Get(Dictionary<string, string> q, string key) => q.TryGetValue(key, out var value) ? value : null;

    private static string? JsonString(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var value) ? value.ToString() : null;
    }

    private static string NameFromUri(Uri uri, string fallback)
    {
        return string.IsNullOrWhiteSpace(uri.Fragment) ? fallback : WebUtility.UrlDecode(uri.Fragment.TrimStart('#'));
    }

    private static string DecodeUserInfo(string value)
    {
        var decoded = TryBase64Decode(value.Replace('-', '+').Replace('_', '/'));
        return WebUtility.UrlDecode(decoded ?? value);
    }

    private static string? TryBase64Decode(string value)
    {
        try
        {
            var normalized = value.Trim().Replace("-", "+").Replace("_", "/");
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
        }
        catch
        {
            return null;
        }
    }

    private static (string host, int port) SplitHostPort(string value)
    {
        var clean = value.Trim();
        var index = clean.LastIndexOf(':');
        if (index < 0) return (clean, 0);
        return (clean[..index].Trim('[', ']'), int.Parse(clean[(index + 1)..]));
    }
}

public sealed record ProfileParseResult(List<ProxyProfile> Profiles, int RejectedCount, int RemovedProtocolCount = 0);
