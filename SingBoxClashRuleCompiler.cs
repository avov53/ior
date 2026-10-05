using System.Net;
using System.Text.Json.Nodes;

namespace HappBox;

/// <summary>Only emits Clash rules whose first-match semantics can be preserved exactly.</summary>
internal static class SingBoxClashRuleCompiler
{
    internal static bool TryCompile(HappRoutingProfile profile, out JsonArray rules, out string reason)
    {
        rules = new JsonArray();
        var unknownParameter = profile.RoutePlan?.Rules.FirstOrDefault(rule => rule.Parameters.Any(parameter =>
            !parameter.Equals("no-resolve", StringComparison.OrdinalIgnoreCase)));
        if (unknownParameter is not null)
        { reason = $"Неподдерживаемый параметр Clash-правила: {unknownParameter.Raw}"; return false; }
        if (profile.ClashOrderedRules.Count == 0)
        { reason = "В Clash-профиле нет упорядоченных правил."; return false; }
        foreach (var raw in profile.ClashUnmappedRules)
            if (profile.ClashOrderedRules.All(rule => !string.Equals(rule.Raw, raw, StringComparison.OrdinalIgnoreCase)))
            { reason = $"Не удалось разобрать Clash-правило: {raw}"; return false; }

        var matchSeen = false;
        foreach (var entry in profile.ClashOrderedRules)
        {
            if (matchSeen) { reason = $"Правило после MATCH недостижимо: {entry.Raw}"; return false; }
            if (entry.NoResolve) { reason = $"Параметр no-resolve ещё не преобразуется без изменения смысла: {entry.Raw}"; return false; }
            if (profile.ClashProxyGroups.Contains(entry.Target, StringComparer.OrdinalIgnoreCase))
            { reason = $"Группа Clash ещё не исполняется ядром: {entry.Raw}"; return false; }
            var action = entry.Target.ToUpperInvariant() switch
            {
                "PROXY" => "proxy", "DIRECT" => "direct", "REJECT" or "REJECT-DROP" => "reject", _ => null
            };
            if (action is null) { reason = $"Цель Clash-правила не поддерживается: {entry.Raw}"; return false; }
            if (entry.Kind.Equals("MATCH", StringComparison.OrdinalIgnoreCase))
            {
                if (action == "reject") { reason = "Завершающий MATCH,REJECT пока не поддерживается."; return false; }
                matchSeen = true;
                continue;
            }
            var field = entry.Kind.ToUpperInvariant() switch
            {
                "DOMAIN" => "domain", "DOMAIN-SUFFIX" => "domain_suffix", "DOMAIN-KEYWORD" => "domain_keyword",
                "IP-CIDR" or "IP-CIDR6" when IPNetwork.TryParse(entry.Value, out _) => "ip_cidr", _ => null
            };
            if (field is null || string.IsNullOrWhiteSpace(entry.Value))
            { reason = $"Тип Clash-правила пока не имеет точного эквивалента: {entry.Raw}"; return false; }
            var rule = new JsonObject { [field] = new JsonArray(entry.Value) };
            if (action == "reject") rule["action"] = "reject";
            else { rule["action"] = "route"; rule["outbound"] = action; }
            rules.Add(rule);
        }
        if (!matchSeen) { reason = "В Clash-профиле нет завершающего MATCH."; return false; }
        reason = "";
        return true;
    }
}
