using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Nodes;
using System.Net;

namespace HappBox;

public static class SingBoxConfigBuilder
{
    public static string Build(AppState state, ProxyProfile selected)
    {
        var outbounds = new JsonArray();
        var endpoints = new JsonArray();
        var proxyTag = "proxy";
        var wireGuardMode = selected.Protocol == ProxyProtocol.WireGuard;
        var dnsStrategy = DnsStrategyForProfile(state, selected);

        if (wireGuardMode)
        {
            endpoints.Add(BuildWireGuardEndpoint(selected, proxyTag));
        }
        else
        {
            outbounds.Add(BuildOutbound(state, selected, proxyTag));
        }

        outbounds.Add(BuildDirectOutbound());

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = state.LogLevel, ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    BuildBootstrapDnsServer(),
                    BuildDnsServer("remote", state.DnsServer, wireGuardMode),
                    new JsonObject { ["type"] = "local", ["tag"] = "local" }
                },
                ["final"] = "remote",
                ["strategy"] = dnsStrategy
            },
            ["inbounds"] = BuildInbounds(state, selected),
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = new JsonObject
                {
                    ["server"] = "remote",
                    ["strategy"] = dnsStrategy
                },
                ["rules"] = BuildRouteRules(state, selected),
                ["final"] = RouteFinal(state, proxyTag)
            }
        };

        if (endpoints.Count > 0) root["endpoints"] = endpoints;

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        });
    }

    private static JsonArray BuildRouteRules(AppState state, ProxyProfile selected)
    {
        var rules = new JsonArray();
        if (selected.Protocol == ProxyProtocol.WireGuard)
        {
            AddWireGuardStabilityRules(rules, selected);
        }

        if (state.SniffEnabled)
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
            if (selected.Protocol != ProxyProtocol.WireGuard)
            {
                rules.Add(new JsonObject
                {
                    ["action"] = "sniff",
                    ["timeout"] = "1s"
                });
            }
        }

        if (ProxyRoutingEnabled(state))
        {
            AddDomainRouteRule(rules, state.RoutingBypassSites, "direct");
            AddDomainRouteRule(rules, state.RoutingProxySites, "proxy");
        }

        return rules;
    }

    private static void AddWireGuardStabilityRules(JsonArray rules, ProxyProfile selected)
    {
        AddEndpointDirectRule(rules, selected);

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
        if (ProxyRoutingEnabled(state) && CleanSites(state.RoutingProxySites).Count > 0)
        {
            return "direct";
        }

        return proxyTag;
    }

    private static bool ProxyRoutingEnabled(AppState state)
    {
        return state.MixedProxyEnabled && !state.TunEnabled;
    }

    private static void AddDomainRouteRule(JsonArray rules, IEnumerable<string> sites, string outbound)
    {
        var domains = CleanSites(sites);
        if (domains.Count == 0) return;

        var values = new JsonArray();
        foreach (var domain in domains)
        {
            values.Add(domain);
        }

        rules.Add(new JsonObject
        {
            ["domain_suffix"] = values,
            ["action"] = "route",
            ["outbound"] = outbound
        });
    }

    private static List<string> CleanSites(IEnumerable<string>? sites)
    {
        if (sites is null) return [];

        return sites
            .SelectMany(site => site.Split(['\r', '\n', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            .Select(site => site.Trim().TrimEnd('/'))
            .Select(site => site.StartsWith("*.") ? site[2..] : site)
            .Select(site => site.StartsWith(".") ? site[1..] : site)
            .Where(site => !string.IsNullOrWhiteSpace(site))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string DnsStrategyForProfile(AppState state, ProxyProfile selected)
    {
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

    private static JsonObject BuildBootstrapDnsServer()
    {
        return BuildUdpDnsServer("bootstrap", "1.1.1.1", "direct");
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

            inbounds.Add(new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["interface_name"] = "ior",
                ["address"] = ToJsonArray(state.TunAddress),
                ["mtu"] = tunMtu,
                ["auto_route"] = state.AutoRoute,
                ["strict_route"] = strictRoute
            });
            if (state.Windows10TunCompatibilityMode || wireGuardMode)
            {
                inbounds[^1]!["stack"] = "gvisor";
                inbounds[^1]!["endpoint_independent_nat"] = true;
                inbounds[^1]!["udp_timeout"] = "5m";
                AddWireGuardTunRouteExcludes(inbounds[^1]!.AsObject(), selected);
            }
            else if (state.StackSystem) inbounds[^1]!["stack"] = "system";
        }

        return inbounds;
    }

    private static void AddWireGuardTunRouteExcludes(JsonObject tun, ProxyProfile selected)
    {
        if (selected.Protocol != ProxyProtocol.WireGuard ||
            string.IsNullOrWhiteSpace(selected.Server) ||
            !IPAddress.TryParse(selected.Server, out var address))
        {
            return;
        }

        tun["route_exclude_address"] = new JsonArray
        {
            address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"{selected.Server}/128"
                : $"{selected.Server}/32"
        };
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
            _ => throw new NotSupportedException($"Unsupported outbound: {profile.Protocol}")
        };
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
