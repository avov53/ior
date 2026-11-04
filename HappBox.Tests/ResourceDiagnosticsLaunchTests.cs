using HappBox;
using Xunit;

public sealed class ResourceDiagnosticsLaunchTests
{
    [Fact]
    public void NormalLaunchDoesNotEnableResourceCapture()=>Assert.Null(Program.ResourceDiagnosticsDomains([]));
    [Fact]
    public void ExplicitArgumentNormalizesBoundedDomains()=>Assert.Equal("youtube.com,googlevideo.com",Program.ResourceDiagnosticsDomains(["--resource-diagnostics=YouTube.com.,googlevideo.com"]));
    [Theory]
    [InlineData("https://example.com/private?token=secret")]
    [InlineData("1.2.3.4")]
    [InlineData("")]
    [InlineData("a.test,b.test,c.test,d.test,e.test,f.test,g.test,h.test,i.test")]
    public void InvalidCaptureArgumentsAreIgnored(string value)=>Assert.Null(Program.ResourceDiagnosticsDomains(["--resource-diagnostics="+value]));
}
