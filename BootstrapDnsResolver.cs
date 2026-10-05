using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HappBox;

public static class BootstrapDnsResolver
{
    public static async Task<IPAddress> ResolveEndpointAsync(string host,string? configured,CancellationToken token=default)
    {
        if(IPAddress.TryParse(host,out var literal))return literal;
        using var race=CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task<IPAddress> Resolve(ushort kind,int delay)
        {
            if(delay>0)await Task.Delay(delay,race.Token);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(race.Token);timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var server=ServerAddress(configured);using var client=new UdpClient(server.AddressFamily);client.Connect(server,53);
            var query=BuildQuery(host);query[^3]=(byte)kind;
            await client.SendAsync(query,timeout.Token);
            var response=await client.ReceiveAsync(timeout.Token);
            var result=ReadFirstAddress(response.Buffer,query,kind);
            _=PhysicalInterfaceResolver.ForAddress(result);return result;
        }
        // The endpoint's family is independent of the tunnel's inner addresses.
        var a=Resolve(1,0);var aaaa=Resolve(28,250);
        var first=await Task.WhenAny(a,aaaa);var second=ReferenceEquals(first,a)?aaaa:a;
        try {
            try{return await first;}catch(Exception error) when(!token.IsCancellationRequested && error is SocketException or OperationCanceledException or InvalidDataException or InvalidOperationException){return await second;}
        } finally {race.Cancel();try{await Task.WhenAll(a,aaaa);}catch(Exception error) when(error is SocketException or OperationCanceledException or InvalidDataException or InvalidOperationException){} }
    }
    public static IPAddress ServerAddress(string? configured) =>
        IPAddress.TryParse(configured, out var address)
            ? address : IPAddress.Parse("1.1.1.1");

    public static async Task<IPAddress> ResolveIpv4Async(string host, string? configured, CancellationToken cancellationToken = default,
        Action<Socket>? bindPhysical = null)
    {
        if (IPAddress.TryParse(host, out var address)) return address;
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Адрес сервера не указан.", nameof(host));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var token = timeout.Token;
        try
        {
            using var client = new UdpClient(ServerAddress(configured).AddressFamily);
            bindPhysical?.Invoke(client.Client);
            client.Connect(ServerAddress(configured), 53);
            var query = BuildQuery(host);
            await client.SendAsync(query, token);
            var response = await client.ReceiveAsync(token);
            return ReadFirstIpv4(response.Buffer, query);
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException or InvalidDataException)
        {
            throw new InvalidOperationException($"Не удалось получить IPv4-адрес сервера {host} через bootstrap DNS {ServerAddress(configured)}: {error.Message}", error);
        }
    }

    internal static byte[] BuildQuery(string host)
    {
        var labels = new List<byte>();
        foreach (var part in host.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(part);
            if (bytes.Length is 0 or > 63 || bytes.Any(value => value > 127)) throw new ArgumentException("Некорректное доменное имя сервера.", nameof(host));
            labels.Add((byte)bytes.Length);
            labels.AddRange(bytes);
        }
        if (labels.Count > 252) throw new ArgumentException("Доменное имя сервера слишком длинное.", nameof(host));
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = new List<byte> { (byte)(id >> 8), (byte)id, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        query.AddRange(labels);
        query.AddRange([0, 0, 1, 0, 1]);
        return [.. query];
    }

    internal static IPAddress ReadFirstIpv4(byte[] response, byte[] query)
        =>ReadFirstAddress(response,query,1);

    internal static IPAddress ReadFirstAddress(byte[] response,byte[] query,ushort kind)
    {
        if (response.Length < 12 || response[0] != query[0] || response[1] != query[1] || (response[2] & 0x80) == 0 || (response[2]&2)!=0 || (response[3] & 0x0f) != 0)
            throw new InvalidDataException("Некорректный ответ DNS.");
        var questions = ReadUInt16(response, 4);
        var answers = ReadUInt16(response, 6);
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            SkipName(response, ref offset);
            offset += 4;
            if (offset > response.Length) throw new InvalidDataException("Ответ DNS обрезан.");
        }
        for (var i = 0; i < answers; i++)
        {
            SkipName(response, ref offset);
            if (offset + 10 > response.Length) throw new InvalidDataException("Ответ DNS обрезан.");
            var type = ReadUInt16(response, offset);
            var dataLength = ReadUInt16(response, offset + 8);
            offset += 10;
            if (offset + dataLength > response.Length) throw new InvalidDataException("Ответ DNS обрезан.");
            if (type == kind && ReadUInt16(response,offset-8)==1 && dataLength == (kind==28?16:4)) return new IPAddress(response.AsSpan(offset, dataLength));
            offset += dataLength;
        }
        throw new InvalidDataException("DNS не вернул IPv4-адрес сервера.");
    }

    private static int ReadUInt16(byte[] data, int offset) => (data[offset] << 8) | data[offset + 1];

    private static void SkipName(byte[] data, ref int offset)
    {
        for (var labels = 0; labels < 128; labels++)
        {
            if (offset >= data.Length) throw new InvalidDataException("Ответ DNS обрезан.");
            var length = data[offset++];
            if (length == 0) return;
            if ((length & 0xc0) == 0xc0)
            {
                if (offset >= data.Length) throw new InvalidDataException("Ответ DNS обрезан.");
                offset++;
                return;
            }
            if ((length & 0xc0) != 0 || offset + length > data.Length) throw new InvalidDataException("Некорректное имя в ответе DNS.");
            offset += length;
        }
        throw new InvalidDataException("Слишком длинное имя в ответе DNS.");
    }
}
