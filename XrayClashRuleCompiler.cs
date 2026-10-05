using System.Net;
using System.Text.Json.Nodes;

namespace HappBox;

/// <summary>Compiles the shared ordered route plan only where Xray has an exact equivalent.</summary>
internal static class XrayClashRuleCompiler
{
    internal static bool TryCompile(HappRoutingProfile profile, out JsonArray rules,
        out bool hasExternalDirect, out string reason)
    {
        rules = new JsonArray();
        hasExternalDirect = false;
        if (!OrderedRoutePlan.TryCreate(profile, out var ordered, out reason)) return false;

        var matchedAll = false;
        foreach (var entry in ordered)
        {
            if (entry.Parameters.Any(parameter => !parameter.Equals("no-resolve", StringComparison.OrdinalIgnoreCase)))
            { reason = $"Неподдерживаемый параметр правила Xray: {entry.Raw}"; return false; }
            if (matchedAll)
            {
                reason = $"Правило после MATCH недостижимо: {entry.Raw}";
                return false;
            }
            var kind = entry.Kind.ToUpperInvariant();
            var target = entry.Target.ToUpperInvariant() switch
            {
                "PROXY" => "proxy", "DIRECT" => "direct",
                "REJECT" or "REJECT-DROP" => "block", _ => null
            };
            if (target is null || profile.ClashProxyGroups.Contains(entry.Target, StringComparer.OrdinalIgnoreCase))
            {
                reason = $"Цель правила пока не имеет подтверждённого Xray-эквивалента: {entry.Raw}";
                return false;
            }
            if (entry.NoResolve)
            {
                reason = $"Параметр no-resolve нельзя сохранить для Xray-правила: {entry.Raw}";
                return false;
            }
            var value = entry.Value.Trim();
            JsonObject rule;
            switch (kind)
            {
                case "DOMAIN" when value.Length > 0:
                    rule = Domain("full:" + value, target); break;
                case "DOMAIN-SUFFIX" when value.Length > 0:
                    rule = Domain("domain:" + value, target); break;
                case "DOMAIN-KEYWORD" when value.Length > 0:
                    rule = Domain("keyword:" + value, target); break;
                case "IP-CIDR" or "IP-CIDR6" when IPNetwork.TryParse(value, out _) || IPAddress.TryParse(value, out _):
                    rule = Ip(value, target); break;
                case "REGEXP" when value.Length > 0:
                    rule = Domain("regexp:" + value, target); break;
                case "GEOIP" when value.Length > 0:
                    if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "core", "xray", "geoip.dat")))
                    { reason = $"Для правила {entry.Raw} отсутствует geoip.dat Xray."; return false; }
                    rule = Ip("geoip:" + value, target); break;
                case "GEOSITE" when value.Length > 0:
                    if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "core", "xray", "geosite.dat")))
                    { reason = $"Для правила {entry.Raw} отсутствует geosite.dat Xray."; return false; }
                    rule = Domain("geosite:" + value, target); break;
                case "MATCH":
                    rule = new JsonObject { ["type"] = "field", ["network"] = "tcp,udp", ["outboundTag"] = target };
                    matchedAll = true;
                    break;
                default:
                    reason = $"Тип правила пока не имеет точного Xray-эквивалента: {entry.Raw}";
                    return false;
            }
            if (target == "direct") hasExternalDirect = true;
            rules.Add(rule);
        }
        if (!matchedAll)
        {
            reason = "В маршруте нет завершающего MATCH: конечный маршрут нельзя вывести без изменения смысла.";
            return false;
        }
        reason = "";
        return true;
    }

    private static JsonObject Domain(string value, string target) =>
        new() { ["type"] = "field", ["domain"] = new JsonArray(value), ["outboundTag"] = target };

    private static JsonObject Ip(string value, string target) =>
        new() { ["type"] = "field", ["ip"] = new JsonArray(value), ["outboundTag"] = target };
}
