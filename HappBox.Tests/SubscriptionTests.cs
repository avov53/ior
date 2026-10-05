using System.Net;
using System.Text;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class SubscriptionTests
{
    private const string ClashYaml = """
        proxies:
          - name: Reality One
            type: vless
            server: edge.example.com
            port: 443
            uuid: 11111111-1111-1111-1111-111111111111
            network: tcp
            tls: true
            servername: cover.example.com
            client-fingerprint: chrome
            reality-opts:
              public-key: example-public-key
              short-id: abcdef
          - name: Hysteria Two
            type: hysteria2
            server: hy.example.com
            port: 8443
            password: test-password
            sni: hy.example.com
        proxy-groups:
          - name: Choose
            type: select
            proxies:
              - Reality One
              - Hysteria Two
        rule-providers:
          media:
            type: http
            url: https://example.com/media.yaml
        rules:
          - DOMAIN-SUFFIX,example.com,Choose
          - RULE-SET,media,Choose
          - MATCH,DIRECT
        """;

    [Fact]
    public void ClashInstallLinkResolvesToCanonicalSubscriptionUrl()
    {
        Assert.Equal("https://example.com/sub?token=abc",
            SubscriptionService.NormalizeUrl("clash://install-config?url=https%3A%2F%2Fexample.com%2Fsub%3Ftoken%3Dabc"));
        Assert.Throws<FormatException>(() => SubscriptionService.NormalizeUrl("clash://install-config?url=file%3A%2F%2Fsecret"));
    }

    [Fact]
    public async Task ClashSubscriptionImportsServersAndRoutingAsOneManagedUnit()
    {
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClashYaml) }));
        var fetched = await SubscriptionService.FetchAsync(client, "https://example.com/clash");
        Assert.True(fetched.IsClashYaml);
        Assert.Equal(2, fetched.Profiles.Count);
        var reality = fetched.Profiles.Single(profile => profile.Protocol == ProxyProtocol.Vless);
        Assert.True(reality.RealityEnabled);
        Assert.Equal("cover.example.com", reality.ServerName);
        Assert.Equal("example-public-key", reality.PublicKey);
        Assert.Equal("test-password", fetched.Profiles.Single(profile => profile.Protocol == ProxyProtocol.Hysteria2).Password);

        var subscription = new ProxySubscription { Name = "Clash", Url = "https://example.com/clash" };
        var state = new AppState { Subscriptions = [subscription] };
        var merge = SubscriptionService.Apply(state, subscription, fetched);
        ConnectionWorkspaceService.AttachSubscription(state, subscription);
        var route = Assert.Single(state.HappRoutingProfiles);
        Assert.Equal(RoutingProfileSource.Clash, route.Source);
        Assert.Equal(subscription.Id, route.ManagedBySubscriptionId);
        Assert.Equal(subscription.RoutingProfileId, route.Id);
        Assert.Equal(route.Id, Assert.Single(state.ConnectionProfiles).RoutingProfileId);
        Assert.Equal(3, route.ClashOrderedRules.Count);
        Assert.Contains("rule-providers:", route.OriginalClashYaml);
        Assert.NotNull(merge.Warning); // complex groups/rule sets are saved, never silently flattened

        SubscriptionService.Apply(state, subscription, fetched);
        Assert.Equal(route.Id, Assert.Single(state.HappRoutingProfiles).Id);
        SubscriptionService.Remove(state, subscription);
        Assert.Empty(state.HappRoutingProfiles);
        Assert.Empty(state.Profiles);
    }

    [Fact]
    public void InvalidClashRefreshDoesNotMutateExistingSubscription()
    {
        var subscription = new ProxySubscription();
        var state = new AppState { Subscriptions = [subscription] };
        var good = ClashSubscriptionParser.Parse(ClashYaml);
        SubscriptionService.Apply(state, subscription, new SubscriptionFetchResult(good.Profiles, good.RejectedCount,
            null, 0, 0, 0, null, null, null, IsClashYaml: true, ClashRouting: good.Routing));
        var serverId = state.Profiles[0].Id;
        var routeId = subscription.RoutingProfileId;

        Assert.Throws<FormatException>(() => ClashSubscriptionParser.Parse("proxies:\n  - name: broken\n    type: vless\nrules:\n  - MATCH,DIRECT"));
        Assert.Equal(serverId, state.Profiles[0].Id);
        Assert.Equal(routeId, subscription.RoutingProfileId);
    }

    [Fact]
    public void ClashImportPreservesSupportedVlessWebSocketTransport()
    {
        const string yaml = """
            proxies:
              - name: Reality TCP
                type: vless
                server: edge.example
                port: 443
                uuid: 11111111-1111-1111-1111-111111111111
                reality-opts:
                  public-key: key
              - name: WebSocket
                type: vless
                server: ws.example
                port: 443
                uuid: 22222222-2222-2222-2222-222222222222
                network: ws
                ws-opts:
                  path: /custom
            rules:
              - MATCH,PROXY
            """;

        var parsed = ClashSubscriptionParser.Parse(yaml);
        Assert.Equal(2, parsed.Profiles.Count);
        Assert.Equal("Reality TCP", parsed.Profiles[0].Name);
        Assert.Equal("ws", parsed.Profiles[1].Network);
        Assert.Equal("/custom", parsed.Profiles[1].Path);
        Assert.Equal(0, parsed.RejectedCount);
    }

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
            response.Headers.TryAddWithoutValidation("Support-Url", "https://support.example.com/help");
            response.Headers.TryAddWithoutValidation("Profile-Web-Page-Url", "https://profile.example.com/me");
            response.Headers.TryAddWithoutValidation("Profile-Update-Interval", "6");
            response.Headers.TryAddWithoutValidation("Announce", "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Технические работы завтра")));
            return response;
        }));

        var result = await SubscriptionService.FetchAsync(client, "https://example.com/sub/token");

        Assert.Equal("My subscription", result.SuggestedName);
        Assert.Equal(2, result.Profiles.Count);
        Assert.Equal(100, result.TotalBytes);
        Assert.True(result.HasUserInfo);
        Assert.True(result.HasTotal);
        Assert.True(result.RoutingEnabled);
        Assert.Equal(routingLink, result.RoutingValue);
        Assert.Equal("https://support.example.com/help", result.SupportUrl);
        Assert.Equal("https://profile.example.com/me", result.ProfileWebPageUrl);
        Assert.Equal("Технические работы завтра", result.Announcement);
        Assert.Equal(6, result.RefreshIntervalHours);
    }

    [Fact]
    public async Task ZeroTotalFromServerMeansUnlimitedAndKeepsUsedTraffic()
    {
        const string link = "vless://11111111-1111-1111-1111-111111111111@1.2.3.4:443?security=tls#One";
        using var client = new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(link) };
            response.Headers.TryAddWithoutValidation("Subscription-Userinfo", "upload=1073741824; download=2147483648; total=0");
            return response;
        }));

        var result = await SubscriptionService.FetchAsync(client, "https://example.com/unlimited");
        var subscription = new ProxySubscription();
        var state = new AppState();
        SubscriptionService.Apply(state, subscription, result);

        Assert.True(subscription.HasUsageInfo);
        Assert.True(result.HasTotal);
        Assert.Equal(0, subscription.TotalBytes);
        Assert.Equal(1073741824, subscription.UploadBytes);
        Assert.Equal(2147483648, subscription.DownloadBytes);
        Assert.Equal(0, Assert.Single(state.Profiles).SubscriptionTotalBytes);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ProxySubscription>(System.Text.Json.JsonSerializer.Serialize(subscription));
        Assert.True(restored?.HasUsageInfo);
        Assert.Equal(0, restored?.TotalBytes);
    }

    [Fact]
    public async Task MissingUserInfoDoesNotPretendToBeUnlimited()
    {
        const string link = "vless://11111111-1111-1111-1111-111111111111@1.2.3.4:443?security=tls#One";
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(link) }));

        var result = await SubscriptionService.FetchAsync(client, "https://example.com/no-usage");
        var subscription = new ProxySubscription();
        var state = new AppState();
        SubscriptionService.Apply(state, subscription, result);

        Assert.False(subscription.HasUsageInfo);
        Assert.Null(subscription.TotalBytes);
        Assert.Null(Assert.Single(state.Profiles).SubscriptionTotalBytes);
    }

    [Fact]
    public async Task UsageWithoutTotalKeepsTrafficButLeavesLimitUnknown()
    {
        const string link = "vless://11111111-1111-1111-1111-111111111111@1.2.3.4:443?security=tls#One";
        using var client = new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(link) };
            response.Headers.TryAddWithoutValidation("Subscription-Userinfo", "upload=1024; download=2048");
            return response;
        }));

        var result = await SubscriptionService.FetchAsync(client, "https://example.com/usage-only");
        var subscription = new ProxySubscription();
        SubscriptionService.Apply(new AppState(), subscription, result);

        Assert.True(result.HasUserInfo);
        Assert.False(result.HasTotal);
        Assert.False(subscription.HasUsageInfo);
        Assert.Equal(1024, subscription.UploadBytes);
        Assert.Equal(2048, subscription.DownloadBytes);
        Assert.Null(subscription.TotalBytes);
    }

    [Fact]
    public async Task OptionalMetadataIsIndependentPerSubscriptionAndInvalidLinksAreIgnored()
    {
        const string link = "vless://11111111-1111-1111-1111-111111111111@1.2.3.4:443?security=tls#One";
        using var client = new HttpClient(new StubHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(link) };
            if (request.RequestUri!.AbsolutePath == "/first")
            {
                response.Headers.TryAddWithoutValidation("Announce", "Plain announcement");
                response.Headers.TryAddWithoutValidation("Support-Url", "javascript:alert(1)");
            }
            return response;
        }));

        var first = await SubscriptionService.FetchAsync(client, "https://example.com/first");
        var second = await SubscriptionService.FetchAsync(client, "https://example.com/second");
        var state = new AppState();
        var firstSubscription = new ProxySubscription { Url = "https://example.com/first" };
        var secondSubscription = new ProxySubscription { Url = "https://example.com/second" };
        SubscriptionService.Apply(state, firstSubscription, first);
        SubscriptionService.Apply(state, secondSubscription, second);

        Assert.Equal("Plain announcement", firstSubscription.Announcement);
        Assert.Null(firstSubscription.SupportUrl);
        Assert.Null(secondSubscription.Announcement);
        Assert.Null(secondSubscription.ProfileWebPageUrl);
        Assert.Null(secondSubscription.SupportUrl);

        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<ProxySubscription>(System.Text.Json.JsonSerializer.Serialize(firstSubscription));
        Assert.Equal(firstSubscription.Announcement, roundTrip?.Announcement);
    }

    [Fact]
    public void SuccessfulRefreshReplacesServerMetadata()
    {
        var subscription = new ProxySubscription { SupportUrl = "https://old.example.com", Announcement = "Old" };
        var state = new AppState();
        var fetched = new SubscriptionFetchResult([Vless("One", "1.2.3.4", 443, "tcp")], 0, null, 0, 0, 0, null, null, null);

        SubscriptionService.Apply(state, subscription, fetched);

        Assert.Null(subscription.SupportUrl);
        Assert.Null(subscription.Announcement);
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
    public void ServerIntervalAppliesUntilUserOverridesIt()
    {
        var subscription = new ProxySubscription();
        var state = new AppState { Subscriptions = [subscription] };
        SubscriptionFetchResult Fetch(int hours) => new([Vless("One", "example.com", 443, "tcp")], 0, null, 0, 0, 0, null, null, null, RefreshIntervalHours: hours);

        SubscriptionService.Apply(state, subscription, Fetch(6));
        Assert.Equal(6, subscription.RefreshIntervalHours);
        subscription.RefreshIntervalHours = 24;
        subscription.RefreshIntervalIsCustom = true;
        SubscriptionService.Apply(state, subscription, Fetch(12));
        Assert.Equal(24, subscription.RefreshIntervalHours);
        Assert.Equal(12, subscription.ServerRefreshIntervalHours);
    }

    [Fact]
    public void IdenticalSubscriptionRoutingSharesOneProfile()
    {
        var first = new ProxySubscription { Name = "First" };
        var second = new ProxySubscription { Name = "Second" };
        var state = new AppState { Subscriptions = [first, second] };
        SubscriptionFetchResult Fetch() => new([Vless("One", "example.com", 443, "tcp")], 0, null, 0, 0, 0, null, true, "{\"Name\":\"Shared\"}");

        SubscriptionService.Apply(state, first, Fetch());
        SubscriptionService.Apply(state, second, Fetch());
        Assert.Single(state.HappRoutingProfiles);
        Assert.Equal(first.RoutingProfileId, second.RoutingProfileId);

        SubscriptionService.Remove(state, first);
        Assert.Single(state.HappRoutingProfiles);
        SubscriptionService.Remove(state, second);
        Assert.Empty(state.HappRoutingProfiles);
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
