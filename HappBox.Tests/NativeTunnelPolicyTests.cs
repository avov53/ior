using HappBox;
using Xunit;
namespace HappBox.Tests;
public sealed class NativeTunnelPolicyTests
{
    [Fact]public void TunnelMtuDoesNotExceedConfiguredAwgMtu(){
        var state=new AppState{TunMtu=1500};var profile=new ProxyProfile{Protocol=ProxyProtocol.WireGuard,Mtu=1280};
        Assert.Equal(1280,NativeTunnelPolicy.EffectiveMtu(state,profile));profile.Mtu=1400;Assert.Equal(1400,NativeTunnelPolicy.EffectiveMtu(state,profile));state.TunMtu=1300;Assert.Equal(1300,NativeTunnelPolicy.EffectiveMtu(state,profile));
    }
    [Fact]public void ExpectedUnavailableFamilyIsCountedWithoutHidingOtherFailures(){
        var second=100L;var limiter=new ProcessLogLimiter(()=>second);
        for(var i=0;i<3;i++)Assert.True(limiter.TryAccept("ior-core: TUN_UDP_FAILED · [2001:db8::1]:443 · reason=TUNNEL_IPV6_LOCAL_ADDRESS_REQUIRED",out _));
        for(var i=0;i<10;i++)Assert.False(limiter.TryAccept("ior-core: TUN_UDP_FAILED · reason=TUNNEL_IPV6_LOCAL_ADDRESS_REQUIRED",out _));
        Assert.True(limiter.TryAccept("ior-core: TUN_UDP_FAILED · reason=OTHER_FAILURE",out _));second++;
        Assert.True(limiter.TryAccept("ior-core: WARNING",out var summary));Assert.Contains("suppressed=10",summary);Assert.Contains("2001:db8::1",summary);
    }
    [Fact]public void Ipv6OnlyTunnelRequiresCompatibleDnsBeforeNetworkChanges(){
        var key=Convert.ToBase64String(new byte[32]);var profile=new ProxyProfile{Protocol=ProxyProtocol.WireGuard,Server="2001:db8::1",Port=12345,PrivateKey=key,PeerPublicKey=key,LocalAddress="fd00::2/128",AllowedIps="::/0",TunnelDns="fd00::1"};
        Assert.True(CoreRuntimeContract.TryCreateStart(new AppState(),profile,out var request,out var reason),reason);
        Assert.Equal(new[]{"fd00::1"},request!.Dns.Upstreams);Assert.False(request.Dns.Ipv4Only);
        profile.TunnelDns="8.8.8.8";Assert.False(CoreRuntimeContract.TryCreateStart(new AppState(),profile,out _,out reason));Assert.Contains("DNS_UPSTREAM_OUTSIDE_TUNNEL",reason);
    }
}
