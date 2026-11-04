using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace HappBox;

/// <summary>Legacy API kept for saved-state compatibility; AWG is recognized and preserved.</summary>
public static class RemovedProtocolPolicy
{
    public const string Message = "VMess, Shadowsocks и Trojan пока не поддерживаются. Эти конфигурации пропущены.";
    public static bool IsRemoved(ProxyProfile profile) => profile.Protocol is ProxyProtocol.Vmess or ProxyProtocol.Shadowsocks or ProxyProtocol.Trojan;
    public static bool IsType(string? type) => type?.ToLowerInvariant() is "awg" or "amneziawg";
    public static bool IsParameter(string name) => ProfileParser.AwgParameterNames.ContainsKey(name) ||
        ProfileParser.AwgParameterNames.Values.Contains(name, StringComparer.OrdinalIgnoreCase);
    public static bool IsJson(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return false;
        return node.EnumerateObject().Any(field => IsParameter(field.Name) ||
            (field.Name is "type" or "protocol" && field.Value.ValueKind == JsonValueKind.String && IsType(field.Value.GetString())) ||
            (field.Name == "settings" && IsJson(field.Value)));
    }
    public static bool IsYaml(YamlMappingNode node) => node.Children.Any(field =>
        field.Key is YamlScalarNode key && (IsParameter(key.Value ?? "") ||
        key.Value == "type" && field.Value is YamlScalarNode value && IsType(value.Value)));

    public static string SanitizeClash(string yaml) => yaml;
    public static int Migrate(AppState state) => 0;
}
