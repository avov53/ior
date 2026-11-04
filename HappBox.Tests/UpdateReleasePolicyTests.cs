using System.Text.Json;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class UpdateReleasePolicyTests
{
    [Fact] public void PackagedInstallerHasUtf8SignatureForWindowsPowerShell(){using var script=typeof(MainForm).Assembly.GetManifestResourceStream("HappBox.Install-Update.ps1");Assert.NotNull(script);Assert.Equal(0xef,script!.ReadByte());Assert.Equal(0xbb,script.ReadByte());Assert.Equal(0xbf,script.ReadByte());}
    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    public void UnpublishedOrPreviewReleasesAreNotOffered(string flag){using var json=System.Text.Json.JsonDocument.Parse("{\"tag_name\":\"v99.0.0\",\""+flag+"\":true}");Assert.Null(UpdateReleasePolicy.Parse(json.RootElement,"https://example.test/release",false));}
    [Fact]
    public void InstalledVersionMatchesAssemblyAndCurrentReleaseDoesNotOfferUpdate()
    {
        var installed = UpdateReleasePolicy.InstalledVersion(typeof(MainForm).Assembly);
        var assembly = typeof(MainForm).Assembly.GetName().Version!;
        Assert.Equal($"{assembly.Major}.{assembly.Minor}.{assembly.Build}", installed);
        Assert.False(UpdateReleasePolicy.IsNewer("v" + installed, installed));
        Assert.False(UpdateReleasePolicy.IsNewer("v" + installed + ".0", installed));
        Assert.True(UpdateReleasePolicy.IsNewer($"v{assembly.Major}.{assembly.Minor}.{assembly.Build + 1}", installed));
    }

    [Theory]
    [InlineData("v1.2.4", "1.2.4+commit", "1.2.3", true)]
    [InlineData("v1.2.4", "1.2.3", "1.2.3", false)]
    [InlineData("v1.2.4", "1.2.5", "1.2.3", false)]
    [InlineData("v1.2.3", "1.2.3", "1.2.3", false)]
    public void ArchiveMustMatchReleaseAndBeNewer(string release, string downloaded, string installed, bool accepted) =>
        Assert.Equal(accepted, UpdateReleasePolicy.CanInstall(release, downloaded, installed));
    [Fact]
    public void ParsesNewerReleaseAndSelectsMatchingWindowsArchive()
    {
        using var json = JsonDocument.Parse("""
        {"tag_name":"v1.3.0","html_url":"https://github.com/example/ior/releases/tag/v1.3.0","assets":[
          {"name":"ior-windows-10.zip","browser_download_url":"https://example.test/win10.zip"},
          {"name":"ior-windows-11.zip","browser_download_url":"https://example.test/win11.zip"}]}
        """);
        var release = UpdateReleasePolicy.Parse(json.RootElement, "https://github.com/example/ior/releases/latest", windows10: false);
        Assert.NotNull(release);
        Assert.Equal("v1.3.0", release.Tag);
        Assert.Equal("ior-windows-11.zip", release.AssetName);
        Assert.Equal("https://example.test/win11.zip", release.AssetDownloadUrl);
        var win10 = UpdateReleasePolicy.Parse(json.RootElement, "https://github.com/example/ior/releases/latest", windows10: true);
        Assert.Equal("ior-windows-10.zip", win10?.AssetName);
        Assert.True(UpdateReleasePolicy.IsNewer(release.Tag, "1.2.2"));
        Assert.True(UpdateReleasePolicy.IsNewer("v1.2.4", "1.2.3"));
        Assert.False(UpdateReleasePolicy.IsNewer("v1.2.2", "1.2.2"));
    }

    [Fact]
    public void MissingOrUnsafeAssetsDoNotBecomeDownloadTargets()
    {
        using var json = JsonDocument.Parse("""
        {"tag_name":"v2.0.0","html_url":"http://example.test/release","assets":[
          {"name":"ior-windows-11.zip","browser_download_url":"http://example.test/file.zip"}]}
        """);
        var release = UpdateReleasePolicy.Parse(json.RootElement, "https://github.com/example/ior/releases/latest", windows10: false);
        Assert.NotNull(release);
        Assert.Null(release.AssetName);
        Assert.Null(release.AssetDownloadUrl);
        Assert.Equal("https://github.com/example/ior/releases/latest", release.ReleaseUrl);
    }

    [Fact]
    public void MissingReleaseTagIsIgnored()
    {
        using var json = JsonDocument.Parse("{\"assets\":[]}");
        Assert.Null(UpdateReleasePolicy.Parse(json.RootElement, "https://github.com/example/ior/releases/latest", windows10: false));
    }

    [Fact]
    public void UnknownContentLengthDoesNotInventProgressAndKnownLengthIsBounded()
    {
        Assert.Null(UpdateReleasePolicy.ProgressPercent(4096, null));
        Assert.Null(UpdateReleasePolicy.ProgressPercent(4096, 0));
        Assert.Equal(50, UpdateReleasePolicy.ProgressPercent(50, 100));
        Assert.Equal(100, UpdateReleasePolicy.ProgressPercent(200, 100));
    }
}
