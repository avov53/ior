namespace HappBox;

public static class AwgClientPolicy
{
    public static bool EmptyRoute(HappRoutingProfile route) =>
        route.Links.Count==0 && route.OrderedRules.Count==0 && route.ClashOrderedRules.Count==0 && route.ClashUnmappedRules.Count==0 &&
        (route.RoutePlan is null || route.RoutePlan.Version==2 && route.RoutePlan.Rules.Count==0) &&
        new[]{route.ProxySites,route.DirectSites,route.BlockSites,route.ProxyIp,route.DirectIp,route.BlockIp,
            route.ProxyDomains,route.DirectDomains,route.BlockDomains,route.ProxyKeywords,route.DirectKeywords,route.BlockKeywords}.All(values=>values.Count==0);
    public static void Migrate(AppState state)
    {
        if (state.AwgRustDefaultMigrated) return;
        state.AwgEngine = AwgEngine.Rust;
        state.AwgRustDefaultMigrated = true;
    }

    public static string RoutingSummary(AppState state)
    {
        var route = RoutingSelectionPolicy.Effective(state);
        if (route is null && !string.IsNullOrWhiteSpace(state.SelectedHappRoutingProfileId))
            return "Выбранный профиль маршрутизации не найден";
        if (route is null || EmptyRoute(route) || OrderedRoutePlan.TryCreate(route, out var rules, out _) && rules.All(rule => rule.Kind.Equals("MATCH", StringComparison.OrdinalIgnoreCase)))
            return "Все сайты через AWG";
        return "По правилам выбранного профиля; остальные сайты через AWG";
    }
}
