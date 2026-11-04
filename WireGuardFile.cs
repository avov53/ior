using System.Text;

namespace HappBox;

public static class WireGuardFile
{
    public static string Export(ProxyProfile profile)
    {
        if (profile.Protocol is not (ProxyProtocol.WireGuard or ProxyProtocol.AmneziaWG))
            throw new InvalidOperationException("Экспорт .conf доступен только для WireGuard и AmneziaWG.");
        if (profile.UnsupportedParameters.Count > 0) throw new InvalidOperationException("Неизвестные параметры нельзя экспортировать без потери данных.");
        if (string.IsNullOrWhiteSpace(profile.PrivateKey) || string.IsNullOrWhiteSpace(profile.PeerPublicKey) || string.IsNullOrWhiteSpace(profile.Server) || profile.Port is < 1 or > 65535)
            throw new InvalidOperationException("Заполните ключи, адрес сервера и порт перед экспортом.");
        var text = new StringBuilder();
        void Field(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (value.Contains('\r') || value.Contains('\n')) throw new FormatException("Параметр конфигурации содержит перенос строки.");
            text.AppendLine(key + " = " + value);
        }
        text.AppendLine("# " + profile.Name.Replace('\r', ' ').Replace('\n', ' '));
        text.AppendLine("[Interface]");
        Field("PrivateKey", profile.PrivateKey); Field("Address", profile.LocalAddress);
        Field("DNS", profile.TunnelDns); Field("MTU", profile.Mtu?.ToString());
        foreach (var parameter in AwgProfileParameters.Normalize(profile.AwgParameters))
        {
            var name = ProfileParser.AwgParameterNames.First(item => item.Value == parameter.Key).Key;
            Field(name, parameter.Value);
        }
        text.AppendLine();text.AppendLine("[Peer]");
        Field("PublicKey", profile.PeerPublicKey);Field("PresharedKey", profile.PreSharedKey);
        var host = profile.Server.Contains(':') ? "[" + profile.Server.Trim('[', ']') + "]" : profile.Server;
        Field("Endpoint", host + ":" + profile.Port);Field("AllowedIPs", profile.AllowedIps);
        Field("PersistentKeepalive", profile.PersistentKeepalive?.ToString());
        return text.ToString();
    }
}
