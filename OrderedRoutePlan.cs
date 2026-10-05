namespace HappBox;

/// <summary>Preserves route order and targets before a backend-specific conversion.</summary>
public static class OrderedRoutePlan
{
    public static bool TryCreate(HappRoutingProfile profile, out IReadOnlyList<OrderedRouteRule> rules,
        out string reason)
    {
        if (profile.RoutePlan is { } plan)
        {
            if (plan.Version != 2 || plan.Rules.Count == 0 || plan.Rules[^1].Kind.ToUpperInvariant() != "MATCH")
            { rules = []; reason = "Некорректная версия RoutePlanV2 или отсутствует завершающий MATCH."; return false; }
            rules = plan.Rules; reason = ""; return true;
        }
        var missingClashRule = profile.Source == RoutingProfileSource.Clash
            ? profile.ClashUnmappedRules.FirstOrDefault(raw => profile.ClashOrderedRules.All(rule =>
                !string.Equals(rule.Raw, raw, StringComparison.OrdinalIgnoreCase))) : null;
        if (missingClashRule is not null)
        {
            rules = [];
            reason = $"Clash-правило не удалось сохранить: {missingClashRule}";
            return false;
        }
        if (profile.OrderedRules.Count > 0)
        {
            if (profile.OrderedRules[^1].Kind.ToUpperInvariant() != "MATCH")
            {
                rules = [];
                reason = "У маршрута нет завершающего правила MATCH.";
                return false;
            }
            rules = profile.OrderedRules;
            reason = "";
            return true;
        }

        var result = new List<OrderedRouteRule>();
        if (profile.Source == RoutingProfileSource.Clash)
        {
            if (profile.ClashOrderedRules.Count == 0)
            {
                rules = [];
                reason = "У Clash-профиля отсутствует упорядоченный список правил.";
                return false;
            }
            result.AddRange(profile.ClashOrderedRules.Select(rule => new OrderedRouteRule
            {
                Kind = rule.Kind, Value = rule.Value, Target = rule.Target,
                NoResolve = rule.NoResolve, Raw = rule.Raw, Source = RoutingProfileSource.Clash
            }));
        }
        else if (profile.Source == RoutingProfileSource.Happ && profile.Links.Count > 0)
        {
            var globalProxy = false;
            foreach (var link in profile.Links)
            {
                var config = SingBoxConfigBuilder.TryParseHappRoutingConfig(link);
                if (config is null)
                {
                    rules = [];
                    reason = "Happ-маршрут содержит ссылку или JSON, который нельзя преобразовать без потери правил.";
                    return false;
                }
                var routeOrder = SingBoxConfigBuilder.TryGetString(config.Value, "RouteOrder");
                if (!string.IsNullOrWhiteSpace(routeOrder) && routeOrder.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(item => item.Trim().ToLowerInvariant() is not ("block" or "proxy" or "direct")))
                {
                    rules = [];
                    reason = $"Happ-маршрут содержит неизвестное действие в RouteOrder: {routeOrder}";
                    return false;
                }
                globalProxy |= SingBoxConfigBuilder.HappConfigGlobalProxy(config.Value);
                foreach (var action in SingBoxConfigBuilder.HappRouteOrder(config.Value))
                {
                    var prefix = action switch { "block" => "Block", "proxy" => "Proxy", _ => "Direct" };
                    var target = action switch { "block" => "REJECT", "proxy" => "PROXY", _ => "DIRECT" };
                    foreach (var value in SingBoxConfigBuilder.GetStringArray(config.Value, prefix + "Sites"))
                        result.Add(new OrderedRouteRule { Kind = HappDomainKind(value),
                            Value = StripHappDomainPrefix(value),
                            Target = target, Raw = value, Source = RoutingProfileSource.Happ });
                    foreach (var value in SingBoxConfigBuilder.GetStringArray(config.Value, prefix + "Ip"))
                        result.Add(new OrderedRouteRule { Kind = value.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase) ? "GEOIP" : "IP-CIDR",
                            Value = value.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase) ? value[6..] : value,
                            Target = target, Raw = value, Source = RoutingProfileSource.Happ });
                }
            }
            result.Add(new OrderedRouteRule { Kind = "MATCH", Target = globalProxy ? "PROXY" : "DIRECT",
                Raw = "Happ final", Source = RoutingProfileSource.Happ });
        }
        else
        {
            foreach (var (prefix, target) in new[] { ("Block", "REJECT"), ("Proxy", "PROXY"), ("Direct", "DIRECT") })
            {
                Add(result, profile, prefix, target);
            }
            result.Add(new OrderedRouteRule { Kind = "MATCH", Target = profile.GlobalProxy ? "PROXY" : "DIRECT",
                Raw = "ior final", Source = profile.Source });
        }

        if (result.Count == 0 || result[^1].Kind != "MATCH")
        {
            rules = [];
            reason = "У маршрута нет завершающего правила MATCH.";
            return false;
        }
        rules = result;
        reason = "";
        return true;
    }

    private static string HappDomainKind(string value) => value.Split(':', 2)[0].ToLowerInvariant() switch
    {
        "geosite" => "GEOSITE", "full" => "DOMAIN", "keyword" => "DOMAIN-KEYWORD",
        "regexp" => "REGEXP", _ => "DOMAIN-SUFFIX"
    };

    private static string StripHappDomainPrefix(string value)
    {
        var prefix = value.Split(':', 2)[0].ToLowerInvariant();
        return prefix is "geosite" or "full" or "keyword" or "regexp" or "domain"
            ? value[(prefix.Length + 1)..] : value;
    }

    private static void Add(List<OrderedRouteRule> rules, HappRoutingProfile profile, string prefix, string target)
    {
        void Append(IEnumerable<string> values, string kind)
        {
            foreach (var value in values)
                rules.Add(new OrderedRouteRule { Kind = kind, Value = value, Target = target,
                    Raw = $"{kind},{value},{target}", Source = profile.Source });
        }
        switch (prefix)
        {
            case "Block":
                Append(profile.BlockSites, "DOMAIN-SUFFIX"); Append(profile.BlockDomains, "DOMAIN");
                Append(profile.BlockKeywords, "DOMAIN-KEYWORD"); Append(profile.BlockIp, "IP-CIDR"); break;
            case "Proxy":
                Append(profile.ProxySites, "DOMAIN-SUFFIX"); Append(profile.ProxyDomains, "DOMAIN");
                Append(profile.ProxyKeywords, "DOMAIN-KEYWORD"); Append(profile.ProxyIp, "IP-CIDR"); break;
            default:
                Append(profile.DirectSites, "DOMAIN-SUFFIX"); Append(profile.DirectDomains, "DOMAIN");
                Append(profile.DirectKeywords, "DOMAIN-KEYWORD"); Append(profile.DirectIp, "IP-CIDR"); break;
        }
    }
}
