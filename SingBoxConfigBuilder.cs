using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Nodes;
using System.Net;

namespace HappBox;

public static class SingBoxConfigBuilder
{
    public static string Build(AppState state, ProxyProfile selected, string? resolvedServer = null, string? outboundInterface = null)
    {
        var bootstrapDomain = !string.IsNullOrWhiteSpace(resolvedServer) && !IPAddress.TryParse(selected.Server, out _);
        if (!string.IsNullOrWhiteSpace(resolvedServer))
        {
            if (!IPAddress.TryParse(resolvedServer, out _)) throw new ArgumentException("Resolved server must be an IP address.", nameof(resolvedServer));
            var originalHost = selected.Server;
            selected = JsonSerializer.Deserialize<ProxyProfile>(JsonSerializer.Serialize(selected))!;
            selected.Server = resolvedServer;
            if ((selected.TlsEnabled || selected.RealityEnabled || selected.Protocol is ProxyProtocol.Hysteria2 or ProxyProtocol.Trojan) &&
                string.IsNullOrWhiteSpace(selected.ServerName) && !IPAddress.TryParse(originalHost, out _))
                selected.ServerName = originalHost;
        }
        var outbounds = new JsonArray();
        var endpoints = new JsonArray();
        var proxyTag = "proxy";
        var wireGuardMode = selected.Protocol == ProxyProtocol.WireGuard;
        var dnsStrategy = DnsStrategyForProfile(state, selected, bootstrapDomain);

        if (wireGuardMode)
        {
            endpoints.Add(BuildWireGuardEndpoint(selected, proxyTag));
        }
        else
        {
            outbounds.Add(BuildOutbound(state, selected, proxyTag));
        }

        outbounds.Add(BuildDirectOutbound());

        var routeRules = BuildRouteRules(state, selected);
        var route = new JsonObject
        {
            ["auto_detect_interface"] = true,
            ["default_domain_resolver"] = new JsonObject
            {
                ["server"] = "remote",
                ["strategy"] = dnsStrategy
            },
            ["rules"] = routeRules,
            ["final"] = RouteFinal(state, proxyTag)
        };
        if (state.TunEnabled && !string.IsNullOrWhiteSpace(outboundInterface))
        {
            route["auto_detect_interface"] = false;
            route["default_interface"] = outboundInterface;
        }
        var happRuleSets = BuildHappRouteRuleSets(state);
        AddGeoRuleSets(happRuleSets, routeRules);
        if (happRuleSets.Count > 0) route["rule_set"] = happRuleSets;

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = state.LogLevel, ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    BuildBootstrapDnsServer(state),
                    BuildDnsServer("remote", state.DnsServer, wireGuardMode),
                    new JsonObject { ["type"] = "local", ["tag"] = "local" }
                },
                ["rules"] = BuildDnsRules(state),
                ["final"] = "remote",
                ["strategy"] = dnsStrategy
            },
            ["inbounds"] = BuildInbounds(state, selected),
            ["outbounds"] = outbounds,
            ["route"] = route
        };

        if (happRuleSets.Count > 0)
        {
            root["experimental"] = new JsonObject
            {
                ["cache_file"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["path"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ior", "rules-cache.db")
                }
            };
        }

        if (endpoints.Count > 0) root["endpoints"] = endpoints;

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        });
    }

    private static JsonArray BuildDnsRules(AppState state)
    {
        return [];
    }

    private static JsonArray BuildRouteRules(AppState state, ProxyProfile selected)
    {
        var rules = new JsonArray();
        // TUN's DNS address is private (normally the next address in its /30).
        // Hijack it before the private-address direct rule, even when sniffing
        // is disabled; otherwise DNS packets are routed out instead of handled.
        if (state.TunEnabled || state.MixedProxyEnabled)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "logical",
                ["mode"] = "or",
                ["rules"] = new JsonArray
                {
                    new JsonObject { ["port"] = 53 },
                    new JsonObject { ["protocol"] = "dns" }
                },
                ["action"] = "hijack-dns"
            });
        }
        AddEndpointDirectRule(rules, selected);
        rules.Add(new JsonObject { ["ip_is_private"] = true, ["action"] = "route", ["outbound"] = "direct" });
        if (selected.Protocol == ProxyProtocol.WireGuard)
        {
            AddWireGuardStabilityRules(rules);
        }

        if (state.SniffEnabled)
        {
            if (selected.Protocol != ProxyProtocol.WireGuard)
            {
                rules.Add(new JsonObject
                {
                    ["action"] = "sniff",
                    ["timeout"] = "1s"
                });
            }
        }

        if (HappRoutingEnabled(state))
        {
            var activeRoute = ActiveRoutingProfile(state);
            if (activeRoute?.Source == RoutingProfileSource.Clash)
            {
                if (!SingBoxClashRuleCompiler.TryCompile(activeRoute, out var ordered, out var reason))
                    throw new FormatException(reason);
                foreach (var rule in ordered) rules.Add(rule?.DeepClone());
            }
            else if (activeRoute?.Source == RoutingProfileSource.Ior) AddLocalRoutingRules(rules, activeRoute);
            else AddHappRoutingRules(rules, state);
        }

        return rules;
    }

    private static void AddWireGuardStabilityRules(JsonArray rules)
    {
        rules.Add(new JsonObject
        {
            ["domain_suffix"] = new JsonArray
            {
                "msftconnecttest.com",
                "msftncsi.com",
                "windows.com",
                "windowsupdate.com",
                "microsoft.com",
                "mp.microsoft.com",
                "dl.delivery.mp.microsoft.com",
                "do.dsp.mp.microsoft.com"
            },
            ["action"] = "route",
            ["outbound"] = "direct"
        });
    }

    private static void AddEndpointDirectRule(JsonArray rules, ProxyProfile selected)
    {
        if (string.IsNullOrWhiteSpace(selected.Server)) return;

        if (IPAddress.TryParse(selected.Server, out var address))
        {
            var cidr = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"{selected.Server}/128"
                : $"{selected.Server}/32";

            rules.Add(new JsonObject
            {
                ["ip_cidr"] = new JsonArray { cidr },
                ["action"] = "route",
                ["outbound"] = "direct"
            });
            return;
        }

        rules.Add(new JsonObject
        {
            ["domain"] = new JsonArray { selected.Server },
            ["action"] = "route",
            ["outbound"] = "direct"
        });
    }

    private static string RouteFinal(AppState state, string proxyTag)
    {
        var activeRoute = ActiveRoutingProfile(state);
        if (activeRoute?.Source is RoutingProfileSource.Ior or RoutingProfileSource.Clash)
        {
            return activeRoute.GlobalProxy ? proxyTag : "direct";
        }
        if (HappRoutingEnabled(state) && ActiveHappRoutingItems(state).Count > 0)
        {
            return ActiveHappRoutingConfigs(state).Any(HappConfigGlobalProxy) ? proxyTag : "direct";
        }

        return proxyTag;
    }

    private static bool HappRoutingEnabled(AppState state)
    {
        return state.TunEnabled || state.MixedProxyEnabled;
    }

    private static JsonArray BuildHappRouteRuleSets(AppState state)
    {
        var ruleSets = new JsonArray();
        var index = 1;
        foreach (var link in ActiveHappRoutingItems(state).Where(IsHttpRuleSetLink))
        {
            ruleSets.Add(new JsonObject
            {
                ["type"] = "remote",
                ["tag"] = HappRuleSetTag(index++),
                ["format"] = RuleSetFormat(link),
                ["url"] = link,
                ["download_detour"] = "direct"
            });
        }

        return ruleSets;
    }

    private static void AddHappRoutingRules(JsonArray rules, AppState state)
    {
        var tags = new JsonArray();
        var remoteLinks = ActiveHappRoutingItems(state).Where(IsHttpRuleSetLink).ToList();
        for (var i = 0; i < remoteLinks.Count; i++)
        {
            tags.Add(HappRuleSetTag(i + 1));
        }
        if (tags.Count > 0)
        {
            rules.Add(new JsonObject
            {
                ["rule_set"] = tags,
                ["action"] = "route",
                ["outbound"] = "proxy"
            });
        }

        foreach (var config in ActiveHappRoutingConfigs(state))
        {
            AddHappConfigRules(rules, config);
        }
    }

    private static void AddLocalRoutingRules(JsonArray rules, HappRoutingProfile profile)
    {
        AddLocalDomainRule(rules, profile.BlockSites, "reject", null);
        AddLocalTextRule(rules, profile.BlockDomains, "domain", "reject", null);
        AddLocalTextRule(rules, profile.BlockKeywords, "domain_keyword", "reject", null);
        AddLocalIpRule(rules, profile.BlockIp, "reject", null);
        AddLocalDomainRule(rules, profile.ProxySites, "route", "proxy");
        AddLocalTextRule(rules, profile.ProxyDomains, "domain", "route", "proxy");
        AddLocalTextRule(rules, profile.ProxyKeywords, "domain_keyword", "route", "proxy");
        AddLocalIpRule(rules, profile.ProxyIp, "route", "proxy");
        AddLocalDomainRule(rules, profile.DirectSites, "route", "direct");
        AddLocalTextRule(rules, profile.DirectDomains, "domain", "route", "direct");
        AddLocalTextRule(rules, profile.DirectKeywords, "domain_keyword", "route", "direct");
        AddLocalIpRule(rules, profile.DirectIp, "route", "direct");
    }

    private static void AddLocalTextRule(JsonArray rules, IEnumerable<string> values, string field, string action, string? outbound)
    {
        var clean = values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (clean.Length == 0) return;
        var entries = new JsonArray();
        foreach (var value in clean) entries.Add(value);
        var rule = new JsonObject { [field] = entries, ["action"] = action };
        if (outbound is not null) rule["outbound"] = outbound;
        rules.Add(rule);
    }

    private static void AddLocalDomainRule(JsonArray rules, IEnumerable<string> values, string action, string? outbound)
    {
        var clean = values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (clean.Length == 0) return;
        var domains = new JsonArray();
        foreach (var value in clean)
        {
            if (value.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase))
                AddGeoRule(rules, "geosite", value[8..], action, outbound);
            else domains.Add(value);
        }
        if (domains.Count > 0)
        {
            var rule = new JsonObject { ["domain_suffix"] = domains, ["action"] = action };
            if (outbound is not null) rule["outbound"] = outbound;
            rules.Add(rule);
        }
    }

    private static void AddLocalIpRule(JsonArray rules, IEnumerable<string> values, string action, string? outbound)
    {
        AddIpRules(rules, values, action, outbound);
    }

    private static HappRoutingProfile? ActiveRoutingProfile(AppState state)
    {
        return state.HappRoutingProfiles.FirstOrDefault(profile => profile.Id == state.SelectedHappRoutingProfileId) ??
               state.HappRoutingProfiles.FirstOrDefault();
    }

    private static string HappRuleSetTag(int index)
    {
        return $"happ-routing-{index}";
    }

    private static List<string> ActiveHappRoutingItems(AppState state)
    {
        var selected = ActiveRoutingProfile(state);
        if (selected?.Source is RoutingProfileSource.Ior or RoutingProfileSource.Clash) return [];
        return selected is null
            ? CleanRoutingItems(state.HappRoutingLinks)
            : CleanRoutingItems(selected.Links);
    }

    private static List<JsonElement> ActiveHappRoutingConfigs(AppState state)
    {
        return ActiveHappRoutingItems(state)
            .Select(TryParseHappRoutingConfig)
            .Where(config => config.HasValue)
            .Select(config => config!.Value)
            .ToList();
    }

    private static string RuleSetFormat(string url)
    {
        return url.EndsWith(".srs", StringComparison.OrdinalIgnoreCase) ? "binary" : "source";
    }

    private static List<string> CleanRoutingItems(IEnumerable<string>? links)
    {
        if (links is null) return [];

        return links
            .Select(link => link.Trim())
            .Where(link => !string.IsNullOrWhiteSpace(link))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsHttpRuleSetLink(string link)
    {
        return Uri.TryCreate(link, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
               !uri.Host.Equals("routing.happ.su", StringComparison.OrdinalIgnoreCase);
    }

    internal static JsonElement? TryParseHappRoutingConfig(string value)
    {
        var payload = ExtractHappPayload(value.Trim());
        foreach (var candidate in DecodeHappPayloadCandidates(payload))
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                return doc.RootElement.Clone();
            }
            catch
            {
            }
        }

        return null;
    }

    private static string ExtractHappPayload(string value)
    {
        if (!value.StartsWith("happ://routing/", StringComparison.OrdinalIgnoreCase)) return value;

        var marker = value.Contains("/onadd/", StringComparison.OrdinalIgnoreCase) ? "/onadd/" : "/add/";
        var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? value : Uri.UnescapeDataString(value[(index + marker.Length)..]);
    }

    private static IEnumerable<string> DecodeHappPayloadCandidates(string payload)
    {
        yield return payload;

        var normalized = payload.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        if (padding > 0) normalized = normalized.PadRight(normalized.Length + 4 - padding, '=');

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(normalized);
        }
        catch
        {
            yield break;
        }

        yield return System.Text.Encoding.UTF8.GetString(bytes);
    }

    internal static bool HappConfigGlobalProxy(JsonElement config)
    {
        if (!TryGetProperty(config, "GlobalProxy", out var value)) return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var enabled) && enabled,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            _ => false
        };
    }

    private static void AddHappConfigRules(JsonArray rules, JsonElement config)
    {
        foreach (var action in HappRouteOrder(config))
        {
            switch (action)
            {
                case "block":
                    AddHappDomainRule(rules, config, "BlockSites", "reject", null);
                    AddHappIpRule(rules, config, "BlockIp", "reject", null);
                    break;
                case "proxy":
                    AddHappDomainRule(rules, config, "ProxySites", "route", "proxy");
                    AddHappIpRule(rules, config, "ProxyIp", "route", "proxy");
                    break;
                case "direct":
                    AddHappDomainRule(rules, config, "DirectSites", "route", "direct");
                    AddHappIpRule(rules, config, "DirectIp", "route", "direct");
                    break;
            }
        }
    }

    internal static IEnumerable<string> HappRouteOrder(JsonElement config)
    {
        var value = TryGetString(config, "RouteOrder");
        var parts = string.IsNullOrWhiteSpace(value)
            ? ["block", "proxy", "direct"]
            : value.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var normalized = part.Trim().ToLowerInvariant();
            if (normalized is "block" or "proxy" or "direct") yield return normalized;
        }
    }

    private static void AddHappDomainRule(JsonArray rules, JsonElement config, string property, string action, string? outbound)
    {
        var values = GetStringArray(config, property);
        if (values.Count == 0) return;

        var domains = new JsonArray();
        foreach (var value in values)
        {
            if (value.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase))
                AddGeoRule(rules, "geosite", value[8..], action, outbound);
            else domains.Add(value);
        }
        if (domains.Count > 0)
        {
            var rule = new JsonObject { ["domain_suffix"] = domains, ["action"] = action };
            if (!string.IsNullOrWhiteSpace(outbound)) rule["outbound"] = outbound;
            rules.Add(rule);
        }
    }

    private static void AddHappIpRule(JsonArray rules, JsonElement config, string property, string action, string? outbound)
    {
        AddIpRules(rules, GetStringArray(config, property), action, outbound);
    }

    private static void AddIpRules(JsonArray rules, IEnumerable<string> values, string action, string? outbound)
    {
        var cidrs = new JsonArray();
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (value.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase))
            {
                var code = value[6..];
                if (code.Equals("private", StringComparison.OrdinalIgnoreCase))
                {
                    var privateRule = new JsonObject { ["ip_is_private"] = true, ["action"] = action };
                    if (outbound is not null) privateRule["outbound"] = outbound;
                    rules.Add(privateRule);
                }
                else AddGeoRule(rules, "geoip", code, action, outbound);
                continue;
            }
            if (IPAddress.TryParse(value, out var address))
            {
                cidrs.Add(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"{address}/128" : $"{address}/32");
                continue;
            }
            if (!IPNetwork.TryParse(value, out _))
                throw new FormatException($"Некорректное IP/CIDR-правило маршрута: {value}");
            cidrs.Add(value);
        }
        if (cidrs.Count > 0)
        {
            var rule = new JsonObject { ["ip_cidr"] = cidrs, ["action"] = action };
            if (outbound is not null) rule["outbound"] = outbound;
            rules.Add(rule);
        }
    }

    private static void AddGeoRule(JsonArray rules, string kind, string code, string action, string? outbound)
    {
        code = code.Trim().ToLowerInvariant();
        if (code.Length == 0 || code.Any(ch => !char.IsAsciiLetterLower(ch) && !char.IsAsciiDigit(ch) && ch != '-'))
            throw new FormatException($"Некорректное {kind}-правило маршрута: {code}");
        var rule = new JsonObject { ["rule_set"] = new JsonArray($"{kind}-{code}"), ["action"] = action };
        if (outbound is not null) rule["outbound"] = outbound;
        rules.Add(rule);
    }

    private static void AddGeoRuleSets(JsonArray definitions, JsonArray rules)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules.OfType<JsonObject>())
        {
            if (rule["rule_set"] is not JsonArray tags) continue;
            foreach (var node in tags)
            {
                var tag = node?.GetValue<string>();
                if (tag is null || !seen.Add(tag)) continue;
                var kind = tag.StartsWith("geoip-", StringComparison.Ordinal) ? "geoip" :
                    tag.StartsWith("geosite-", StringComparison.Ordinal) ? "geosite" : null;
                if (kind is null) continue;
                var repository = kind == "geoip" ? "sing-geoip" : "sing-geosite";
                var bundledPath = tag == "geoip-ru"
                    ? Path.Combine(AppContext.BaseDirectory, "core", "rules", "geoip-ru.srs") : null;
                if (bundledPath is not null && File.Exists(bundledPath))
                {
                    definitions.Add(new JsonObject { ["type"] = "local", ["tag"] = tag,
                        ["format"] = "binary", ["path"] = bundledPath });
                }
                else
                {
                    definitions.Add(new JsonObject
                    {
                        ["type"] = "remote", ["tag"] = tag, ["format"] = "binary",
                        ["url"] = $"https://raw.githubusercontent.com/SagerNet/{repository}/rule-set/{tag}.srs",
                        ["download_detour"] = "direct"
                    });
                }
            }
        }
    }

    internal static List<string> GetStringArray(JsonElement config, string property)
    {
        if (!TryGetProperty(config, property, out var value) || value.ValueKind != JsonValueKind.Array) return [];

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static string? TryGetString(JsonElement config, string property)
    {
        return TryGetProperty(config, property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryGetProperty(JsonElement config, string property, out JsonElement value)
    {
        foreach (var item in config.EnumerateObject())
        {
            if (item.Name.Equals(property, StringComparison.OrdinalIgnoreCase))
            {
                value = item.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string DnsStrategyForProfile(AppState state, ProxyProfile selected, bool bootstrapDomain)
    {
        if (state.TunEnabled && bootstrapDomain) return "ipv4_only";
        return selected.Protocol == ProxyProtocol.WireGuard && !HasIpv6Address(selected.LocalAddress)
            ? "ipv4_only"
            : state.DnsStrategy;
    }

    private static JsonObject BuildDnsServer(string tag, string server, bool preferPlainUdp)
    {
        var value = string.IsNullOrWhiteSpace(server) ? "1.1.1.1" : server.Trim();

        if (preferPlainUdp)
        {
            return BuildUdpDnsServer(tag, PlainDnsAddressForWireGuard(value), "proxy");
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "h3"))
        {
            return new JsonObject
            {
                ["type"] = "https",
                ["tag"] = tag,
                ["server"] = uri.Host,
                ["server_port"] = uri.IsDefaultPort ? 443 : uri.Port,
                ["path"] = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? "/dns-query" : uri.AbsolutePath,
                ["detour"] = "proxy",
                ["domain_resolver"] = "bootstrap",
                ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = uri.Host }
            };
        }

        if (value is "1.1.1.1" or "1.0.0.1" or "8.8.8.8" or "8.8.4.4")
        {
            var google = value.StartsWith("8.", StringComparison.Ordinal);
            return new JsonObject
            {
                ["type"] = "https",
                ["tag"] = tag,
                ["server"] = value,
                ["server_port"] = 443,
                ["path"] = "/dns-query",
                ["detour"] = "proxy",
                ["tls"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["server_name"] = google ? "dns.google" : "cloudflare-dns.com"
                }
            };
        }

        return BuildUdpDnsServer(tag, value, "proxy");
    }

    private static JsonObject BuildUdpDnsServer(string tag, string server, string? detour)
    {
        var dns = new JsonObject
        {
            ["type"] = "udp",
            ["tag"] = tag,
            ["server"] = server,
            ["server_port"] = 53
        };

        if (!string.IsNullOrWhiteSpace(detour)) dns["detour"] = detour;
        return dns;
    }

    private static string PlainDnsAddressForWireGuard(string value)
    {
        if (IsPlainDnsAddress(value)) return value;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.Host.ToLowerInvariant() switch
            {
                "cloudflare-dns.com" or "one.one.one.one" => "1.1.1.1",
                "dns.google" or "google.dns" => "8.8.8.8",
                "dns.quad9.net" or "quad9.net" => "9.9.9.9",
                _ => "1.1.1.1"
            };
        }

        return "1.1.1.1";
    }

    private static bool IsPlainDnsAddress(string value)
    {
        return !value.Contains("://", StringComparison.Ordinal) &&
               !value.Contains(':', StringComparison.Ordinal);
    }

    private static JsonObject BuildBootstrapDnsServer(AppState state)
    {
        return BuildUdpDnsServer("bootstrap", BootstrapDnsResolver.ServerAddress(state.BootstrapDnsServer).ToString(), "direct");
    }

    private static JsonArray BuildInbounds(AppState state, ProxyProfile selected)
    {
        var inbounds = new JsonArray();
        if (state.MixedProxyEnabled)
        {
            inbounds.Add(new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = "mixed-in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = state.MixedProxyPort
            });
        }

        if (state.TunEnabled)
        {
            var tunMtu = EffectiveTunMtu(state, selected);
            var wireGuardMode = selected.Protocol == ProxyProtocol.WireGuard;
            var strictRoute = state.Windows10TunCompatibilityMode || wireGuardMode
                ? false
                : state.StrictRoute;

            var tun = new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["interface_name"] = "ior",
                ["address"] = ToJsonArray(state.TunAddress),
                ["mtu"] = tunMtu,
                ["auto_route"] = state.AutoRoute,
                ["strict_route"] = strictRoute
            };
            AddTunRouteExcludes(tun, selected);
            inbounds.Add(tun);
            if (state.Windows10TunCompatibilityMode || wireGuardMode)
            {
                inbounds[^1]!["stack"] = "gvisor";
                inbounds[^1]!["endpoint_independent_nat"] = true;
                inbounds[^1]!["udp_timeout"] = "5m";
            }
            else if (state.StackSystem) inbounds[^1]!["stack"] = "system";
            else inbounds[^1]!["stack"] = "gvisor";
        }

        return inbounds;
    }

    private static void AddTunRouteExcludes(JsonObject tun, ProxyProfile selected)
    {
        var excludes = new List<string>();
        if (!string.IsNullOrWhiteSpace(selected.Server) &&
            IPAddress.TryParse(selected.Server, out var address))
        {
            excludes.Add(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"{selected.Server}/128"
                : $"{selected.Server}/32");
        }

        if (excludes.Count == 0) return;
        var array = new JsonArray();
        foreach (var value in excludes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            array.Add(value);
        }
        tun["route_exclude_address"] = array;
    }

    private static int EffectiveTunMtu(AppState state, ProxyProfile selected)
    {
        if (selected.Protocol == ProxyProtocol.WireGuard)
        {
            return 1280;
        }

        return state.Windows10TunCompatibilityMode
            ? Math.Clamp(state.TunMtu, 1280, 1500)
            : Math.Clamp(state.TunMtu, 1280, 10000);
    }

    private static JsonObject BuildOutbound(AppState state, ProxyProfile profile, string tag)
    {
        if (RemovedProtocolPolicy.IsRemoved(profile)) throw new NotSupportedException(RemovedProtocolPolicy.Message);
        return profile.Protocol switch
        {
            ProxyProtocol.Vless => BuildVless(state, profile, tag),
            ProxyProtocol.Vmess => BuildVmess(state, profile, tag),
            ProxyProtocol.Trojan => BuildTrojan(state, profile, tag),
            ProxyProtocol.Shadowsocks => new JsonObject
            {
                ["type"] = "shadowsocks",
                ["tag"] = tag,
                ["server"] = profile.Server,
                ["server_port"] = profile.Port,
                ["method"] = profile.Method,
                ["password"] = profile.Password
            },
            ProxyProtocol.Hysteria2 => BuildHysteria2(state, profile, tag),
            ProxyProtocol.Http or ProxyProtocol.Socks => BuildForwardProxy(state, profile, tag),
            _ => throw new NotSupportedException($"Unsupported outbound: {profile.Protocol}")
        };
    }

    private static JsonObject BuildForwardProxy(AppState state, ProxyProfile profile, string tag)
    {
        var outbound = new JsonObject { ["type"] = profile.Protocol == ProxyProtocol.Http ? "http" : "socks",
            ["tag"] = tag, ["server"] = profile.Server, ["server_port"] = profile.Port, ["domain_resolver"] = "bootstrap" };
        if (profile.Protocol == ProxyProtocol.Socks) outbound["version"] = "5";
        if (!string.IsNullOrEmpty(profile.Username)) { outbound["username"] = profile.Username; outbound["password"] = profile.Password; }
        if (profile.TlsEnabled) outbound["tls"] = BuildTls(state, profile);
        return outbound;
    }

    private static JsonObject BuildVless(AppState state, ProxyProfile profile, string tag)
    {
        var outbound = new JsonObject
        {
            ["type"] = "vless",
            ["tag"] = tag,
            ["server"] = profile.Server,
            ["server_port"] = profile.Port,
            ["domain_resolver"] = "bootstrap",
            ["uuid"] = profile.UserId,
            ["packet_encoding"] = "xudp"
        };

        var flow = NormalizeVlessFlow(profile.Flow);
        if (!string.IsNullOrWhiteSpace(flow)) outbound["flow"] = flow;
        ApplyTransport(outbound, profile);
        if (profile.TlsEnabled || profile.RealityEnabled)
        {
            outbound["tls"] = BuildTls(state, profile);
        }

        return outbound;
    }

    private static string? NormalizeVlessFlow(string? flow)
    {
        return flow?.Trim() switch
        {
            "xtls-rprx-vision" => "xtls-rprx-vision",
            "xtls-rprx-vision-udp443" => "xtls-rprx-vision",
            _ => null
        };
    }

    private static JsonObject BuildHysteria2(AppState state, ProxyProfile profile, string tag)
    {
        var outbound = new JsonObject
        {
            ["type"] = "hysteria2",
            ["tag"] = tag,
            ["server"] = profile.Server,
            ["server_port"] = profile.Port,
            ["domain_resolver"] = "bootstrap",
            ["password"] = profile.Password,
            ["tls"] = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = profile.ServerName,
                ["insecure"] = state.AllowInsecureCertificates || profile.AllowInsecure
            }
        };

        if (!string.IsNullOrWhiteSpace(profile.ObfsType) && !string.IsNullOrWhiteSpace(profile.ObfsPassword))
        {
            outbound["obfs"] = new JsonObject
            {
                ["type"] = profile.ObfsType,
                ["password"] = profile.ObfsPassword
            };
        }

        return outbound;
    }

    private static JsonObject BuildVmess(AppState state, ProxyProfile profile, string tag)
    {
        var outbound = new JsonObject
        {
            ["type"] = "vmess",
            ["tag"] = tag,
            ["server"] = profile.Server,
            ["server_port"] = profile.Port,
            ["domain_resolver"] = "bootstrap",
            ["uuid"] = profile.UserId,
            ["security"] = string.IsNullOrWhiteSpace(profile.Security) ? "auto" : profile.Security,
            ["alter_id"] = profile.AlterId ?? 0,
            ["packet_encoding"] = "xudp"
        };

        ApplyTransport(outbound, profile);
        if (profile.TlsEnabled || profile.RealityEnabled)
        {
            outbound["tls"] = BuildTls(state, profile);
        }

        return outbound;
    }

    private static JsonObject BuildTrojan(AppState state, ProxyProfile profile, string tag)
    {
        var outbound = new JsonObject
        {
            ["type"] = "trojan",
            ["tag"] = tag,
            ["server"] = profile.Server,
            ["server_port"] = profile.Port,
            ["domain_resolver"] = "bootstrap",
            ["password"] = profile.Password
        };

        ApplyTransport(outbound, profile);
        if (profile.TlsEnabled || profile.RealityEnabled)
        {
            outbound["tls"] = BuildTls(state, profile);
        }

        return outbound;
    }

    private static JsonObject BuildTls(AppState state, ProxyProfile profile)
    {
        var tls = new JsonObject
        {
            ["enabled"] = true,
            ["server_name"] = profile.ServerName,
            ["insecure"] = state.AllowInsecureCertificates || profile.AllowInsecure
        };

        if (profile.RealityEnabled)
        {
            tls["reality"] = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = profile.PublicKey,
                ["short_id"] = profile.ShortId
            };
            tls["utls"] = new JsonObject
            {
                ["enabled"] = true,
                ["fingerprint"] = profile.Fingerprint ?? "chrome"
            };
        }

        return tls;
    }

    private static void ApplyTransport(JsonObject outbound, ProxyProfile profile)
    {
        var network = profile.Network?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(network) || network is "tcp" or "raw") return;

        if (network == "ws" || network == "websocket")
        {
            var transport = new JsonObject
            {
                ["type"] = "ws",
                ["path"] = string.IsNullOrWhiteSpace(profile.Path) ? "/" : profile.Path
            };
            if (!string.IsNullOrWhiteSpace(profile.Host))
            {
                transport["headers"] = new JsonObject { ["Host"] = profile.Host };
            }
            outbound["transport"] = transport;
            return;
        }

        if (network == "grpc")
        {
            outbound["transport"] = new JsonObject
            {
                ["type"] = "grpc",
                ["service_name"] = profile.ServiceName ?? profile.Path?.TrimStart('/') ?? ""
            };
            return;
        }

        if (network == "http" || network == "h2")
        {
            var transport = new JsonObject { ["type"] = "http" };
            if (!string.IsNullOrWhiteSpace(profile.Host))
            {
                transport["host"] = new JsonArray { profile.Host };
            }
            if (!string.IsNullOrWhiteSpace(profile.Path))
            {
                transport["path"] = profile.Path;
            }
            outbound["transport"] = transport;
        }
    }

    private static JsonObject BuildWireGuardEndpoint(ProxyProfile profile, string tag)
    {
        return new JsonObject
        {
            ["type"] = "wireguard",
            ["tag"] = tag,
            ["system"] = false,
            ["mtu"] = 1280,
            ["address"] = ToJsonArray(profile.LocalAddress),
            ["private_key"] = profile.PrivateKey,
            ["detour"] = "direct",
            ["domain_resolver"] = "bootstrap",
            ["udp_fragment"] = false,
            ["connect_timeout"] = "5s",
            ["peers"] = new JsonArray
            {
                new JsonObject
                {
                    ["address"] = profile.Server,
                    ["port"] = profile.Port,
                    ["public_key"] = profile.PeerPublicKey,
                    ["pre_shared_key"] = profile.PreSharedKey,
                    ["allowed_ips"] = WireGuardAllowedIps(profile),
                    ["persistent_keepalive_interval"] = profile.PersistentKeepalive ?? 15
                }
            }
        };
    }

    private static JsonObject BuildDirectOutbound()
    {
        return new JsonObject
        {
            ["type"] = "direct",
            ["tag"] = "direct",
            ["domain_resolver"] = "bootstrap",
            ["udp_fragment"] = false,
            ["connect_timeout"] = "5s"
        };
    }

    private static JsonArray WireGuardAllowedIps(ProxyProfile profile)
    {
        var hasIpv6 = HasIpv6Address(profile.LocalAddress);
        var array = new JsonArray();
        foreach (var item in (profile.AllowedIps ?? "0.0.0.0/0").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!hasIpv6 && item.Contains(':', StringComparison.Ordinal)) continue;
            array.Add(item);
        }

        if (array.Count == 0) array.Add("0.0.0.0/0");
        return array;
    }

    private static bool HasIpv6Address(string? csv)
    {
        return (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(item => item.Contains(':', StringComparison.Ordinal));
    }

    private static JsonArray ToJsonArray(string? csv)
    {
        var array = new JsonArray();
        foreach (var item in (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            array.Add(item);
        }

        return array;
    }
}
