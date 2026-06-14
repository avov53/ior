using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace HappBox;

public static class ProfileParser
{
    public static List<ProxyProfile> ParseMany(string input)
    {
        var text = input.Trim();
        var profiles = new List<ProxyProfile>();

        if (text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase))
        {
            var wg = TryParseWireGuardConfig(text, "WireGuard");
            if (wg is not null) profiles.Add(wg);
            return profiles;
        }

        foreach (var line in ExpandSubscription(text))
        {
            var profile = TryParseLine(line.Trim());
            if (profile is not null) profiles.Add(profile);
        }

        return profiles;
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
            if (line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) return ParseVless(line);
            if (line.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) return ParseVmess(line);
            if (line.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)) return ParseTrojan(line);
            if (line.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)) return ParseShadowsocks(line);
            if (line.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase)) return ParseHysteria2(line);
            if (line.StartsWith("wireguard://", StringComparison.OrdinalIgnoreCase)) return ParseWireGuardLink(line);
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static ProxyProfile ParseVless(string line)
    {
        var uri = new Uri(line);
        var q = Query(uri);
        var security = Get(q, "security");

        return new ProxyProfile
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
            Network = Get(q, "type") ?? Get(q, "network"),
            Host = Get(q, "host"),
            Path = Get(q, "path"),
            ServiceName = Get(q, "serviceName")
        };
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
            ServiceName = JsonString(root, "serviceName")
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
            ServiceName = Get(q, "serviceName")
        };
    }

    private static ProxyProfile ParseShadowsocks(string line)
    {
        var withoutScheme = line["ss://".Length..];
        var hashIndex = withoutScheme.IndexOf('#');
        var name = hashIndex >= 0 ? WebUtility.UrlDecode(withoutScheme[(hashIndex + 1)..]) : "Shadowsocks";
        var body = hashIndex >= 0 ? withoutScheme[..hashIndex] : withoutScheme;
        var queryIndex = body.IndexOf('?');
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
        var endpoint = SplitHostPort(hostPort);
        return new ProxyProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? endpoint.host : name,
            Protocol = ProxyProtocol.Shadowsocks,
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
        var uri = new Uri(line);
        var decoded = TryBase64Decode(uri.Host + uri.AbsolutePath.TrimStart('/'));
        return decoded is null ? null : TryParseWireGuardConfig(decoded, NameFromUri(uri, "WireGuard"));
    }

    private static ProxyProfile? TryParseWireGuardConfig(string text, string name)
    {
        var map = ParseWireGuardSections(text);
        if (!map.TryGetValue("Interface.PrivateKey", out var privateKey) ||
            !map.TryGetValue("Peer.PublicKey", out var publicKey) ||
            !map.TryGetValue("Peer.Endpoint", out var endpoint))
        {
            return null;
        }

        var hostPort = SplitHostPort(endpoint);
        return new ProxyProfile
        {
            Name = name,
            Protocol = ProxyProtocol.WireGuard,
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
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith('#') || raw.StartsWith(';')) continue;
            var header = Regex.Match(raw, @"^\[(.+)\]$");
            if (header.Success)
            {
                section = header.Groups[1].Value.Trim();
                continue;
            }

            var parts = raw.Split('=', 2);
            if (parts.Length == 2) result[$"{section}.{parts[0].Trim()}"] = parts[1].Trim();
        }

        return result;
    }

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
