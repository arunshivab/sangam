namespace Sangam.Client.Tests;

/// <summary>rc.5 (ASVS V3.4.4): the sign-in cookie's default name.</summary>
public sealed class SangamOptionsTests
{
    [Fact]
    public void TheDefaultCookie_IsHostPrefixedOverHttps_AndAnApplicationsOwnNameIsKept()
    {
        Assert.Equal("__Host-sangam.app", new SangamOptions { RequireHttpsMetadata = true }.EffectiveCookieName);
        Assert.Equal("sangam.app", new SangamOptions { RequireHttpsMetadata = false }.EffectiveCookieName);
        Assert.Equal("hospital.session", new SangamOptions { CookieName = "hospital.session" }.EffectiveCookieName);
    }
}
