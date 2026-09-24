using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain.Tests;

public sealed class SignInModesTests
{
    [Theory]
    [InlineData(null, SignInMode.Password, SignInMode.Password)]
    [InlineData(null, SignInMode.OtpOnly, SignInMode.OtpOnly)]
    [InlineData(SignInPolicy.Default, SignInMode.PasswordAndOtp, SignInMode.PasswordAndOtp)]
    [InlineData(SignInPolicy.Password, SignInMode.OtpOnly, SignInMode.Password)]
    [InlineData(SignInPolicy.PasswordAndOtp, SignInMode.Password, SignInMode.PasswordAndOtp)]
    [InlineData(SignInPolicy.OtpOnly, SignInMode.Password, SignInMode.OtpOnly)]
    public void Resolve_AppRuleWins_OtherwiseUserPreference(SignInPolicy? policy, SignInMode preference, SignInMode expected)
    {
        Assert.Equal(expected, SignInModes.Resolve(policy, preference));
    }
}
