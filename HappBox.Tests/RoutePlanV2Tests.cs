using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class RoutePlanV2Tests
{
    [Fact]
    public void PreservesFullGroupsProvidersAndNestedMatcher()
    {
        const string yaml = """
            proxy-groups:
              - name: Games
                type: url-test
                proxies: [first, second]
                use: [remote]
                url: https://example.org/health
                interval: 300
                tolerance: 50
                lazy: true
            rule-providers:
              media:
                type: http
                behavior: classical
                format: yaml
                url: https://example.org/rules
                interval: 3600
                path: ./rules/media.yaml
            rules:
              - AND,((DOMAIN,example.com),(NETWORK,UDP)),Games
              - RULE-SET,media,DIRECT,no-resolve
              - MATCH,Games
            """;
        var plan = ClashRoutePlanReader.Parse(yaml);
        Assert.Equal(2, plan.Version);
        Assert.Equal("((DOMAIN,example.com),(NETWORK,UDP))", plan.Rules[0].Value);
        Assert.Equal("Games", plan.Rules[0].Target);
        Assert.Equal(new[] { "first", "second" }, plan.Groups[0].Members);
        Assert.Equal("50", plan.Groups[0].Parameters["tolerance"]!.GetValue<string>());
        Assert.Equal("classical", plan.Providers[0].Behavior);
        Assert.Equal(3600, plan.Providers[0].IntervalSeconds);
        Assert.True(plan.Rules[1].NoResolve);
        Assert.Equal(plan.Rules[0].Id, ClashRoutePlanReader.Parse(yaml).Rules[0].Id);
    }

    [Fact]
    public void DuplicateExpressionsHaveDifferentStableIds()
    {
        var plan = ClashRoutePlanReader.Parse("rules:\n - DOMAIN,example.com,DIRECT\n - DOMAIN,example.com,DIRECT\n - MATCH,PROXY");
        Assert.NotEqual(plan.Rules[0].Id, plan.Rules[1].Id);
    }

    [Fact]
    public void RejectsUnbalancedLogicInsteadOfGuessingTarget() =>
        Assert.Throws<FormatException>(() => ClashRoutePlanReader.Parse("rules:\n - AND,((DOMAIN,a),(NETWORK,UDP),DIRECT\n - MATCH,PROXY"));
}
