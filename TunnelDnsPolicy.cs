namespace HappBox;
public static class TunnelDnsPolicy
{
    public static (string Source, string[] Servers) Effective(AppState state, ProxyProfile profile)
    {
        var fromProfile = profile.Protocol == ProxyProtocol.WireGuard && !string.IsNullOrWhiteSpace(profile.TunnelDns);
        return (fromProfile ? "profile" : "application", (fromProfile ? profile.TunnelDns! : state.DnsServer).Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
