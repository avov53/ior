using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class RoutingProfileEditorTests
{
    [Fact]
    public void CreatesClashAndReparsesEditedRulesWithoutLosingOrderOrDefinitions()
    {
        const string yaml = "proxy-groups:\n  - name: Pick\n    type: select\n    proxies: [PROXY, DIRECT]\nrule-providers:\n  ads:\n    type: file\n    behavior: domain\n    path: ./ads.yaml\nrules:\n  - DOMAIN-SUFFIX,ru,DIRECT\n  - DOMAIN,ads.example,REJECT\n  - MATCH,PROXY\n";
        var previous = ClashProfileParser.Parse("rules:\n  - MATCH,DIRECT\n");
        previous.ManagedBySubscriptionId = "subscription";
        var result = RoutingProfileEditor.Prepare(new HappRoutingProfile { Id = previous.Id, Name = "Edited", Source = RoutingProfileSource.Clash,
            OriginalClashYaml = yaml, DirectSites = ["stale.example"] }, previous);
        Assert.Equal(previous.Id, result.Id);
        Assert.Equal(yaml, result.OriginalClashYaml);
        Assert.Equal(new[] { "DIRECT", "REJECT", "PROXY" }, result.OrderedRules.Select(item => item.Target));
        Assert.DoesNotContain("stale.example", result.DirectSites);
        Assert.Single(result.RoutePlan!.Groups);
        Assert.Single(result.RoutePlan.Providers);
        Assert.Null(result.ManagedBySubscriptionId);
        Assert.Equal("rules:\n  - MATCH,DIRECT\n", previous.OriginalClashYaml);
    }

    [Fact]
    public void InvalidYamlDoesNotMutatePreviousProfile()
    {
        var previous = ClashProfileParser.Parse("rules:\n  - MATCH,DIRECT\n");
        Assert.ThrowsAny<Exception>(() => RoutingProfileEditor.Prepare(new HappRoutingProfile { Source = RoutingProfileSource.Clash, OriginalClashYaml = "rules: [" }, previous));
        Assert.False(previous.GlobalProxy);
    }

    [Fact]
    public void CannotBypassHappReadOnlyByChangingSource()
    {
        Assert.Throws<InvalidOperationException>(() => RoutingProfileEditor.Prepare(new HappRoutingProfile { Source = RoutingProfileSource.Ior }, new HappRoutingProfile { Source = RoutingProfileSource.Happ }));
    }
}
