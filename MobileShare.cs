using System.Text;
using System.Text.Json;

namespace HappBox;

public sealed record ShareQr(string Text, string Format, string Description);

public static class MobileShare
{
    public static ShareQr Subscription(ProxySubscription subscription, AppState state)
    {
        var clash = subscription.IsClashYaml ?? state.HappRoutingProfiles.Any(route =>
            route.ManagedBySubscriptionId == subscription.Id && route.Source == RoutingProfileSource.Clash);
        if (string.IsNullOrWhiteSpace(subscription.Url)) throw new FormatException("У подписки отсутствует ссылка.");
        return clash
            ? new("clash://install-config?url=" + Uri.EscapeDataString(subscription.Url), "clash", "Для Clash Verge и совместимых Clash-клиентов. Этот QR-код не предназначен для Happ.")
            : subscription.IsClashYaml == false
                ? new(subscription.Url, "happ", "URL подписки для сканера Happ. Совместимость зависит от содержимого подписки на сервере.")
                : new(subscription.Url, "url", "Формат старой подписки ещё не определён. Обновите подписку для выбора QR Happ или Clash.");
    }

    public static ShareQr Configuration(ProxyProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Server) || profile.Port is < 1 or > 65535)
            throw new FormatException("У конфигурации отсутствует корректный адрес или порт.");
        if (profile.Protocol is ProxyProtocol.Vless or ProxyProtocol.Vmess && !Guid.TryParse(profile.UserId, out _))
            throw new FormatException("У конфигурации отсутствует корректный UUID.");
        if (profile.Protocol is ProxyProtocol.Trojan or ProxyProtocol.Hysteria2 && string.IsNullOrEmpty(profile.Password))
            throw new FormatException("У конфигурации отсутствует пароль.");
        if (profile.Protocol is ProxyProtocol.Socks or ProxyProtocol.Shadowsocks && profile.TlsEnabled)
            throw new NotSupportedException("QR Happ для SOCKS/SS с отдельным TLS не подтверждён. Экспорт без TLS отключён; используйте ссылку ior.");
        if (profile.Protocol is not (ProxyProtocol.Vless or ProxyProtocol.Vmess or ProxyProtocol.Trojan or ProxyProtocol.Shadowsocks or ProxyProtocol.Socks or ProxyProtocol.Hysteria2))
            return new(ProfileShareCodec.Export(profile), "ior", "QR-код для ior. Совместимость этого протокола со сканером Happ не подтверждена.");
        if (profile.UnsupportedParameters.Count > 0)
            throw new NotSupportedException("Экспорт для Happ невозможен без потери неизвестных параметров. Используйте ссылку ior.");
        var host = profile.Server.Contains(':') ? "[" + profile.Server.Trim('[', ']') + "]" : profile.Server;
        var query = new Dictionary<string, string>();
        void Add(string key, string? value) { if (!string.IsNullOrEmpty(value)) query[key] = value; }
        Add("security", profile.RealityEnabled ? "reality" : profile.TlsEnabled ? "tls" : "none");
        Add("type", profile.Network); Add("sni", profile.ServerName); Add("fp", profile.Fingerprint);
        Add("host", profile.Host); Add("path", profile.Path); Add("serviceName", profile.ServiceName);
        Add("flow", profile.Flow); Add("pbk", profile.PublicKey); Add("sid", profile.ShortId);
        if (profile.AllowInsecure) Add("allowInsecure", "1");
        var suffix = "#" + Uri.EscapeDataString(profile.Name);
        string UriLink(string scheme, string auth) => scheme + "://" + (string.IsNullOrEmpty(auth) ? "" : auth + "@") + host + ":" + profile.Port +
            (query.Count == 0 ? "" : "?" + string.Join("&", query.Select(item => Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value)))) + suffix;
        string link;
        switch (profile.Protocol)
        {
            case ProxyProtocol.Vless: link = UriLink("vless", Uri.EscapeDataString(profile.UserId ?? "")); break;
            case ProxyProtocol.Trojan: link = UriLink("trojan", Uri.EscapeDataString(profile.Password ?? "")); break;
            case ProxyProtocol.Hysteria2:
                query.Clear(); Add("sni", profile.ServerName); Add("obfs", profile.ObfsType); Add("obfs-password", profile.ObfsPassword);
                if (profile.AllowInsecure) Add("insecure", "1");
                link = UriLink("hysteria2", Uri.EscapeDataString(profile.Password ?? "")); break;
            case ProxyProtocol.Shadowsocks:
                query.Clear(); Add("plugin", profile.Plugin);
                link = UriLink("ss", Convert.ToBase64String(Encoding.UTF8.GetBytes(profile.Method + ":" + profile.Password)).TrimEnd('=').Replace('+', '-').Replace('/', '_')); break;
            case ProxyProtocol.Socks:
                query.Clear(); link = UriLink("socks", string.IsNullOrEmpty(profile.Username) && string.IsNullOrEmpty(profile.Password) ? "" : Uri.EscapeDataString(profile.Username ?? "") + ":" + Uri.EscapeDataString(profile.Password ?? "")); break;
            default:
                if (profile.RealityEnabled) throw new NotSupportedException("VMess Reality не экспортируется в QR Happ без подтверждённого формата.");
                var json = JsonSerializer.Serialize(new { v = "2", ps = profile.Name, add = profile.Server, port = profile.Port.ToString(),
                    id = profile.UserId, aid = (profile.AlterId ?? 0).ToString(), scy = profile.Security ?? "auto", net = profile.Network ?? "tcp",
                    host = profile.Host, path = profile.Path, serviceName = profile.ServiceName, tls = profile.TlsEnabled ? "tls" : "", sni = profile.ServerName, fp = profile.Fingerprint },
                    new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                link = "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)); break;
        }
        return new(link, "happ", "Стандартная ссылка конфигурации для сканирования в Happ.");
    }
}
