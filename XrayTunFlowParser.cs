using System.Net;

namespace HappBox;

public static class XrayTunFlowParser
{
    public static bool TryParseTcp(string line, out IPEndPoint? source, out IPEndPoint? destination)
    {
        source = destination = null;
        const string marker = "proxy/tun: processing from tcp:";
        var offset = line.IndexOf(marker, StringComparison.Ordinal);
        if (offset < 0) return false;
        var endpoints = line[(offset + marker.Length)..].Split(" to tcp:", StringSplitOptions.None);
        return endpoints.Length == 2 && IPEndPoint.TryParse(endpoints[0].Trim(), out source) &&
            IPEndPoint.TryParse(endpoints[1].Trim(), out destination);
    }
}
