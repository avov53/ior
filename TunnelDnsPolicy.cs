namespace HappBox;
public static class TunnelDnsPolicy
{
    public static (string Source, string[] Servers) Effective(AppState state, ProxyProfile profile)
    {
        var fromProfile = profile.Protocol is ProxyProtocol.WireGuard or ProxyProtocol.AmneziaWG && !string.IsNullOrWhiteSpace(profile.TunnelDns);
        return (fromProfile ? "profile" : "application", (fromProfile ? profile.TunnelDns! : state.DnsServer).Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
