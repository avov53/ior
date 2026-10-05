using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace HappBox;

/// <summary>Finds the physical egress interface before a TUN route can change the routing table.</summary>
public static class PhysicalInterfaceResolver
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestInterface(uint destination, out uint interfaceIndex);
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestInterfaceEx(byte[] destination, out uint interfaceIndex);

    public static string ForAddress(IPAddress destination)
    {
        if(destination.AddressFamily==AddressFamily.InterNetwork)return ForIpv4(destination);
        if(!OperatingSystem.IsWindows() || destination.AddressFamily!=AddressFamily.InterNetworkV6)
            throw new InvalidOperationException("PHYSICAL_ADDRESS_FAMILY_UNSUPPORTED");
        var socketAddress=new byte[28];socketAddress[0]=23;
        destination.GetAddressBytes().CopyTo(socketAddress,8);
        BitConverter.GetBytes((uint)destination.ScopeId).CopyTo(socketAddress,24);
        var result=GetBestInterfaceEx(socketAddress,out var index);
        if(result!=0)throw new InvalidOperationException("PHYSICAL_IPV6_ROUTE_UNAVAILABLE: "+result);
        var network=NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item=>
            item.OperationalStatus==OperationalStatus.Up && item.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
            item.GetIPProperties().GetIPv6Properties()?.Index==index && !IsIorInterface(item.Name));
        return network?.Name ?? throw new InvalidOperationException("PHYSICAL_IPV6_INTERFACE_UNAVAILABLE");
    }

    public static CoreWireNetwork Describe(string name)
    {
        var properties=NetworkInterface.GetAllNetworkInterfaces().Single(item=>item.Name==name && item.OperationalStatus==OperationalStatus.Up).GetIPProperties();
        var ip4=properties.UnicastAddresses.Select(item=>item.Address).FirstOrDefault(ip=>ip.AddressFamily==AddressFamily.InterNetwork && !ip.ToString().StartsWith("169.254.") && !IPAddress.IsLoopback(ip));
        var ip6=properties.UnicastAddresses.Select(item=>item.Address).FirstOrDefault(ip=>ip.AddressFamily==AddressFamily.InterNetworkV6 && !ip.IsIPv6LinkLocal && !ip.IsIPv6Multicast && !IPAddress.IsLoopback(ip));
        return new CoreWireNetwork {PhysicalAddress=ip4?.ToString()??"",InterfaceIndex=ip4 is null?0:(uint)properties.GetIPv4Properties()!.Index,
            PhysicalAddress6=ip6?.ToString(),InterfaceIndex6=ip6 is null?null:(uint)properties.GetIPv6Properties()!.Index};
    }

    // Readiness must allow the exact owned TUN; physical egress selection must not.
    public static bool UsesInterfaceForAddress(IPAddress destination, string interfaceName)
    {
        if(destination.AddressFamily==AddressFamily.InterNetwork)
            return UsesInterfaceForIpv4(destination,interfaceName);
        if(!OperatingSystem.IsWindows() || destination.AddressFamily!=AddressFamily.InterNetworkV6)return false;
        var socketAddress=new byte[28];socketAddress[0]=23;
        destination.GetAddressBytes().CopyTo(socketAddress,8);
        BitConverter.GetBytes((uint)destination.ScopeId).CopyTo(socketAddress,24);
        if(GetBestInterfaceEx(socketAddress,out var index)!=0)return false;
        return NetworkInterface.GetAllNetworkInterfaces().Any(item=>
            item.OperationalStatus==OperationalStatus.Up &&
            item.Name.Equals(interfaceName,StringComparison.OrdinalIgnoreCase) &&
            item.GetIPProperties().GetIPv6Properties()?.Index==index);
    }

    public static bool UsesInterfaceForIpv4(IPAddress destination, string interfaceName)
    {
        if (!OperatingSystem.IsWindows() || destination.AddressFamily != AddressFamily.InterNetwork)
            return false;
        if (GetBestInterface(BitConverter.ToUInt32(destination.GetAddressBytes()), out var index) != 0)
            return false;
        return NetworkInterface.GetAllNetworkInterfaces().Any(item =>
            item.OperationalStatus == OperationalStatus.Up && item.Name == interfaceName &&
            item.GetIPProperties().GetIPv4Properties()?.Index == index);
    }

    public static bool IsIorTunBestRouteForIpv4(IPAddress destination)
    {
        if (!OperatingSystem.IsWindows() || destination.AddressFamily != AddressFamily.InterNetwork)
            return false;
        if (GetBestInterface(BitConverter.ToUInt32(destination.GetAddressBytes()), out var index) != 0)
            return false;
        return NetworkInterface.GetAllNetworkInterfaces().Any(item =>
            item.GetIPProperties().GetIPv4Properties()?.Index == index &&
            IsIorInterface(item.Name));
    }

    public static string ForIpv4(IPAddress destination)
    {
        if (!OperatingSystem.IsWindows() || destination.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("Определение выходного интерфейса TUN доступно только для IPv4 на Windows.");

        var result = GetBestInterface(BitConverter.ToUInt32(destination.GetAddressBytes()), out var index);
        if (result != 0)
            throw new InvalidOperationException($"Windows не смогла определить сетевой интерфейс сервера (код {result}).");

        var network = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item =>
            item.OperationalStatus == OperationalStatus.Up &&
            item.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
            item.GetIPProperties().GetIPv4Properties()?.Index == index);
        if (network is null || string.IsNullOrWhiteSpace(network.Name) ||
            IsIorInterface(network.Name))
            throw new InvalidOperationException("Выходной интерфейс сервера не найден или ведёт через TUN. Подключение остановлено во избежание сетевой петли.");

        return network.Name;
    }

    public static bool IsIorInterface(string name) => name.Equals("ior", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("ior-", StringComparison.OrdinalIgnoreCase);
}
