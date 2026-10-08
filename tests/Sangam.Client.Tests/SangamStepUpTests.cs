using System.Security.Claims;
using Sangam.Shared.Constants;

namespace Sangam.Client.Tests;

/// <summary>PR-17: the client library's step-up checks and the RFC 9470 challenge.</summary>
public sealed class SangamStepUpTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ALevel_IsMetByItself_OrAStrongerOne_AndFreshnessIsChecked()
    {
        ClaimsPrincipal twoFactor = Person(SangamAcr.TwoFactor, Now.AddMinutes(-3));
        Assert.True(twoFactor.Satisfies(SangamAcr.SingleFactor, now: Now));
        Assert.True(twoFactor.Satisfies(SangamAcr.TwoFactor, now: Now));
        Assert.False(twoFactor.Satisfies(SangamAcr.PhishingResistant, now: Now));
        Assert.True(twoFactor.Satisfies(SangamAcr.TwoFactor, TimeSpan.FromMinutes(5), Now));
        Assert.False(twoFactor.Satisfies(SangamAcr.TwoFactor, TimeSpan.FromMinutes(1), Now));
        Assert.True(twoFactor.Satisfies(SangamAcr.Signature, now: Now));
        Assert.False(Person(SangamAcr.TwoFactor, Now.AddMinutes(-6)).Satisfies(SangamAcr.Signature, now: Now));
        Assert.False(Person(SangamAcr.SingleFactor, Now).Satisfies(SangamAcr.Signature, now: Now));
        Assert.False(new ClaimsPrincipal(new ClaimsIdentity()).Satisfies(SangamAcr.SingleFactor, now: Now));
    }

    [Fact]
    public void TheApiChallenge_FollowsRfc9470()
    {
        Assert.Equal(
            "Bearer error=\"insufficient_user_authentication\", error_description=\"A stronger or more recent authentication is required\", acr_values=\"urn:sangam:acr:2\", max_age=300",
            SangamStepUp.Challenge(SangamAcr.TwoFactor, TimeSpan.FromMinutes(5)));
    }

    private static ClaimsPrincipal Person(string acr, DateTimeOffset at) => new(new ClaimsIdentity(
    [
        new Claim("acr", acr),
        new Claim("auth_time", at.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
    ], "test"));
}
