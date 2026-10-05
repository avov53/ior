using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class ConnectionWorkspaceServiceTests
{
    [Fact]
    public void NewSubscriptionBecomesVisibleAndSelectedInActiveWorkspace()
    {
        var existing = new ProxyProfile { Id = "old", Name = "Old" };
        var subscription = new ProxySubscription { Id = "sub" };
        var imported = new ProxyProfile { Id = "new", Name = "New", SubscriptionId = subscription.Id };
        var workspace = new ConnectionWorkspaceProfile { Id = "workspace", ConfigIds = [existing.Id], DefaultServerId = existing.Id };
        var state = new AppState { Profiles = [existing, imported], ConnectionProfiles = [workspace],
            SelectedConnectionProfileId = workspace.Id, SelectedProfileId = existing.Id,
            RunningServerId = existing.Id, RunningServerName = existing.Name };

        ConnectionWorkspaceService.AttachSubscription(state, subscription);

        Assert.Equal(subscription.Id, workspace.SubscriptionId);
        Assert.Equal(imported.Id, workspace.DefaultServerId);
        Assert.Equal(imported.Id, state.SelectedProfileId);
        Assert.Equal(existing.Id, state.RunningServerId);
        Assert.Equal(existing.Name, state.RunningServerName);
    }

    [Fact]
    public void ExplicitReimportActivatesExistingSubscriptionInAnotherWorkspace()
    {
        var subscription = new ProxySubscription { Id = "existing" };
        var imported = new ProxyProfile { Id = "server", SubscriptionId = subscription.Id };
        var workspace = new ConnectionWorkspaceProfile { Id = "workspace", SubscriptionId = "other", DefaultServerId = "old" };
        var state = new AppState { Profiles = [imported], Subscriptions = [subscription], ConnectionProfiles = [workspace],
            SelectedConnectionProfileId = workspace.Id, SelectedProfileId = "old" };

        ConnectionWorkspaceService.AttachSubscription(state, subscription);

        Assert.Equal(subscription.Id, workspace.SubscriptionId);
        Assert.Equal(imported.Id, workspace.DefaultServerId);
        Assert.Equal(imported.Id, state.SelectedProfileId);
    }

    [Fact]
    public void RefreshReplacesRemovedDefaultAndSelection()
    {
        var subscription = new ProxySubscription { Id = "sub" };
        var next = new ProxyProfile { Id = "next", SubscriptionId = subscription.Id };
        var workspace = new ConnectionWorkspaceProfile { Id = "workspace", SubscriptionId = subscription.Id, DefaultServerId = "removed" };
        var state = new AppState { Profiles = [next], ConnectionProfiles = [workspace],
            SelectedConnectionProfileId = workspace.Id, SelectedProfileId = next.Id };

        ConnectionWorkspaceService.ReconcileAfterRefresh(state, subscription, "removed");

        Assert.Equal(next.Id, workspace.DefaultServerId);
        Assert.Equal(next.Id, state.SelectedProfileId);
    }

    [Fact]
    public void RemovingSubscriptionClearsBindingAndSelectsPersonalServer()
    {
        var subscription = new ProxySubscription { Id = "sub" };
        var subscribed = new ProxyProfile { Id = "sub-server", SubscriptionId = subscription.Id };
        var personal = new ProxyProfile { Id = "personal" };
        var workspace = new ConnectionWorkspaceProfile { Id = "workspace", SubscriptionId = subscription.Id,
            ConfigIds = [personal.Id], DefaultServerId = subscribed.Id };
        var state = new AppState { Profiles = [subscribed, personal], Subscriptions = [subscription],
            ConnectionProfiles = [workspace], SelectedConnectionProfileId = workspace.Id, SelectedProfileId = subscribed.Id };

        SubscriptionService.Remove(state, subscription);

        Assert.Null(workspace.SubscriptionId);
        Assert.Equal(personal.Id, workspace.DefaultServerId);
        Assert.Equal(personal.Id, state.SelectedProfileId);
        Assert.DoesNotContain(state.Profiles, profile => profile.Id == subscribed.Id);
    }

    [Fact]
    public void RunningServerSnapshotSurvivesRemovedSubscriptionAndStateReload()
    {
        var subscription = new ProxySubscription { Id = "sub" };
        var running = new ProxyProfile { Id = "running", Name = "Previously connected", SubscriptionId = subscription.Id,
            Protocol = ProxyProtocol.Vless, Server = "203.0.113.10", Port = 443 };
        var workspace = new ConnectionWorkspaceProfile { Id = "workspace", SubscriptionId = subscription.Id, DefaultServerId = running.Id };
        var state = new AppState { Profiles = [running], Subscriptions = [subscription], ConnectionProfiles = [workspace],
            SelectedConnectionProfileId = workspace.Id, SelectedProfileId = running.Id,
            RunningServerId = running.Id, RunningServerName = running.Name, RunningProfileSnapshot = running };

        SubscriptionService.Remove(state, subscription);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppState>(System.Text.Json.JsonSerializer.Serialize(state))!;

        Assert.Empty(restored.Profiles);
        Assert.Null(restored.SelectedProfileId);
        Assert.Equal(running.Id, restored.RunningServerId);
        Assert.Equal(running.Server, restored.RunningProfileSnapshot?.Server);
    }
}
