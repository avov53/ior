using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace HappBox;

/// <summary>Recognizes removed formats solely to reject imports and migrate old state.</summary>
public static class RemovedProtocolPolicy
{
    public const string Message = "AmneziaWG больше не поддерживается. Серверы этого протокола пропущены.";
    public static bool IsRemoved(ProxyProfile profile) => (int)profile.Protocol == 8;
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

    public static string SanitizeClash(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root) return yaml;
        YamlNode? Get(YamlMappingNode node, string key) => node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
        if (Get(root, "proxies") is not YamlSequenceNode proxies) return yaml;
        var rejected = proxies.Children.OfType<YamlMappingNode>().Where(IsYaml).ToArray();
        if (rejected.Length == 0) return yaml;
        var names = rejected.Select(node => (Get(node, "name") as YamlScalarNode)?.Value).Where(name => name != null).ToHashSet(StringComparer.Ordinal);
        foreach (var node in rejected) proxies.Children.Remove(node);
        if (Get(root, "proxy-groups") is YamlSequenceNode groups)
            foreach (var group in groups.Children.OfType<YamlMappingNode>())
                if (Get(group, "proxies") is YamlSequenceNode members)
                {
                    foreach (var member in members.Children.OfType<YamlScalarNode>().Where(member => names.Contains(member.Value)).ToArray()) members.Children.Remove(member);
                    if (members.Children.Count == 0) members.Add(new YamlScalarNode("REJECT"));
                }
        if (Get(root, "rules") is YamlSequenceNode rules)
            foreach (var rule in rules.Children.OfType<YamlScalarNode>())
            {
                var parts = (rule.Value ?? "").Split(',');
                var target = parts.Length - 1;
                if (target > 0 && parts[target].Trim().Equals("no-resolve", StringComparison.OrdinalIgnoreCase)) target--;
                if (target > 0 && names.Contains(parts[target].Trim())) { parts[target] = "REJECT"; rule.Value = string.Join(',', parts); }
            }
        using var writer = new StringWriter();
        stream.Save(writer, false);
        return writer.ToString();
    }

    public static int Migrate(AppState state)
    {
        var removed = state.Profiles.Where(IsRemoved).Select(profile => profile.Id).ToHashSet();
        state.Profiles.RemoveAll(IsRemoved);
        if (state.SelectedProfileId is { } selected && removed.Contains(selected))
            state.SelectedProfileId = state.Profiles.FirstOrDefault()?.Id;
        foreach (var workspace in state.ConnectionProfiles)
        {
            workspace.ConfigIds.RemoveAll(removed.Contains);
            if (workspace.DefaultServerId is { } id && removed.Contains(id))
                workspace.DefaultServerId = state.Profiles.FirstOrDefault(profile => workspace.ConfigIds.Contains(profile.Id) ||
                    workspace.SubscriptionId != null && profile.SubscriptionId == workspace.SubscriptionId)?.Id;
        }
        if (state.RunningProfileSnapshot is { } running && IsRemoved(running) || state.RunningServerId is { } runningId && removed.Contains(runningId))
        { state.RunningProfileSnapshot = null; state.RunningServerId = null; state.RunningServerName = null; state.RunningCore = null; }
        for (var i = 0; i < state.HappRoutingProfiles.Count; i++)
        {
            var route = state.HappRoutingProfiles[i];
            if (route.Source != RoutingProfileSource.Clash || string.IsNullOrWhiteSpace(route.OriginalClashYaml)) continue;
            try
            {
                var sanitized = SanitizeClash(route.OriginalClashYaml);
                if (sanitized == route.OriginalClashYaml) continue;
                var replacement = ClashProfileParser.Parse(sanitized, route.Name);
                replacement.Id = route.Id;
                replacement.ManagedBySubscriptionId = route.ManagedBySubscriptionId;
                replacement.NameIsCustom = route.NameIsCustom;
                replacement.Links = route.Links;
                state.HappRoutingProfiles[i] = replacement;
            }
            catch (Exception error) when (error is FormatException or YamlDotNet.Core.YamlException)
            { /* An unrelated malformed saved route must not reset the user's state. */ }
        }
        return removed.Count;
    }
}
