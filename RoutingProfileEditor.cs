namespace HappBox;

/// <summary>Reparse the complete Clash document; never save stale derived rule lists.</summary>
public static class RoutingProfileEditor
{
    public static HappRoutingProfile Prepare(HappRoutingProfile draft, HappRoutingProfile? previous)
    {
        if(draft.Id=="none"||previous?.Id=="none")throw new InvalidOperationException("Системный маршрут none нельзя изменить.");
        if (string.IsNullOrWhiteSpace(draft.Name)) throw new InvalidOperationException("Название маршрута не может быть пустым.");
        if (previous?.Source == RoutingProfileSource.Happ || draft.Source == RoutingProfileSource.Happ)
            throw new InvalidOperationException("Профили Happ доступны только для чтения.");
        if (previous is not null && previous.Source != draft.Source)
            throw new InvalidOperationException("Формат существующего маршрута менять нельзя; создайте отдельный профиль.");
        if (draft.Source != RoutingProfileSource.Clash) return draft;
        var parsed = ClashProfileParser.Parse(draft.OriginalClashYaml ?? "", draft.Name);
        parsed.Id = draft.Id;
        parsed.ManagedBySubscriptionId = null;
        parsed.NameIsCustom = true;
        return parsed;
    }
}
