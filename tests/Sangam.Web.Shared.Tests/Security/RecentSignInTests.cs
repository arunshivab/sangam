using System.Globalization;
using System.Security.Claims;
using Sangam.Web.Shared.Security;

namespace Sangam.Web.Shared.Tests.Security;

/// <summary>rc.9 (ASVS V3.7.1): the 15-minute fresh-sign-in rule and the safe return address.</summary>
public sealed class RecentSignInTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    private static ClaimsPrincipal SignedIn(DateTimeOffset? at)
    {
        ClaimsIdentity identity = new("test");
        if (at is DateTimeOffset t)
        {
            identity.AddClaim(new Claim(RecentSignIn.Claim, t.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        }

        return new ClaimsPrincipal(identity);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(14, true)]
    [InlineData(15, true)]
    [InlineData(16, false)]
    [InlineData(600, false)]
    [InlineData(-5, false)]
    public void ASignIn_IsRecent_OnlyWithinFifteenMinutes(int minutesAgo, bool recent)
        => Assert.Equal(recent, RecentSignIn.IsRecent(SignedIn(Now.AddMinutes(-minutesAgo)), Now));

    [Fact]
    public void NoAuthenticationTime_IsNeverRecent()
    {
        Assert.False(RecentSignIn.IsRecent(SignedIn(null), Now));
        Assert.Null(RecentSignIn.At(SignedIn(null)));
    }

    [Theory]
    [InlineData("/profile", "/profile")]
    [InlineData("/devices?x=1", "/devices?x=1")]
    [InlineData("/", "/")]
    [InlineData("//evil.example", "/fallback")]
    [InlineData("/\\evil.example", "/fallback")]
    [InlineData("https://evil.example/", "/fallback")]
    [InlineData("", "/fallback")]
    [InlineData(null, "/fallback")]
    public void TheReturnAddress_StaysOnThisSite(string? asked, string expected)
        => Assert.Equal(expected, RecentSignIn.LocalReturn(asked, "/fallback"));
}
