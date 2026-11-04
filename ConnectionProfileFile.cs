using System.Text.Json;
using System.Text.Json.Serialization;

namespace HappBox;

public sealed class ConnectionProfileFile
{
    public string Type { get; set; } = "ior-connection-profile";
    public int Version { get; set; } = 1;
    public ConnectionWorkspaceProfile Profile { get; set; } = new();
    public List<ProxyProfile> Configurations { get; set; } = [];
    public ProxySubscription? Subscription { get; set; }
    public HappRoutingProfile? Route { get; set; }
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    public static string Export(AppState state, string id)
    {
        var profile = state.ConnectionProfiles.FirstOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("Профиль не найден.");
        var subscription = state.Subscriptions.FirstOrDefault(s => s.Id == profile.SubscriptionId);
        var route = state.HappRoutingProfiles.FirstOrDefault(r => r.Id == profile.RoutingProfileId);
        var file = new ConnectionProfileFile { Profile = Copy(profile), Configurations = state.Profiles.Where(p => profile.ConfigIds.Contains(p.Id)).Select(Copy).ToList(),
            Subscription = subscription is null ? null : new ProxySubscription { Id = subscription.Id, Name = subscription.Name, Url = subscription.Url }, Route = route is null ? null : Copy(route) };
        return JsonSerializer.Serialize(file, Options);
    }

    public static ConnectionWorkspaceProfile Import(AppState state, string json)
    {
        if (json.Length > 5_000_000) throw new FormatException("Файл профиля слишком большой.");
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Type", out var type) || type.GetString() != "ior-connection-profile" || !document.RootElement.TryGetProperty("Version", out var version) || version.GetInt32() != 1)
            throw new FormatException("Нужен файл профиля подключения ior версии 1.");
        var file = JsonSerializer.Deserialize<ConnectionProfileFile>(json, Options) ?? throw new FormatException("Некорректный профиль.");
        if (file.Profile is null || string.IsNullOrWhiteSpace(file.Profile.Name) || file.Configurations is null || file.Profile.ConfigIds is null)
            throw new FormatException("Некорректный профиль подключения.");
        if (file.Configurations.Any(p => p is null || !Enum.IsDefined(p.Protocol) || RemovedProtocolPolicy.IsRemoved(p) || string.IsNullOrWhiteSpace(p.Server) || p.Port is < 1 or > 65535))
            throw new FormatException("Файл содержит некорректный сервер.");
        if (file.Configurations.Select(p => p.Id).Distinct().Count() != file.Configurations.Count || file.Profile.ConfigIds.Any(id => file.Configurations.All(p => p.Id != id)))
            throw new FormatException("Ссылки на конфигурации в профиле повреждены.");
        if (file.Route is not null && (string.IsNullOrWhiteSpace(file.Route.Name) || !Enum.IsDefined(file.Route.Source))) throw new FormatException("Некорректный маршрут.");
        string? subscriptionUrl = file.Subscription is null ? null : SubscriptionService.NormalizeUrl(file.Subscription.Url);
        if (subscriptionUrl is not null && (!Uri.TryCreate(subscriptionUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) throw new FormatException("Некорректная ссылка подписки.");
        var mapping = file.Configurations.ToDictionary(p => p.Id, _ => Guid.NewGuid().ToString("N"));
        foreach (var config in file.Configurations) { config.Id = mapping[config.Id]; config.SubscriptionId = null; }
        var selected = file.Profile.DefaultServerId;
        file.Profile.Id = Guid.NewGuid().ToString("N");
        file.Profile.ConfigIds = file.Profile.ConfigIds.Select(id => mapping[id]).Distinct().ToList();
        file.Profile.DefaultServerId = selected is not null && mapping.TryGetValue(selected, out var newId) ? newId : file.Profile.ConfigIds.FirstOrDefault();
        file.Profile.SubscriptionId = null;file.Profile.RoutingProfileId = null;file.Profile.IorTrafficMode = IorTrafficMode.Standard;
        ProxySubscription? subscription = null;
        if (subscriptionUrl is not null) { subscription = new ProxySubscription { Id = Guid.NewGuid().ToString("N"), Name = file.Subscription!.Name, Url = subscriptionUrl }; file.Profile.SubscriptionId = subscription.Id; }
        if (file.Route is not null) { file.Route.Id = Guid.NewGuid().ToString("N");file.Route.ManagedBySubscriptionId = null;file.Profile.RoutingProfileId = file.Route.Id; }
        state.Profiles.AddRange(file.Configurations);
        if (subscription is not null) state.Subscriptions.Add(subscription);
        if (file.Route is not null) state.HappRoutingProfiles.Add(file.Route);
        state.ConnectionProfiles.Add(file.Profile);
        return file.Profile;
    }
}
