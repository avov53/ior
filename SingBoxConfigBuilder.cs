using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Nodes;

namespace HappBox;

public static class SingBoxConfigBuilder
{
    public static string Build(AppState state, ProxyProfile selected)
    {
        var outbounds = new JsonArray();
        var endpoints = new JsonArray();
        var proxyTag = "proxy";

        if (selected.Protocol == ProxyProtocol.WireGuard)
        {
            endpoints.Add(BuildWireGuardEndpoint(selected, proxyTag));
        }
        else
        {
            outbounds.Add(BuildOutbound(state, selected, proxyTag));
        }

        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = state.LogLevel, ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    BuildBootstrapDnsServer(),
                    BuildDnsServer("remote", state.DnsServer),
                    new JsonObject { ["type"] = "local", ["tag"] = "local" }
                },
                ["final"] = "remote",
                ["strategy"] = state.DnsStrategy
            },
            ["inbounds"] = BuildInbounds(state),
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = new JsonObject
                {
                    ["server"] = "remote",
                    ["strategy"] = state.DnsStrategy
                },
                ["rules"] = BuildRouteRules(state),
                ["final"] = proxyTag
            }
        };

        if (endpoints.Count > 0) root["endpoints"] = endpoints;

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        });
    }

    private static JsonArray BuildRouteRules(AppState state)
    {
        var rules = new JsonArray();
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
            rules.Add(new JsonObject
            {
                ["action"] = "sniff",
                ["timeout"] = "1s"
            });
        }

        if (ProxyRoutingEnabled(state))
        {
            AddDomainRouteRule(rules, state.RoutingBypassSites, "direct");
        }

        return rules;
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

    private static JsonObject BuildDnsServer(string tag, string server)
    {
        var value = string.IsNullOrWhiteSpace(server) ? "1.1.1.1" : server.Trim();
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

        return new JsonObject
        {
            ["type"] = "udp",
            ["tag"] = tag,
            ["server"] = value,
            ["server_port"] = 53,
            ["detour"] = "proxy"
        };
    }

    private static JsonObject BuildBootstrapDnsServer()
    {
        return new JsonObject
        {
            ["type"] = "udp",
            ["tag"] = "bootstrap",
            ["server"] = "1.1.1.1",
            ["server_port"] = 53
        };
    }

    private static JsonArray BuildInbounds(AppState state)
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
            var tunMtu = state.Windows10TunCompatibilityMode
                ? Math.Clamp(state.TunMtu, 1280, 1500)
                : state.TunMtu;
            var strictRoute = state.Windows10TunCompatibilityMode
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
            if (state.Windows10TunCompatibilityMode)
            {
                inbounds[^1]!["stack"] = "gvisor";
                inbounds[^1]!["endpoint_independent_nat"] = true;
                inbounds[^1]!["udp_timeout"] = "5m";
            }
            else if (state.StackSystem) inbounds[^1]!["stack"] = "system";
        }

        return inbounds;
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

        if (!string.IsNullOrWhiteSpace(profile.Flow)) outbound["flow"] = profile.Flow;
        ApplyTransport(outbound, profile);
        if (profile.TlsEnabled || profile.RealityEnabled)
        {
            outbound["tls"] = BuildTls(state, profile);
        }

        return outbound;
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
            ["mtu"] = profile.Mtu ?? 1408,
            ["address"] = ToJsonArray(profile.LocalAddress),
            ["private_key"] = profile.PrivateKey,
            ["peers"] = new JsonArray
            {
                new JsonObject
                {
                    ["address"] = profile.Server,
                    ["port"] = profile.Port,
                    ["public_key"] = profile.PeerPublicKey,
                    ["pre_shared_key"] = profile.PreSharedKey,
                    ["allowed_ips"] = ToJsonArray(profile.AllowedIps),
                    ["persistent_keepalive_interval"] = profile.PersistentKeepalive ?? 25
                }
            }
        };
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
