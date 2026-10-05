namespace HappBox;

public static class ClashProfileParser
{
    public static HappRoutingProfile Parse(string yaml, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(yaml)) throw new FormatException("Профиль Clash пуст.");
        yaml = RemovedProtocolPolicy.SanitizeClash(yaml);
        var profile = new HappRoutingProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Clash профиль" : name.Trim(),
            Source = RoutingProfileSource.Clash,
            OriginalClashYaml = yaml,
            GlobalProxy = true
        };
        var section = "";
        var foundClashSection = false;
        var foundRule = false;
        var lastBucket = -1;
        var matchSeen = false;
        foreach (var raw in yaml.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!char.IsWhiteSpace(raw[0]) && line.EndsWith(':'))
            {
                section = line[..^1].ToLowerInvariant();
                if (section is "proxies" or "proxy-groups" or "rules" or "rule-providers" or "dns" or "tun") foundClashSection = true;
                continue;
            }
            if (section == "proxy-groups" && line.StartsWith("- name:", StringComparison.OrdinalIgnoreCase))
            {
                var group = Unquote(line[7..].Trim());
                if (group.Length > 0) profile.ClashProxyGroups.Add(group);
            }
            if (section != "rules" || !line.StartsWith('-')) continue;
            foundRule = true;
            var expression = Unquote(line[1..].Trim());
            var parts = expression.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) { profile.ClashUnmappedRules.Add(expression); continue; }
            var kind = parts[0].ToUpperInvariant();
            var targetValue = kind == "MATCH" ? parts[1] : parts.Length >= 3 ? parts[2] : "";
            profile.ClashOrderedRules.Add(new ClashRuleEntry
            {
                Kind = kind, Value = kind == "MATCH" ? "" : parts[1], Target = targetValue,
                NoResolve = parts.Skip(3).Any(part => part.Equals("no-resolve", StringComparison.OrdinalIgnoreCase)), Raw = expression
            });
            if (matchSeen) profile.ClashUnmappedRules.Add(expression);
            if (kind == "MATCH")
            {
                matchSeen = true;
                profile.GlobalProxy = !parts[1].Equals("DIRECT", StringComparison.OrdinalIgnoreCase);
                if ((!parts[1].Equals("PROXY", StringComparison.OrdinalIgnoreCase) && !parts[1].Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) ||
                    profile.ClashProxyGroups.Contains(parts[1], StringComparer.OrdinalIgnoreCase))
                    profile.ClashUnmappedRules.Add(expression);
                continue;
            }
            if (parts.Length < 3) { profile.ClashUnmappedRules.Add(expression); continue; }
            var target = parts[2].ToUpperInvariant();
            var value = parts[1];
            if (value.Length == 0) { profile.ClashUnmappedRules.Add(expression); continue; }
            var isIp = kind is "IP-CIDR" or "IP-CIDR6";
            var isDomain = kind is "DOMAIN" or "DOMAIN-SUFFIX" or "DOMAIN-KEYWORD";
            if (!isIp && !isDomain) { profile.ClashUnmappedRules.Add(expression); continue; }
            var bucket = target is "REJECT" or "REJECT-DROP" ? 0 : target == "DIRECT" ? 2 : 1;
            if (bucket < lastBucket || !IsSupportedTarget(parts[2]) || profile.ClashProxyGroups.Contains(parts[2], StringComparer.OrdinalIgnoreCase) ||
                parts.Skip(3).Any(part => part.Equals("no-resolve", StringComparison.OrdinalIgnoreCase)))
                profile.ClashUnmappedRules.Add(expression);
            lastBucket = Math.Max(lastBucket, bucket);
            var list = target switch
            {
                "DIRECT" => isIp ? profile.DirectIp : kind == "DOMAIN" ? profile.DirectDomains : kind == "DOMAIN-KEYWORD" ? profile.DirectKeywords : profile.DirectSites,
                "REJECT" or "REJECT-DROP" => isIp ? profile.BlockIp : kind == "DOMAIN" ? profile.BlockDomains : kind == "DOMAIN-KEYWORD" ? profile.BlockKeywords : profile.BlockSites,
                _ => isIp ? profile.ProxyIp : kind == "DOMAIN" ? profile.ProxyDomains : kind == "DOMAIN-KEYWORD" ? profile.ProxyKeywords : profile.ProxySites
            };
            if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
        }
        if (!foundClashSection || !foundRule) throw new FormatException("Не найден список правил Clash/Mihomo. Проверьте YAML подписки.");
        profile.ClashUnmappedRules = profile.ClashUnmappedRules.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        profile.OrderedRules = profile.ClashOrderedRules.Select(rule => new OrderedRouteRule
        {
            Kind = rule.Kind, Value = rule.Value, Target = rule.Target, NoResolve = rule.NoResolve,
            Raw = rule.Raw, Source = RoutingProfileSource.Clash
        }).ToList();
        profile.RoutePlan = ClashRoutePlanReader.Parse(yaml);
        profile.OrderedRules = profile.RoutePlan.Rules;
        profile.ClashProxyGroups = profile.RoutePlan.Groups.Select(group => group.Name).ToList();
        profile.ClashOrderedRules = profile.RoutePlan.Rules.Select(rule => new ClashRuleEntry
            { Kind = rule.Kind, Value = rule.Value, Target = rule.Target, NoResolve = rule.NoResolve, Raw = rule.Raw }).ToList();
        return profile;
    }

    private static bool IsSupportedTarget(string value) => value.Equals("PROXY", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("DIRECT", StringComparison.OrdinalIgnoreCase) || value.Equals("REJECT", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("REJECT-DROP", StringComparison.OrdinalIgnoreCase);

    private static string Unquote(string value) => value.Length >= 2 &&
        ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
        ? value[1..^1] : value;
}
