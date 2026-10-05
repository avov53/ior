using HappBox;
using Xunit;

namespace HappBox.Tests;
public sealed class RuntimeConnectionWarningTests
{
    [Fact]
    public void OldProcessOrAnotherProtocolCannotSetRealityWarning()
    {
        var reality = new ProxyProfile {Protocol=ProxyProtocol.Vless, RealityEnabled=true};
        const string message = "reality verification failed";
        Assert.True(RuntimeConnectionWarningPolicy.IsRealityFailure(2,2,CoreKind.SingBox,reality,message));
        Assert.False(RuntimeConnectionWarningPolicy.IsRealityFailure(1,2,CoreKind.SingBox,reality,message));
        Assert.False(RuntimeConnectionWarningPolicy.IsRealityFailure(2,2,CoreKind.IorCore,reality,message));
        Assert.False(RuntimeConnectionWarningPolicy.IsRealityFailure(2,2,CoreKind.SingBox,new ProxyProfile {Protocol=ProxyProtocol.Hysteria2},message));
        Assert.False(RuntimeConnectionWarningPolicy.IsRealityFailure(2,2,CoreKind.SingBox,reality,"connected"));
    }
}
