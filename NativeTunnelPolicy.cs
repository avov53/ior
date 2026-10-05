namespace HappBox;

public static class NativeTunnelPolicy
{
    public static int EffectiveMtu(AppState state, ProxyProfile profile)
    {
        var mtu=Math.Clamp(state.TunMtu,1280,1500);
        return profile.Protocol is ProxyProtocol.WireGuard
            ? Math.Min(mtu,profile.Mtu ?? 1380) : mtu;
    }
}
