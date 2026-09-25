using Sangam.Identity.Infrastructure.Portal;

namespace Sangam.Identity.Infrastructure.Tests.Portal;

public sealed class UserAgentSummaryTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36", "Chrome on Windows")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15", "Safari on macOS")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 Version/17.0 Mobile/15E148 Safari/604.1", "Safari on iOS")]
    [InlineData("Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 Chrome/131.0.0.0 Mobile Safari/537.36", "Chrome on Android")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0", "Edge on Windows")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0", "Firefox on Linux")]
    public void Describe_RecognisesTheCommonFamilies(string userAgent, string expected)
    {
        Assert.Equal(expected, UserAgentSummary.Describe(userAgent));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("curl/8.5.0")]
    public void Describe_SaysUnknownRatherThanGuessing(string? userAgent)
    {
        Assert.StartsWith("Unknown browser", UserAgentSummary.Describe(userAgent), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_PrefersEdgeAndOperaOverTheChromeTokenTheyCarry()
    {
        Assert.Equal("Opera on Windows", UserAgentSummary.Describe("Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36 OPR/116.0.0.0"));
        Assert.Equal("Samsung Internet on Android", UserAgentSummary.Describe("Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 SamsungBrowser/23.0 Chrome/115.0.0.0 Mobile Safari/537.36"));
    }
}
