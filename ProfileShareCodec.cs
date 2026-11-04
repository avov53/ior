using System.Text;
using System.Text.Json;

namespace HappBox;

public static class ProfileShareCodec
{
    private const string Prefix = "ior-profile://";
    public static string Export(ProxyProfile profile)
    {
        var copy = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(profile))!.AsObject();
        var defaults = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new ProxyProfile()))!.AsObject();
        foreach (var key in copy.Select(item => item.Key).ToArray())
            if (key is "Id" or "SubscriptionId" || System.Text.Json.Nodes.JsonNode.DeepEquals(copy[key], defaults[key])) copy.Remove(key);
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(copy.ToJsonString()));
    }
    public static ProxyProfile Import(string link)
    {
        if (!link.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || link.Length > 1_000_000)
            throw new FormatException("Некорректная ссылка профиля ior.");
        var profile = JsonSerializer.Deserialize<ProxyProfile>(Encoding.UTF8.GetString(Convert.FromBase64String(link[Prefix.Length..])))
            ?? throw new FormatException("Пустой профиль ior.");
        if (RemovedProtocolPolicy.IsRemoved(profile) || !Enum.IsDefined(profile.Protocol) ||
            string.IsNullOrWhiteSpace(profile.Server) || profile.Port is < 1 or > 65535)
            throw new FormatException("Неподдерживаемый или некорректный профиль ior.");
        profile.Id = Guid.NewGuid().ToString("N");
        profile.SubscriptionId = null;
        return profile;
    }
}
