using System.Net;
using System.Text;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class SubscriptionHwidTests
{
    private const string Node = "vless://11111111-1111-1111-1111-111111111111@edge.example:443?security=tls#Test";
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Origin, string? Id)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add((SubscriptionHwid.Origin(request.RequestUri!), request.Headers.TryGetValues("x-hwid", out var values) ? values.Single() : null));
            return Task.FromResult(respond(request));
        }
    }
    private static HttpResponseMessage Response(string? flag = null, HttpStatusCode status = HttpStatusCode.OK, string? body = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body ?? Node) };
        if (flag is not null) response.Headers.Add(flag, "true");
        return response;
    }
    private static SubscriptionRequestIdentity Context(AppState state, ProxySubscription sub) =>
        SubscriptionHwid.Context(state, sub, origin => SubscriptionIdentity.Derive("test-install", origin));

    [Fact]
    public void IdentifierIsStablePerProviderAndDoesNotRevealUrlCredentials()
    {
        var origin = SubscriptionHwid.Origin(new Uri("https://user:password@EXAMPLE.test:443/path?token=secret"));
        Assert.Equal("https://example.test", origin);
        var id = SubscriptionIdentity.Derive("seed", origin);
        Assert.True(Guid.TryParse(id, out _)); Assert.True(SubscriptionHwid.IsValid(id));
        Assert.Equal(id, SubscriptionIdentity.Derive("seed", origin));
        Assert.NotEqual(id, SubscriptionIdentity.Derive("seed", "https://other.test"));
    }
    [Fact]
    public void IdentityFileSurvivesRestartAndIsOutsideBackupState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ior-hwid-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var id = new SubscriptionIdentity(directory).ForOrigin("https://example.test");
            Assert.Equal(id, new SubscriptionIdentity(directory).ForOrigin("https://example.test"));
            Assert.DoesNotContain(id, System.Text.Json.JsonSerializer.Serialize(new AppState()));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData(false, SubscriptionHwidMode.Auto, false)]
    [InlineData(true, SubscriptionHwidMode.Auto, true)]
    [InlineData(false, SubscriptionHwidMode.Allow, true)]
    [InlineData(true, SubscriptionHwidMode.Block, false)]
    [InlineData(true, (SubscriptionHwidMode)99, false)]
    public async Task GlobalAndIndividualPermissionsControlRequestHeaders(bool global, SubscriptionHwidMode mode, bool sent)
    {
        var state = new AppState { SendSubscriptionHwid = global };
        var sub = new ProxySubscription { Url = "https://example.test/sub", HwidMode = mode };
        if (mode == SubscriptionHwidMode.Allow) sub.HwidApprovedOrigins.Add("https://example.test");
        var handler = new Handler(_ => Response()); using var client = new HttpClient(handler);
        await SubscriptionService.FetchAsync(client, sub.Url, identity: Context(state, sub));
        Assert.Equal(sent, handler.Requests.Single().Id is not null);
        Assert.False(client.DefaultRequestHeaders.Contains("x-hwid"));
    }
    [Fact]
    public async Task RequirementDoesNotAutomaticallyRetryOrChangeUserSettings()
    {
        var state = new AppState(); var sub = new ProxySubscription { Url = "https://example.test/sub", LastUpdatedUtc = DateTime.UtcNow.AddDays(-1) };
        var previous = sub.LastUpdatedUtc;
        var handler = new Handler(request => Response("subscription-always-hwid-enable")); using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<SubscriptionHwidException>(() => SubscriptionService.FetchAsync(client, sub.Url, identity: Context(state, sub)));
        Assert.Equal("required", error.Code); Assert.Single(handler.Requests); Assert.Null(handler.Requests[0].Id);
        Assert.False(state.SendSubscriptionHwid); Assert.Equal(SubscriptionHwidMode.Auto, sub.HwidMode); Assert.Equal(previous, sub.LastUpdatedUtc);
        sub.HwidMode = SubscriptionHwidMode.Allow;
        sub.HwidApprovedOrigins.Add("https://example.test");
        var result = await SubscriptionService.FetchAsync(client, sub.Url, identity: Context(state, sub));
        Assert.Equal(2, handler.Requests.Count); Assert.True(result.RequiresHwid);
        Assert.NotNull(handler.Requests[1].Id);
    }
    [Fact]
    public async Task CrossOriginRedirectDoesNotForwardIdentifierWithoutApproval()
    {
        var state = new AppState { SendSubscriptionHwid = true }; var sub = new ProxySubscription { Url = "https://first.test/sub" };
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.Host == "first.test")
            { var response = Response(status: HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://second.test/sub"); return response; }
            return Response();
        });
        using var client = new HttpClient(handler);
        await SubscriptionService.FetchAsync(client, sub.Url, identity: Context(state, sub));
        Assert.NotNull(handler.Requests[0].Id); Assert.Null(handler.Requests[1].Id);
        sub.HwidApprovedOrigins.Add("https://second.test");
        await SubscriptionService.FetchAsync(client, sub.Url, identity: Context(state, sub));
        Assert.NotNull(handler.Requests[3].Id); Assert.NotEqual(handler.Requests[2].Id, handler.Requests[3].Id);
    }
    [Fact]
    public async Task SameOriginRedirectRetainsPermissionAndHttpNeverReceivesHwid()
    {
        var state = new AppState { SendSubscriptionHwid = true }; var sub = new ProxySubscription { Url = "https://first.test/start" };
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/start")
            { var response = Response(status: HttpStatusCode.Redirect); response.Headers.Location = new Uri("/finish", UriKind.Relative); return response; }
            return Response();
        });
        using var client = new HttpClient(handler);
        await SubscriptionService.FetchAsync(client, sub.Url, identity: Context(state, sub));
        Assert.Equal(handler.Requests[0].Id, handler.Requests[1].Id);
        sub.Url = "http://first.test/finish";
        await SubscriptionService.FetchAsync(client, sub.Url, identity: Context(state, sub));
        Assert.Null(handler.Requests.Last().Id);
    }
    [Theory]
    [InlineData("x-hwid-max-devices-reached")]
    [InlineData("x-hwid-limit")]
    public async Task DeviceLimitIsRecognizedBeforeHttpStatusAndDoesNotMerge(string flag)
    {
        var sub = new ProxySubscription { Url = "https://first.test/sub" };
        var handler = new Handler(_ => Response(flag, HttpStatusCode.Forbidden)); using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<SubscriptionHwidException>(() => SubscriptionService.FetchAsync(client, sub.Url));
        Assert.Equal("limit", error.Code);
    }
    [Theory]
    [InlineData("short")]
    [InlineData("valid-value-123\r\nheader")]
    public async Task InvalidManualIdentityIsRejectedBeforeSending(string value)
    {
        var sub = new ProxySubscription { Url = "https://first.test/sub", HwidMode = SubscriptionHwidMode.Allow, HwidOverride = value };
        sub.HwidApprovedOrigins.Add("https://first.test");
        var handler = new Handler(_ => Response()); using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<FormatException>(() => SubscriptionService.FetchAsync(client, sub.Url, identity: Context(new AppState(), sub)));
        Assert.Empty(handler.Requests);
    }
    [Fact]
    public void ImportedBackupAndSharedSubscriptionDoNotGrantHwidPermission()
    {
        var state = new AppState { SendSubscriptionHwid = true };
        var sub = new ProxySubscription { HwidMode = SubscriptionHwidMode.Allow, HwidOverride = "manual-123456", HwidApprovedOrigins = ["https://example.test"] };
        state.Subscriptions.Add(sub); SubscriptionHwid.ClearImportedPermissions(state);
        Assert.False(state.SendSubscriptionHwid); Assert.Null(sub.HwidOverride); Assert.Empty(sub.HwidApprovedOrigins); Assert.Equal(SubscriptionHwidMode.Auto, sub.HwidMode);
        Assert.True(SubscriptionHwid.BodyRequires(Convert.ToBase64String(Encoding.UTF8.GetBytes("#subscription-always-hwid-enable: 1\n" + Node))));
        Assert.False(SubscriptionHwid.BodyRequires("#subscription-always-hwid-enable: false\n" + Node));
    }

    [Fact]
    public async Task AuthorizedManualHwidIsExactAndDirectiveIsNotCountedAsRejectedServer()
    {
        var sub = new ProxySubscription { Url = "https://example.test/sub", HwidMode = SubscriptionHwidMode.Allow, HwidOverride = "existing-happ-12345" };
        sub.HwidApprovedOrigins.Add("https://example.test");
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes("#subscription-always-hwid-enable: 1\n" + Node));
        var handler = new Handler(request =>
        {
            Assert.Equal("Windows", request.Headers.GetValues("x-device-os").Single());
            Assert.Equal("Windows PC", request.Headers.GetValues("x-device-model").Single());
            Assert.StartsWith("ior/", request.Headers.UserAgent.ToString());
            return Response(body: body);
        });
        using var client = new HttpClient(handler);
        var result = await SubscriptionService.FetchAsync(client, sub.Url, identity: Context(new AppState(), sub));
        Assert.Equal(sub.HwidOverride, handler.Requests.Single().Id);
        Assert.Single(result.Profiles); Assert.Equal(0, result.RejectedCount);
    }

    [Fact]
    public async Task NewRedirectProviderNeedsItsOwnExplicitPermission()
    {
        var state = new AppState { SendSubscriptionHwid = true }; var sub = new ProxySubscription { Url = "https://first.test/sub" };
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.Host == "first.test")
            { var response = Response(status: HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://other.test/sub"); return response; }
            return Response("x-hwid-not-supported", HttpStatusCode.Forbidden);
        });
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<SubscriptionHwidException>(() => SubscriptionService.FetchAsync(client, sub.Url, identity: Context(state, sub)));
        Assert.Equal("https://other.test", error.Origin); Assert.Equal("required", error.Code);
        Assert.Equal(2, handler.Requests.Count); Assert.Null(handler.Requests[1].Id); Assert.Empty(sub.HwidApprovedOrigins);
    }

    [Fact]
    public async Task ServerReasonPhraseCannotExposeHwidThroughAnException()
    {
        var sub = new ProxySubscription { Url = "https://example.test/sub", HwidMode = SubscriptionHwidMode.Allow, HwidOverride = "existing-happ-12345" };
        sub.HwidApprovedOrigins.Add("https://example.test");
        var handler = new Handler(_ => { var response = Response(status: HttpStatusCode.Forbidden); response.ReasonPhrase = sub.HwidOverride; return response; });
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => SubscriptionService.FetchAsync(client, sub.Url, identity: Context(new AppState(), sub)));
        Assert.DoesNotContain(sub.HwidOverride, error.Message);
    }

    [Fact]
    public async Task InformationalActiveHeaderDoesNotForcePermissionForAWorkingSubscription()
    {
        var handler = new Handler(_ => Response("x-hwid-active")); using var client = new HttpClient(handler);
        var result = await SubscriptionService.FetchAsync(client, "https://example.test/sub");
        Assert.Single(result.Profiles); Assert.False(result.RequiresHwid); Assert.Null(handler.Requests.Single().Id);
    }

    [Fact]
    public async Task ApprovingRedirectDestinationDoesNotAlsoAuthorizeTheOriginalHost()
    {
        var sub = new ProxySubscription { Url = "https://first.test/sub", HwidMode = SubscriptionHwidMode.Allow, HwidApprovedOrigins = ["https://second.test"] };
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.Host == "first.test")
            { var response = Response(status: HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://second.test/sub"); return response; }
            return Response();
        });
        using var client = new HttpClient(handler);
        await SubscriptionService.FetchAsync(client, sub.Url, identity: Context(new AppState(), sub));
        Assert.Null(handler.Requests[0].Id); Assert.NotNull(handler.Requests[1].Id);
    }
}
