using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain.Tests;

/// <summary>PR-16: policies combine level by level, and a lower level can only make them stricter.</summary>
public sealed class SecurityPolicyTests
{
    private static readonly SecurityPolicy Platform = new(SignInPolicy.Default, 8, MfaRequirement.Optional, false);

    [Fact]
    public void SignInRules_AreRankedFromWeakestToStrictest()
    {
        Assert.True(SecurityPolicy.Rank(SignInPolicy.Password) < SecurityPolicy.Rank(SignInPolicy.Default));
        Assert.Equal(SecurityPolicy.Rank(SignInPolicy.Password), SecurityPolicy.Rank(SignInPolicy.OtpOnly));
        Assert.True(SecurityPolicy.Rank(SignInPolicy.Default) < SecurityPolicy.Rank(SignInPolicy.PasswordAndOtp));
        Assert.True(SecurityPolicy.Rank(SignInPolicy.PasswordAndOtp) < SecurityPolicy.Rank(SignInPolicy.PasskeyOnly));
    }

    [Fact]
    public void Tightening_TakesTheStricterSetting_AndIgnoresAWeakerOne()
    {
        SecurityPolicy strict = Platform.Tighten(SignInPolicy.PasskeyOnly, 14, MfaRequirement.Required, true);
        Assert.Equal(new SecurityPolicy(SignInPolicy.PasskeyOnly, 14, MfaRequirement.Required, true), strict);

        SecurityPolicy unchanged = strict.Tighten(SignInPolicy.PasswordAndOtp, 10, MfaRequirement.Optional, false);
        Assert.Equal(strict, unchanged);
        Assert.Equal(strict, strict.Tighten(null, null, null, null));
        Assert.Equal(SecurityPolicy.MaxMinPasswordLength, Platform.Tighten(null, 500, null, null).MinPasswordLength);
    }

    [Fact]
    public void TwoPoliciesThatBothApply_GiveTheStricterOfEach()
    {
        SecurityPolicy a = Platform.Tighten(SignInPolicy.PasswordAndOtp, 12, null, null);
        SecurityPolicy b = Platform.Tighten(null, 10, MfaRequirement.RequiredForAdministrators, true);
        Assert.Equal(new SecurityPolicy(SignInPolicy.PasswordAndOtp, 12, MfaRequirement.RequiredForAdministrators, true), a.Strictest(b));
    }

    [Fact]
    public void SecondFactor_IsRequired_ForEveryone_OrForAdministratorsOnly()
    {
        Assert.False(Platform.RequiresSecondFactor(isAdministrator: true));
        SecurityPolicy admins = Platform with { Mfa = MfaRequirement.RequiredForAdministrators };
        Assert.True(admins.RequiresSecondFactor(isAdministrator: true));
        Assert.False(admins.RequiresSecondFactor(isAdministrator: false));
        Assert.True((Platform with { Mfa = MfaRequirement.Required }).RequiresSecondFactor(isAdministrator: false));
    }

    [Fact]
    public void ALowerLevel_CannotWeakenWhatItInherits()
    {
        SecurityPolicy inherited = Platform.Tighten(SignInPolicy.PasswordAndOtp, 12, MfaRequirement.RequiredForAdministrators, true);
        Assert.Null(inherited.WhyWeaker(SignInPolicy.PasskeyOnly, 16, MfaRequirement.Required, true));
        Assert.Null(inherited.WhyWeaker(null, null, null, null));
        Assert.NotNull(inherited.WhyWeaker(SignInPolicy.Default, null, null, null));
        Assert.NotNull(inherited.WhyWeaker(null, 10, null, null));
        Assert.NotNull(inherited.WhyWeaker(null, 65, null, null));
        Assert.NotNull(inherited.WhyWeaker(null, null, MfaRequirement.Optional, null));
        Assert.NotNull(inherited.WhyWeaker(null, null, null, false));
    }

    [Fact]
    public void CharacterTypes_AreOffByDefault_TightenOnly_AndCannotBeSwitchedOffBelow()
    {
        // rc.5: an organisation's or application's own choice, never the platform default (ASVS V2.1.9).
        Assert.False(Platform.RequireCharacterTypes);
        SecurityPolicy org = Platform.Tighten(null, null, null, null, requireCharacterTypes: true);
        Assert.True(org.RequireCharacterTypes);
        Assert.True(org.Tighten(null, null, null, null, requireCharacterTypes: false).RequireCharacterTypes);
        Assert.True(Platform.Strictest(org).RequireCharacterTypes);
        Assert.NotNull(org.WhyWeaker(null, null, null, null, requireCharacterTypes: false));
        Assert.Null(org.WhyWeaker(null, null, null, null, requireCharacterTypes: true));
        Assert.Null(Platform.WhyWeaker(null, null, null, null, requireCharacterTypes: false));
    }

    [Fact]
    public void APasskeyOnlyRule_MeansAPasskeySignIn()
        => Assert.Equal(SignInMode.Passkey, SignInModes.Resolve(SignInPolicy.PasskeyOnly, SignInMode.Password));
}
