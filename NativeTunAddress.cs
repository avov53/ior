using System.Net;
using System.Net.Sockets;

namespace HappBox;

public sealed record NativeTunAddress(IPAddress Adapter, int PrefixLength, IPAddress Dns)
{
    public static NativeTunAddress Parse(string address)
    {
        var parts = address.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var adapter) ||
            adapter.AddressFamily != AddressFamily.InterNetwork || !int.TryParse(parts[1], out var prefix) || prefix is < 1 or > 30)
            throw new InvalidOperationException("TUN_ADDRESS_INVALID: нужен IPv4-адрес адаптера с префиксом от /1 до /30.");
        var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(adapter.GetAddressBytes());
        var mask = uint.MaxValue << (32 - prefix);
        var first = value & mask;
        var last = first | ~mask;
        if (value <= first || value >= last || IPAddress.IsLoopback(adapter) || adapter.GetAddressBytes()[0] is 0 or >= 224)
            throw new InvalidOperationException("TUN_ADDRESS_INVALID: адрес адаптера не должен быть адресом сети, broadcast или loopback.");
        var dns = value == first + 1 ? first + 2 : first + 1;
        Span<byte> bytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, dns);
        return new NativeTunAddress(adapter, prefix, new IPAddress(bytes));
    }
}
