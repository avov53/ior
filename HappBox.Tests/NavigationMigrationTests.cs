using System.Reflection;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class NavigationMigrationTests
{
    [Fact]
    public void LegacyAboutItemIsDroppedAndNewNavigationItemsAreRestored()
    {
        var normalize = typeof(MainForm).GetMethod("NormalizeNavItemOrder", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(normalize);
        var result = Assert.IsType<List<string>>(normalize!.Invoke(null, [new[] { "servers", "info", "logs" }]));
        Assert.Equal(new[] { "servers", "logs", "settings", "config" }, result);
        Assert.DoesNotContain("info", result);
    }
}
