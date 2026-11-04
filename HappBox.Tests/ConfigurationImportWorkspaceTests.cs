using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class ConfigurationImportWorkspaceTests
{
    [Fact]
    public void ImportedConfigurationJoinsActiveWorkspaceWithoutChangingSubscriptionOrDuplicatingIds()
    {
        var workspace = new ConnectionWorkspaceProfile { Id = "active", SubscriptionId = "subscription", RoutingProfileId = "route" };
        var other = new ConnectionWorkspaceProfile { Id = "other" };
        var profile = new ProxyProfile { Id = "local", Server = "example.invalid", Port = 443 };
        var state = new AppState { ConnectionProfiles = [workspace, other], SelectedConnectionProfileId = workspace.Id, Profiles = [profile] };
        ConnectionWorkspaceService.AttachConfigurations(state, [profile]);
        ConnectionWorkspaceService.AttachConfigurations(state, [profile]);
        Assert.Equal(new[] { profile.Id }, workspace.ConfigIds);
        Assert.Equal(profile.Id, workspace.DefaultServerId);
        Assert.Equal("subscription", workspace.SubscriptionId);
        Assert.Equal("route", workspace.RoutingProfileId);
        Assert.Empty(other.ConfigIds);
    }

    [Fact]
    public void ExportedHysteriaLinkWithEncodedNameImportsWithAllParameters()
    {
        var original = new ProxyProfile { Protocol = ProxyProtocol.Hysteria2, Name = "Сервер скорость + 50%", Server = "example.invalid", Port = 443,
            Password = "password+%#@", ServerName = "cover.invalid", ObfsType = "salamander", ObfsPassword = "obfs+%", AllowInsecure = true };
        var link = MobileShare.Configuration(original).Text;
        Assert.StartsWith("hysteria2://", link);
        Assert.DoesNotContain('\n', link);
        var imported = Assert.Single(ProfileParser.ParseMany(link));
        Assert.Equal(original.Name, imported.Name);
        Assert.Equal(original.Password, imported.Password);
        Assert.Equal(original.ServerName, imported.ServerName);
        Assert.Equal(original.ObfsPassword, imported.ObfsPassword);
        Assert.Equal(original.Port, imported.Port);
        Assert.True(imported.AllowInsecure);
    }
}
