using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain.Tests;

/// <summary>PR-17: methods (RFC 8176), the level they reach, and what acr_values require.</summary>
public sealed class AuthenticationAssuranceTests
{
    [Theory]
    [InlineData(SignInMode.Password, false, false, "pwd", AuthenticationAssurance.Acr1)]
    [InlineData(SignInMode.Password, true, false, "pwd otp mfa", AuthenticationAssurance.Acr2)]
    [InlineData(SignInMode.PasswordAndOtp, false, false, "pwd otp mfa", AuthenticationAssurance.Acr2)]
    [InlineData(SignInMode.PasswordAndOtp, false, true, "pwd sms mfa", AuthenticationAssurance.Acr2)]
    [InlineData(SignInMode.OtpOnly, false, false, "otp", AuthenticationAssurance.Acr1)]
    [InlineData(SignInMode.OtpOnly, true, false, "otp mfa", AuthenticationAssurance.Acr2)]
    [InlineData(SignInMode.Passkey, false, false, "pop mfa", AuthenticationAssurance.Acr3)]
    public void EachSignIn_RecordsItsMethods_AndReachesItsLevel(SignInMode mode, bool authenticator, bool sms, string methods, string acr)
    {
        IReadOnlyList<string> amr = AuthenticationAssurance.Methods(mode, authenticator, sms);
        Assert.Equal(methods, string.Join(' ', amr));
        Assert.Equal(acr, AuthenticationAssurance.AcrFor(amr));
    }

    [Fact]
    public void AcrValues_RequireTheLowestKnownLevel_AndSignatureMeansFresh()
    {
        Assert.Equal((0, false), AuthenticationAssurance.Required(null));
        Assert.Equal((0, false), AuthenticationAssurance.Required(["urn:other:gold"]));
        Assert.Equal((2, false), AuthenticationAssurance.Required([AuthenticationAssurance.Acr3, AuthenticationAssurance.Acr2]));
        Assert.Equal((3, false), AuthenticationAssurance.Required([AuthenticationAssurance.Acr3, "urn:other:gold"]));
        Assert.Equal((2, true), AuthenticationAssurance.Required([AuthenticationAssurance.AcrSign]));
        Assert.Equal((1, false), AuthenticationAssurance.Required([AuthenticationAssurance.Acr1, AuthenticationAssurance.AcrSign]));
        Assert.Equal(TimeSpan.FromMinutes(5), AuthenticationAssurance.SignatureFreshness);
    }
}
