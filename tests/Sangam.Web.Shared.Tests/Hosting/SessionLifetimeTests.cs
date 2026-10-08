using System.Globalization;
using System.Security.Claims;
using Sangam.Web.Shared.Hosting;

namespace Sangam.Web.Shared.Tests.Hosting;

public sealed class SessionLifetimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ASession_EndsTwelveHoursAfterSignIn_HoweverBusy()
    {
        // R7 (ASVS V3.3.2).
        Assert.False(SessionLifetime.Expired(Since(Now.AddHours(-11)), Now));
        Assert.True(SessionLifetime.Expired(Since(Now.AddHours(-12).AddMinutes(-1)), Now));
    }

    [Fact]
    public void ASession_WithNoStartTime_IsEnded()
    {
        Assert.True(SessionLifetime.Expired(new ClaimsPrincipal(new ClaimsIdentity("test")), Now));
        Assert.True(SessionLifetime.Expired(null, Now));
    }

    private static ClaimsPrincipal Since(DateTimeOffset when)
        => new(new ClaimsIdentity([new Claim(SessionLifetime.SinceClaim, when.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))], "test"));
}
