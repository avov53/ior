using System.Text.Json;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class NativeFiveProtocolTests
{

    private static string Key => Convert.ToBase64String(Enumerable.Repeat((byte)7,32).ToArray());



    [Theory]
    [InlineData(ProxyProtocol.Vmess,"vmess")]
    [InlineData(ProxyProtocol.Shadowsocks,"shadowsocks")]
    [InlineData(ProxyProtocol.Trojan,"trojan")]
    [InlineData(ProxyProtocol.WireGuard,"wireguard")]
    public void PrivateContractCarriesEveryNewTransport(ProxyProtocol protocol,string wireName)
    {
        var profile=new ProxyProfile{Protocol=protocol,Server="203.0.113.1",Port=443,ServerName="example.com",UserId="00112233-4455-6677-8899-aabbccddeeff",Password="test-only",Method="aes-128-gcm",Security="auto",TlsEnabled=protocol==ProxyProtocol.Trojan,Network="tcp",PrivateKey=Key,PeerPublicKey=Key,LocalAddress="10.0.0.2/32",AllowedIps="0.0.0.0/0",TunnelDns="9.9.9.9",PersistentKeepalive=25,Mtu=1380};
        foreach(var tun in new[]{false,true}) {
            Assert.True(CoreRuntimeContract.TryCreateStart(new AppState{TunEnabled=tun},profile,out var request,out var reason),reason);
            Assert.Equal(CoreRuntimeContract.ApiVersion,request!.ApiVersion);Assert.Equal(wireName,request.Profile.Protocol);
            var roundtrip=JsonSerializer.Deserialize<CoreStartRequest>(JsonSerializer.Serialize(request,CoreRuntimeContract.JsonOptions),CoreRuntimeContract.JsonOptions)!;
            Assert.Equal(wireName,roundtrip.Profile.Protocol);
            Assert.DoesNotContain(request.Profile.Credential,request.Profile.ToString());
            if(protocol is ProxyProtocol.WireGuard){Assert.Equal("9.9.9.9",request.Dns.Upstream);Assert.Equal(["0.0.0.0/0"],request.Profile.Options.AllowedIps!);Assert.Equal(25,request.Profile.Options.Keepalive);}
        }
    }

    [Fact]
    public void WireGuardBase64LinkPreservesCaseSensitiveKeys()
    {
        var config=$"[Interface]\nPrivateKey={Key}\nAddress=10.0.0.2/32\n[Peer]\nPublicKey={Key}\nEndpoint=example.com:443\nAllowedIPs=0.0.0.0/0";
        var encoded=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(config));
        var profile=ProfileParser.ParseManyDetailed("wireguard://"+encoded+"#Local").Profiles.Single();Assert.Equal(Key,profile.PrivateKey);Assert.Equal("Local",profile.Name);
    }
    [Fact]
    public void UnknownTunnelParameterBlocksBeforeNetwork()
    {
        var profile=ProfileParser.ParseManyDetailed($"[Interface]\nPrivateKey={Key}\nAddress=10.0.0.2/32\nFutureOption=secret-value\n[Peer]\nPublicKey={Key}\nEndpoint=example.com:443\nAllowedIPs=0.0.0.0/0").Profiles.Single();
        Assert.False(CoreRuntimeContract.TryCreateStart(new AppState(),profile,out var request,out var reason));Assert.Null(request);Assert.Contains("FutureOption",reason);Assert.DoesNotContain("secret-value",reason);
    }
    [Fact]
    public void JsonWireGuardAndClashAwgRetainTunnelParameters()
    {
        var json=JsonSerializer.Serialize(new{type="wireguard",server="example.com",server_port=443,private_key=Key,peer_public_key=Key,local_address=new[]{"10.0.0.2/32"},allowed_ips=new[]{"0.0.0.0/0"},dns=new[]{"9.9.9.9"},mtu=1380});
        var wg=JsonSubscriptionParser.Parse(json).Profiles.Single();Assert.Equal(ProxyProtocol.WireGuard,wg.Protocol);Assert.Equal("10.0.0.2/32",wg.LocalAddress);Assert.Equal(1380,wg.Mtu);
    }
    [Theory]
    [InlineData("bad-key")]
    [InlineData("c2hvcnQ=")]
    public void Shadowsocks2022InvalidKeyRejectedBeforeStart(string password)
    {var profile=new ProxyProfile{Protocol=ProxyProtocol.Shadowsocks,Server="example.com",Port=443,Method="2022-blake3-aes-128-gcm",Password=password};Assert.False(CoreRuntimeContract.TryCreateStart(new AppState(),profile,out _,out var reason));Assert.Contains("2022",reason);Assert.DoesNotContain(password,reason);}
    [Fact]
    public async Task OptInRealPrivateControlStartsAndStopsAllFiveTransports()
    {
        if(Environment.GetEnvironmentVariable("IOR_NATIVE_CONTROL_TEST")!="1")return;
        var binary=Environment.GetEnvironmentVariable("IOR_NATIVE_TEST_BINARY")!;
        var adapter=System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().First(item=>item.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up && item.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet or System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 && item.GetIPProperties().UnicastAddresses.Any(address=>address.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork));
        foreach(var protocol in new[]{ProxyProtocol.Vmess,ProxyProtocol.Shadowsocks,ProxyProtocol.Trojan,ProxyProtocol.WireGuard}) {
            var profile=new ProxyProfile{Protocol=protocol,Server="203.0.113.1",Port=443,ServerName="example.com",UserId="00112233-4455-6677-8899-aabbccddeeff",Password="test-only",Method="aes-128-gcm",Security="auto",TlsEnabled=protocol==ProxyProtocol.Trojan,Network="tcp",PrivateKey=Key,PeerPublicKey=Key,LocalAddress="10.0.0.2/32",AllowedIps="0.0.0.0/0",Mtu=1380};
            Assert.True(CoreRuntimeContract.TryCreateStart(new AppState(),profile,out var request,out var reason),reason);
            request!.Network=new CoreWireNetwork{PhysicalAddress=adapter.GetIPProperties().UnicastAddresses.First(item=>item.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork).Address.ToString(),InterfaceIndex=(uint)adapter.GetIPProperties().GetIPv4Properties()!.Index};
            await IorCoreControlClient.ValidateProfileAsync(binary,request);
            var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();var port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
            var client=await IorCoreControlClient.StartAsync(binary,port,request);var process=client.Process;Assert.True(client.Ready.Tcp);Assert.True(client.Ready.Udp);Assert.False(client.Ready.Tun);
            var status=await client.StatusAsync();Assert.True(status.Udp);await client.DisposeAsync();
        }
    }
}
