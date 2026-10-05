using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HappBox;

public static class XrayTunHealthProbe
{
    internal static bool HasProxyEvidence(string host,(string Source,string Destination)? connection,
        Func<string,bool>? streamEvidence,Func<string,string,bool>? packetEvidence)=>
        streamEvidence?.Invoke(host)==true || connection is { } endpoints&&packetEvidence?.Invoke(endpoints.Source,endpoints.Destination)==true;
    public static async Task<(bool Ready, string Reason)> CheckAsync(IPAddress serverEndpoint,
        string physicalInterface, CancellationToken cancellationToken = default, string adapterName = "ior-xray",
        IPAddress? dnsEndpoint = null, string coreLabel = "Xray", bool ipv6Only = false,
        Action<string,int,long,string>? measured=null, Func<string,bool>? proxyTrafficConfirmed=null, bool hedged=true,
        bool fastStart=false, Action<NativeTunReadinessResult>? readiness=null,string? optionalRouteError=null,
        Func<string,bool>? proxyDnsConfirmed=null,Func<string,string,bool>? proxyPacketTrafficConfirmed=null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                if (NetworkInterface.GetAllNetworkInterfaces().Any(item =>
                    item.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase) &&
                    item.OperationalStatus == OperationalStatus.Up)) break;
                await Task.Delay(100, timeout.Token);
            }
            if (!NetworkInterface.GetAllNetworkInterfaces().Any(item =>
                item.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase) &&
                item.OperationalStatus == OperationalStatus.Up))
                return (false, $"{coreLabel} не создал активный TUN-адаптер.");

            // The TUN adapter can be up while the server's own route has already
            // moved into it. Check the actual Windows decision after TUN starts.
            var egress = PhysicalInterfaceResolver.ForAddress(serverEndpoint);
            if (!string.Equals(egress, physicalInterface, StringComparison.OrdinalIgnoreCase))
                return (false, $"Адрес сервера после запуска TUN направлен через {egress}, а не {physicalInterface}.");

            using var handler = new SocketsHttpHandler { UseProxy = false };
            var probeConnections=new System.Collections.Concurrent.ConcurrentDictionary<string,(string Source,string Destination)>(StringComparer.OrdinalIgnoreCase);
            // Keep normal TLS certificate/hostname verification in HttpClient.
            // Only expose the DNS and TCP portions of the readiness connection.
            handler.ConnectCallback=async (context,token)=>
            {
                var attempt=context.DnsEndPoint.Host.Equals("www.cloudflare.com",StringComparison.OrdinalIgnoreCase)?0:1;
                var phase="https_dns";
                var watch=System.Diagnostics.Stopwatch.StartNew();
                Socket? socket=null;
                try
                {
                    var addresses=await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host,token);
                    measured?.Invoke(phase,attempt,watch.ElapsedMilliseconds,"OK");
                    phase="https_tcp";watch.Restart();
                    socket=new Socket(SocketType.Stream,ProtocolType.Tcp){NoDelay=true};
                    await socket.ConnectAsync(addresses,context.DnsEndPoint.Port,token);
                    if(socket.LocalEndPoint is IPEndPoint local && socket.RemoteEndPoint is IPEndPoint remote)
                        probeConnections[context.DnsEndPoint.Host]=(NativeProxyTrafficEvidence.Endpoint(local),NativeProxyTrafficEvidence.Endpoint(remote));
                    measured?.Invoke(phase,attempt,watch.ElapsedMilliseconds,"OK");
                    return new NetworkStream(socket,ownsSocket:true);
                }
                catch(Exception error)
                {
                    socket?.Dispose();
                    measured?.Invoke(phase,attempt,watch.ElapsedMilliseconds,error.GetType().Name);
                    throw;
                }
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            async Task CheckHttpsAsync(int attempt, CancellationToken token)
            {
                var watch=System.Diagnostics.Stopwatch.StartNew();
                var url = attempt == 0 ? "https://www.cloudflare.com/cdn-cgi/trace" : hedged ? "https://www.microsoft.com/" : "https://example.com/";
                try {
                using var response = await client.GetAsync(url,
                    HttpCompletionOption.ResponseHeadersRead, token);
                measured?.Invoke("https_response_headers",attempt,watch.ElapsedMilliseconds,"HTTP_"+(int)response.StatusCode);
                response.EnsureSuccessStatusCode();
                using var body = await response.Content.ReadAsStreamAsync(token);
                var bodyWatch=System.Diagnostics.Stopwatch.StartNew();
                if (await body.ReadAsync(new byte[1], token) == 0)
                    throw new IOException("TUN_HTTPS_EMPTY_RESPONSE");
                measured?.Invoke("https_body_first_byte",attempt,bodyWatch.ElapsedMilliseconds,"OK");
                if(proxyTrafficConfirmed is not null || proxyPacketTrafficConfirmed is not null) {
                    var host=new Uri(url).Host;
                    bool Confirmed()=>HasProxyEvidence(host,probeConnections.TryGetValue(host,out var connection)?connection:null,
                        proxyTrafficConfirmed,proxyPacketTrafficConfirmed);
                    for(var wait=0;wait<10&&!Confirmed();wait++)await Task.Delay(10,token);
                    if(!Confirmed())throw new IOException("TUN_HTTPS_PROXY_PATH_UNCONFIRMED");
                }
                measured?.Invoke("https_first_byte",attempt,watch.ElapsedMilliseconds,"OK");
                } catch(Exception error){measured?.Invoke("https_first_byte",attempt,watch.ElapsedMilliseconds,error.GetType().Name);throw;}
            }
            async Task CheckUdpAsync(int attempt, CancellationToken token)
            {
                var watch=System.Diagnostics.Stopwatch.StartNew();
                try {
                if(optionalRouteError is not null)throw new IOException(optionalRouteError);
                var resolver = dnsEndpoint ?? IPAddress.Parse("8.8.8.8");
                using var udp = new UdpClient(resolver.AddressFamily);
                udp.Connect(resolver, 53);
                var query = BootstrapDnsResolver.BuildQuery("cloudflare.com");
                if(ipv6Only)query[^3]=28;
                await udp.SendAsync(query, token);
                var answer = await udp.ReceiveAsync(token);
                if (answer.Buffer.Length < 12 || answer.Buffer[0] != query[0] || answer.Buffer[1] != query[1] ||
                (answer.Buffer[2] & 0x80) == 0 || (answer.Buffer[3] & 15) != 0 || (answer.Buffer[6] == 0 && answer.Buffer[7] == 0))
                    throw new IOException($"UDP через {coreLabel} TUN не вернул корректный DNS-ответ.");
                await ConfirmDnsPathAsync(token);
                measured?.Invoke("udp_dns",attempt,watch.ElapsedMilliseconds,"OK");
                } catch(Exception error){measured?.Invoke("udp_dns",attempt,watch.ElapsedMilliseconds,error.GetType().Name);throw;}
            }
            async Task CheckDnsAsync(CancellationToken token)
            {
                if(optionalRouteError is not null)throw new IOException(optionalRouteError);
                var resolver=dnsEndpoint??IPAddress.Parse("8.8.8.8");
                using var tcp=new TcpClient(resolver.AddressFamily);
                await tcp.ConnectAsync(resolver,53,token);
                using var stream=tcp.GetStream();
                var query=BootstrapDnsResolver.BuildQuery("cloudflare.com");
                if(ipv6Only)query[^3]=28;
                byte[] length=[0,(byte)query.Length];
                await stream.WriteAsync(length,token);await stream.WriteAsync(query,token);
                await stream.ReadExactlyAsync(length,token);
                var size=(length[0]<<8)|length[1];
                if(size<12 || size>4096)throw new IOException("TUN_DNS_RESPONSE_INVALID");
                var answer=new byte[size];await stream.ReadExactlyAsync(answer,token);
                if(answer[0]!=query[0] || answer[1]!=query[1] || (answer[2]&0x80)==0 || (answer[3]&15)!=0 || (answer[6]==0&&answer[7]==0))
                    throw new IOException("TUN_DNS_RESPONSE_INVALID");
                await ConfirmDnsPathAsync(token);
            }
            async Task ConfirmDnsPathAsync(CancellationToken token)
            {
                if(proxyDnsConfirmed is null)return;
                for(var wait=0;wait<10&&!proxyDnsConfirmed("cloudflare.com");wait++)await Task.Delay(10,token);
                if(!proxyDnsConfirmed("cloudflare.com"))throw new IOException("NATIVE_TUN_DNS_PROXY_PATH_UNCONFIRMED");
            }
            if(fastStart)
            {
                var result=await NativeTunReadiness.CheckAsync(
                    token=>CheckTrafficStageAsync("HTTPS",CheckHttpsAsync,token, hedgeDelay:TimeSpan.FromMilliseconds(150)),
                    CheckDnsAsync,token=>CheckUdpAsync(0,token),timeout.Token);
                readiness?.Invoke(result);
                foreach(var item in new[]{("dns",result.Dns),("udp",result.Udp)})
                    measured?.Invoke(item.Item1,0,item.Item2.DurationMs,item.Item2.Code);
                if(!result.Ready)return(false,"NATIVE_TUN_HTTPS_FAILED: "+result.Https.Code);
            }
            else await Task.WhenAll(
                hedged?CheckTrafficStageAsync("HTTPS", CheckHttpsAsync, timeout.Token):CheckTrafficSequentialAsync("HTTPS",CheckHttpsAsync,timeout.Token),
                hedged?CheckTrafficStageAsync("UDP_DNS", CheckUdpAsync, timeout.Token):CheckTrafficSequentialAsync("UDP_DNS",CheckUdpAsync,timeout.Token));
            egress = PhysicalInterfaceResolver.ForAddress(serverEndpoint);
            if (!string.Equals(egress, physicalInterface, StringComparison.OrdinalIgnoreCase))
                return (false, $"Маршрут к серверу изменился во время проверки {coreLabel} TUN.");
            return (true, "");
        }
        catch (Exception error) when (error is SocketException or HttpRequestException or OperationCanceledException or IOException)
        {
            return (false, $"Проверка HTTPS/UDP через {coreLabel} TUN не прошла: {error.Message}");
        }
        catch (InvalidOperationException error)
        {
            return (false, $"Проверка физического выхода {coreLabel} TUN не прошла: {error.Message}");
        }
    }

    public static async Task CheckTrafficStageAsync(string stage, Func<int, CancellationToken, Task> probe,
        CancellationToken token, TimeSpan? attemptTimeout = null, TimeSpan? hedgeDelay = null)
    {
        token.ThrowIfCancellationRequested();
        using var race=CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task<Exception?> Attempt(int attempt)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(race.Token);
            deadline.CancelAfter(attemptTimeout ?? TimeSpan.FromSeconds(10));
            try { await probe(attempt, deadline.Token); return null; }
            catch (Exception error) when (
                error is SocketException or HttpRequestException or OperationCanceledException or IOException)
            {return error;}
        }
        var first=Attempt(0);
        Task<Exception?>? second=null;
        try {
        await Task.WhenAny(first,Task.Delay(hedgeDelay ?? TimeSpan.FromMilliseconds(250),race.Token));
        token.ThrowIfCancellationRequested();
        if(first.IsCompleted && await first is null){race.Cancel();return;}
        second=Attempt(1);
        var winner=await Task.WhenAny(first,second);
        token.ThrowIfCancellationRequested();
        if(await winner is null){race.Cancel();await (ReferenceEquals(winner,first)?second:first);return;}
        var other=ReferenceEquals(winner,first)?second:first;
        var failure=await other;
        token.ThrowIfCancellationRequested();
        if(failure is null){race.Cancel();return;}
        throw new IOException($"TUN_PROBE_{stage}_FAILED: обе попытки не прошли ({failure.GetType().Name}).",failure);
        } finally {race.Cancel();await Task.WhenAll(second is null ? [first] : [first,second]);}
    }
    // Retained only for opt-in before/after qualification of the old startup path.
    public static async Task CheckTrafficSequentialAsync(string stage,Func<int,CancellationToken,Task> probe,CancellationToken token)
    {
        for(var attempt=0;attempt<2;attempt++) {
            token.ThrowIfCancellationRequested();using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try {await probe(attempt,deadline.Token);return;}
            catch(Exception error) when(!token.IsCancellationRequested && error is SocketException or HttpRequestException or OperationCanceledException or IOException) {
                if(attempt==1)throw new IOException($"TUN_PROBE_{stage}_FAILED",error);
            }
        }
    }
}
