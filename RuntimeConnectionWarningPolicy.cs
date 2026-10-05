namespace HappBox;

public static class RuntimeConnectionWarningPolicy
{
    public static bool IsRealityFailure(long messageGeneration, long currentGeneration, CoreKind? core,
        ProxyProfile? profile, string line) => messageGeneration == currentGeneration && core == CoreKind.SingBox &&
        profile is { Protocol: ProxyProtocol.Vless, RealityEnabled: true } &&
        line.Contains("reality verification failed", StringComparison.OrdinalIgnoreCase);
}
