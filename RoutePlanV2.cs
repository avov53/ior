using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace HappBox;

public sealed class RoutePlanV2
{
    public int Version { get; set; } = 2;
    public List<OrderedRouteRule> Rules { get; set; } = [];
    public List<RouteGroupDefinition> Groups { get; set; } = [];
    public List<RouteProviderDefinition> Providers { get; set; } = [];
}

public sealed class RouteGroupDefinition
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public List<string> Members { get; set; } = [];
    public List<string> Providers { get; set; } = [];
    public JsonObject Parameters { get; set; } = new();
}

public sealed class RouteProviderDefinition
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Behavior { get; set; } = "";
    public string Format { get; set; } = "yaml";
    public string? Url { get; set; }
    public string? Path { get; set; }
    public int IntervalSeconds { get; set; }
    public JsonObject Parameters { get; set; } = new();
}

public static class ClashRoutePlanReader
{
    public static RoutePlanV2 Parse(string yaml)
    {
        if (yaml.Length > 2_000_000) throw new FormatException("Clash document exceeds import limit.");
        var stream = new YamlStream();
        try { stream.Load(new StringReader(yaml)); }
        catch (Exception error) { throw new FormatException("Некорректный YAML маршрута Clash.", error); }
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new FormatException("Маршрут Clash должен содержать один YAML-объект.");
        var plan = new RoutePlanV2();
        if (Get(root, "rules") is not YamlSequenceNode rules || rules.Children.Count == 0)
            throw new FormatException("Не найден упорядоченный список rules Clash.");
        var occurrence = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in rules.Children)
        {
            if (node is not YamlScalarNode { Value: { } raw }) throw new FormatException("Clash rule must be a scalar expression.");
            var parts = SplitExpression(raw);
            if (parts.Count < 2) throw new FormatException($"Неполное правило Clash: {raw}");
            var kind = parts[0].ToUpperInvariant();
            var targetIndex = kind == "MATCH" ? 1 : 2;
            if (parts.Count <= targetIndex) throw new FormatException($"Правило без цели: {raw}");
            var count = occurrence.GetValueOrDefault(raw); occurrence[raw] = count + 1;
            plan.Rules.Add(new OrderedRouteRule
            {
                Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"clash\n{raw}\n{count}")))[..24],
                Kind = kind, Value = kind == "MATCH" ? "" : parts[1], Target = parts[targetIndex],
                Parameters = parts.Skip(targetIndex + 1).ToList(),
                NoResolve = parts.Skip(targetIndex + 1).Any(item => item.Equals("no-resolve", StringComparison.OrdinalIgnoreCase)),
                Source = RoutingProfileSource.Clash, Raw = raw
            });
        }
        if (Get(root, "proxy-groups") is YamlSequenceNode groups)
            foreach (var node in groups.Children)
            {
                if (node is not YamlMappingNode group) throw new FormatException("Некорректная группа Clash.");
                plan.Groups.Add(new RouteGroupDefinition { Name = Required(group, "name"), Type = Scalar(group, "type") ?? "",
                    Members = Strings(Get(group, "proxies")), Providers = Strings(Get(group, "use")), Parameters = Object(group) });
            }
        if (Get(root, "rule-providers") is YamlMappingNode providers)
            foreach (var pair in providers.Children)
            {
                if (pair.Key is not YamlScalarNode { Value: { } name } || pair.Value is not YamlMappingNode provider)
                    throw new FormatException("Некорректный провайдер правил Clash.");
                plan.Providers.Add(new RouteProviderDefinition { Name = name, Type = Scalar(provider, "type") ?? "",
                    Behavior = Scalar(provider, "behavior") ?? "", Format = Scalar(provider, "format") ?? "yaml",
                    Url = Scalar(provider, "url"), Path = Scalar(provider, "path"),
                    IntervalSeconds = int.TryParse(Scalar(provider, "interval"), out var interval) ? interval : 0,
                    Parameters = Object(provider) });
            }
        if (plan.Groups.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != plan.Groups.Count)
            throw new FormatException("Повторяющиеся имена групп Clash.");
        return plan;
    }

    public static List<string> SplitExpression(string raw)
    {
        var parts = new List<string>(); var depth = 0; var start = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '(') depth++;
            else if (raw[i] == ')' && --depth < 0) throw new FormatException("Unbalanced Clash matcher.");
            else if (raw[i] == ',' && depth == 0) { parts.Add(raw[start..i].Trim()); start = i + 1; }
        }
        if (depth != 0) throw new FormatException("Unbalanced Clash matcher.");
        parts.Add(raw[start..].Trim()); return parts;
    }

    private static YamlNode? Get(YamlMappingNode mapping, string key) => mapping.Children.FirstOrDefault(pair => pair.Key is YamlScalarNode scalar && scalar.Value == key).Value;
    private static string? Scalar(YamlMappingNode node, string key) => (Get(node, key) as YamlScalarNode)?.Value;
    private static string Required(YamlMappingNode node, string key) => Scalar(node, key) is { Length: > 0 } value ? value : throw new FormatException($"Missing Clash definition field: {key}");
    private static List<string> Strings(YamlNode? node) => node is YamlSequenceNode sequence ? sequence.Children.Select(item => item is YamlScalarNode { Value: { } value } ? value : throw new FormatException("Clash member must be a name.")).ToList() : [];
    private static JsonObject Object(YamlMappingNode node, int depth = 0)
    {
        if (depth > 32) throw new FormatException("Clash YAML nesting or alias cycle exceeds limit.");
        var result = new JsonObject();
        foreach (var pair in node.Children)
        {
            if (pair.Key is not YamlScalarNode { Value: { } key }) throw new FormatException("Clash keys must be strings.");
            result.Add(key, Value(pair.Value, depth + 1));
        }
        return result;
    }
    private static JsonNode? Value(YamlNode node, int depth) => depth > 32 ? throw new FormatException("Clash YAML nesting exceeds limit.") : node switch
    {
        YamlScalarNode scalar => JsonValue.Create(scalar.Value),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(item => Value(item, depth + 1)).ToArray()),
        YamlMappingNode mapping => Object(mapping, depth + 1),
        _ => throw new FormatException("Unsupported YAML node.")
    };
}
