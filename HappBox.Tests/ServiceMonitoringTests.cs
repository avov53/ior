using System.Net;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class ServiceMonitoringTests
{
    [Fact]
    public void OldStateGetsThreeDefaultTargets()
    {
        var state = new AppState();

        ServiceMonitorCatalog.NormalizeTargets(state);

        Assert.Equal(3, state.ServiceMonitorTargets.Count);
        Assert.Equal(["Discord", "YouTube", "Telegram"], state.ServiceMonitorTargets.Select(x => x.Name));
        Assert.Equal(3, state.ServiceMonitorTargets.Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public void HiddenTargetRemainsInListAfterNormalization()
    {
        var state = new AppState
        {
            ServiceMonitorTargets =
            [
                new ServiceMonitorTarget
                {
                    Name = "YouTube",
                    Url = "https://www.youtube.com",
                    PresetId = "youtube",
                    Visible = false
                }
            ]
        };

        ServiceMonitorCatalog.NormalizeTargets(state);

        var target = Assert.Single(state.ServiceMonitorTargets);
        Assert.False(target.Visible);
        Assert.Equal("youtube", target.PresetId);
    }

    [Theory]
    [InlineData("example.com", "https://example.com/", "Example")]
    [InlineData("http://status.example.com/path", "http://status.example.com/path", "Status")]
    public void CustomTargetNormalizesUrlAndName(string input, string expectedUrl, string expectedName)
    {
        Assert.True(ServiceMonitorCatalog.TryCreateCustom(input, out var target));
        Assert.Equal(expectedUrl.TrimEnd('/'), target.Url);
        Assert.Equal(expectedName, target.Name);
    }

    [Theory]
    [InlineData("file:///c:/secret.txt")]
    [InlineData("ftp://example.com")]
    [InlineData("https://user:password@example.com")]
    [InlineData("")]
    public void CustomTargetRejectsUnsupportedAddress(string input)
    {
        Assert.False(ServiceMonitorCatalog.TryCreateCustom(input, out _));
    }

    [Fact]
    public async Task FaviconIsDownloadedOnceAndThenReadFromCache()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var requests = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) };
        }));
        var directory = Path.Combine(Path.GetTempPath(), "ior-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new ServiceFaviconCache(directory, client);
            Assert.Equal(png, await cache.GetAsync("https://example.com/page"));
            Assert.Equal(png, await cache.GetAsync("https://example.com/other"));
            Assert.Equal(1, requests);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FaviconRejectsOversizedResponse()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[ServiceFaviconCache.MaximumBytes + 1])
        }));
        var directory = Path.Combine(Path.GetTempPath(), "ior-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new ServiceFaviconCache(directory, client);
            Assert.Null(await cache.GetAsync("https://example.com"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FaviconFallsBackToIconDeclaredByPage()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var requests = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests++;
            return request.RequestUri!.AbsolutePath switch
            {
                "/favicon.ico" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/assets/service.png" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) },
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><head><link rel=\"icon\" href=\"/assets/service.png\"></head></html>")
                }
            };
        }));
        var directory = Path.Combine(Path.GetTempPath(), "ior-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new ServiceFaviconCache(directory, client);
            Assert.Equal(png, await cache.GetAsync("https://discord.example/home"));
            Assert.Equal(3, requests);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SecondCoordinatorSignalsPrimaryInstance()
    {
        using var primary = new SingleInstanceCoordinator();
        Assert.True(primary.IsPrimary);
        using var activated = new ManualResetEventSlim();
        primary.Listen(activated.Set);

        using var secondary = new SingleInstanceCoordinator();
        Assert.False(secondary.IsPrimary);
        secondary.SignalPrimary();

        Assert.True(activated.Wait(TimeSpan.FromSeconds(2)));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
