namespace HappBox;

public static class ConnectionWorkspaceService
{
    public static void AttachConfigurations(AppState state, IEnumerable<ProxyProfile> imported)
    {
        if(state.SelectedConnectionProfileId is null)return;
        var workspace = state.ConnectionProfiles.FirstOrDefault(profile => profile.Id == state.SelectedConnectionProfileId);
        if (workspace is null) return;
        var ids = imported.Select(profile => profile.Id).Distinct().ToArray();
        foreach (var id in ids)
            if (!workspace.ConfigIds.Contains(id)) workspace.ConfigIds.Add(id);
        if (ids.Length > 0) workspace.DefaultServerId = ids[0];
    }
    public static void AttachSubscription(AppState state, ProxySubscription subscription)
    {
        if(state.SelectedConnectionProfileId is null)return;
        var workspace = state.ConnectionProfiles.FirstOrDefault(profile => profile.Id == state.SelectedConnectionProfileId);
        if (workspace is null)
        {
            workspace = new ConnectionWorkspaceProfile { Name = "Основной" };
            state.ConnectionProfiles.Add(workspace);
            state.SelectedConnectionProfileId = workspace.Id;
        }

        workspace.SubscriptionId = subscription.Id;
        workspace.RoutingProfileId = subscription.RoutingProfileId;
        var firstServer = state.Profiles.FirstOrDefault(profile => profile.SubscriptionId == subscription.Id)?.Id;
        workspace.DefaultServerId = firstServer;
        state.SelectedProfileId = firstServer;
    }

    public static void ReconcileAfterRefresh(AppState state, ProxySubscription subscription, string? previousSelectedId)
    {
        foreach (var workspace in state.ConnectionProfiles.Where(profile => profile.SubscriptionId == subscription.Id))
        {
            workspace.RoutingProfileId = subscription.RoutingProfileId;
            if (state.Profiles.All(profile => profile.Id != workspace.DefaultServerId))
                workspace.DefaultServerId = state.Profiles.FirstOrDefault(profile => profile.SubscriptionId == subscription.Id)?.Id;
        }
        if (previousSelectedId is not null && state.Profiles.All(profile => profile.Id != previousSelectedId))
            state.SelectedProfileId = state.Profiles.FirstOrDefault(p=>!RemovedProtocolPolicy.IsRemoved(p))?.Id;
    }
}
