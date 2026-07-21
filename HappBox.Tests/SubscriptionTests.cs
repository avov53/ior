using System.Net;
using System.Text;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class SubscriptionTests
{
    [Fact]
    public void MixedBase64SubscriptionKeepsDifferentTransports()
    {
        var lines = string.Join('\n',
            "vless://11111111-1111-1111-1111-111111111111@1.2.3.4:443?security=reality&type=tcp&pbk=key#Reality",
            "vless://11111111-1111-1111-1111-111111111111@1.2.3.4:443?security=tls&type=ws&path=%2Fws#WebSocket",
            "hysteria2://secret@5.6.7.8:8443?sni=example.com#Hysteria",
            "unsupported://value");
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(lines));

        var result = ProfileParser.ParseManyDetailed(payload);

        Assert.Equal(3, result.Profiles.Count);
        Assert.Equal(1, result.RejectedCount);
        Assert.Equal(3, result.Profiles.Select(SubscriptionService.ProfileIdentity).Distinct().Count());
    }

    [Fact]
    public async Task FetchReads3xUiHeadersAndAllProfiles()
    {
        var links = string.Join('\n',
            "vless://11111111-1111-1111-1111-111111111111@1.2.3.4:443?security=reality&type=tcp&pbk=key#One",
            "trojan://secret@5.6.7.8:443?security=tls#Two");
        var routingJson = "{\"Name\":\"3x route\",\"GlobalProxy\":\"true\"}";
        var routingLink = "happ://routing/add/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(routingJson));
        using var client = new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Convert.ToBase64String(Encoding.UTF8.GetBytes(links)))
            };
            response.Headers.TryAddWithoutValidation("Profile-Title", "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("My subscription")));
            response.Headers.TryAddWithoutValidation("Subscription-Userinfo", "upload=10; download=20; total=100; expire=1893456000");
            response.Headers.TryAddWithoutValidation("Routing-Enable", "true");
            response.Headers.TryAddWithoutValidation("Routing", routingLink);
            return response;
        }));

        var result = await SubscriptionService.FetchAsync(client, "https://example.com/sub/token");

        Assert.Equal("My subscription", result.SuggestedName);
        Assert.Equal(2, result.Profiles.Count);
        Assert.Equal(100, result.TotalBytes);
        Assert.True(result.RoutingEnabled);
        Assert.Equal(routingLink, result.RoutingValue);
    }

    [Fact]
    public void RefreshPreservesStableIdsRemovesStaleAndCreatesRouting()
    {
        var subscription = new ProxySubscription { Name = "Sub", Url = "https://example.com/sub", NameIsCustom = true };
        var retained = Vless("Kept", "1.2.3.4", 443, "tcp");
        retained.SubscriptionId = subscription.Id;
        var retainedId = retained.Id;
        var stale = Vless("Stale", "9.9.9.9", 443, "tcp");
        stale.SubscriptionId = subscription.Id;
        var state = new AppState
        {
            Subscriptions = [subscription],
            Profiles = [retained, stale],
            SelectedProfileId = stale.Id
        };
        var routingJson = "{\"Name\":\"Managed\",\"GlobalProxy\":true}";
        var fetched = new SubscriptionFetchResult(
            [Vless("Renamed", "1.2.3.4", 443, "tcp"), Vless("New", "8.8.8.8", 8443, "ws")],
            0, "Header name", 1, 2, 100, null, true,
            "happ://routing/add/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(routingJson)));

        var result = SubscriptionService.Apply(state, subscription, fetched);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Removed);
        Assert.Equal(retainedId, state.Profiles.Single(x => x.Server == "1.2.3.4").Id);
        Assert.DoesNotContain(state.Profiles, x => x.Server == "9.9.9.9");
        Assert.Contains(state.Profiles, x => x.Id == state.SelectedProfileId);
        var routing = Assert.Single(state.HappRoutingProfiles);
        Assert.Equal(subscription.Id, routing.ManagedBySubscriptionId);
        Assert.Equal("Managed", routing.Name);
        Assert.Equal("Sub", subscription.Name);
    }

    [Fact]
    public void InvalidRoutingKeepsPreviousManagedProfile()
    {
        var subscription = new ProxySubscription { Name = "Sub", Url = "https://example.com/sub" };
        var route = new HappRoutingProfile
        {
            Name = "Working",
            ManagedBySubscriptionId = subscription.Id,
            Links = ["{\"Name\":\"Working\"}"]
        };
        subscription.RoutingProfileId = route.Id;
        var state = new AppState { Subscriptions = [subscription], HappRoutingProfiles = [route] };
        var fetched = new SubscriptionFetchResult(
            [Vless("One", "1.2.3.4", 443, "tcp")], 0, null, 0, 0, 0, null, true, "not-valid-routing");

        var result = SubscriptionService.Apply(state, subscription, fetched);

        Assert.NotNull(result.Warning);
        Assert.Same(route, Assert.Single(state.HappRoutingProfiles));
        Assert.Equal("{\"Name\":\"Working\"}", route.Links.Single());
    }

    [Fact]
    public async Task LatencyMonitorLimitsConcurrencyToFour()
    {
        var active = 0;
        var maximum = 0;
        async Task<long?> Probe(ProxyProfile _, CancellationToken token)
        {
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            await Task.Delay(35, token);
            Interlocked.Decrement(ref active);
            return 25;
        }

        using var monitor = new LatencyMonitor(Probe, maximumConcurrency: 4);
        var profiles = Enumerable.Range(1, 10).Select(i => Vless($"P{i}", $"10.0.0.{i}", 4000 + i, "tcp")).ToList();
        monitor.SetProfiles(profiles, profiles[0].Id);

        await monitor.RefreshAllAsync();

        Assert.InRange(maximum, 1, 4);
        Assert.Equal(10, monitor.Snapshot().Count);
        Assert.All(monitor.Snapshot().Values, x => Assert.Equal(25, x.Current));
    }

    private static ProxyProfile Vless(string name, string server, int port, string network)
    {
        return new ProxyProfile
        {
            Name = name,
            Protocol = ProxyProtocol.Vless,
            Server = server,
            Port = port,
            UserId = "11111111-1111-1111-1111-111111111111",
            Network = network,
            TlsEnabled = true,
            ServerName = server
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responseFactory(request));
        }
    }
}
