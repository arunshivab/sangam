using Sangam.Identity.Application.Security;

namespace Sangam.Identity.Application.Tests.Security;

public sealed class PasswordStrengthTests
{
    [Fact]
    public void Empty_IsWeakWithNoSegments()
    {
        PasswordStrengthResult r = PasswordStrength.Evaluate(null);
        Assert.Equal(PasswordVerdict.Weak, r.Verdict);
        Assert.Equal(0, r.Segments);
        Assert.False(r.MeetsPolicy);
    }

    [Theory]
    [InlineData("Ab1!")]
    [InlineData("Kaveri#7")]
    [InlineData("Abcdefghij1")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    [InlineData("q1w2e3r4t5y6")]
    [InlineData("Password@123")]
    [InlineData("WELCOME@12345")]
    public void BelowPolicy_IsWeak(string password)
    {
        // rc.5: shorter than 12, a single repeated character, or one of the most common passwords.
        PasswordStrengthResult r = PasswordStrength.Evaluate(password);
        Assert.Equal(PasswordVerdict.Weak, r.Verdict);
        Assert.Equal(1, r.Segments);
        Assert.False(r.MeetsPolicy);
    }

    [Theory]
    [InlineData("monsoon tea baner")]
    [InlineData("kaveririverbank")]
    [InlineData("KAVERIRIVERBANK")]
    [InlineData("312983746501")]
    public void TwelveCharacters_WithoutCharacterTypes_AreAccepted(string password)
    {
        // rc.5 (ASVS V2.1.9): no composition rules by default.
        PasswordStrengthResult r = PasswordStrength.Evaluate(password);
        Assert.True(r.MeetsPolicy);
        Assert.NotEqual(PasswordVerdict.Weak, r.Verdict);
        Assert.Single(r.Tokens);
        Assert.Equal(("length", true), r.Tokens[0]);
    }

    [Fact]
    public void TwelveCharacters_IsFair_SixteenIsStrong()
    {
        PasswordStrengthResult fair = PasswordStrength.Evaluate("riverbankpune");
        Assert.Equal(PasswordVerdict.Fair, fair.Verdict);
        Assert.Equal(2, fair.Segments);

        Assert.Equal(3, PasswordStrength.Evaluate("riverbank-pune1").Segments);

        PasswordStrengthResult strong = PasswordStrength.Evaluate("correct horse battery");
        Assert.Equal(PasswordVerdict.Strong, strong.Verdict);
        Assert.Equal(4, strong.Segments);
    }

    [Fact]
    public void SixtyFourCharacters_AreAccepted_ButMoreThan128_AreRefused()
    {
        // R7 (ASVS V2.1.2).
        string sixtyFour = "Kaveri-River-1!" + new string('x', 49);
        Assert.Equal(64, sixtyFour.Length);
        Assert.True(PasswordStrength.Evaluate(sixtyFour).MeetsPolicy);
        Assert.True(PasswordStrength.Evaluate(sixtyFour + new string('y', 64)).MeetsPolicy);
        PasswordStrengthResult tooLong = PasswordStrength.Evaluate(sixtyFour + new string('y', 65));
        Assert.False(tooLong.MeetsPolicy);
        Assert.False(tooLong.IsNotTooLong);
    }

    [Fact]
    public void WhereAPolicyRequiresCharacterTypes_AllFourAreNeeded_AndShown()
    {
        // rc.5: an organisation's or application's own choice.
        PasswordStrengthResult plain = PasswordStrength.Evaluate("monsoon tea baner", 12, requireCharacterTypes: true);
        Assert.False(plain.MeetsPolicy);
        Assert.Equal(5, plain.Tokens.Count);
        Assert.Equal(["length", "upper", "lower", "number", "symbol"], plain.Tokens.Select(t => t.Code));

        PasswordStrengthResult typed = PasswordStrength.Evaluate("Monsoon tea, baner 7", 12, requireCharacterTypes: true);
        Assert.True(typed.MeetsPolicy);
        Assert.True(typed.HasAllClasses);
    }

    [Fact]
    public void ALongerPolicyMinimum_IsApplied_ButNeverBelowTwelve()
    {
        Assert.False(PasswordStrength.Evaluate("riverbankpune", 15, requireCharacterTypes: false).MeetsPolicy);
        Assert.True(PasswordStrength.Evaluate("riverbank in pune", 15, requireCharacterTypes: false).MeetsPolicy);
        PasswordStrengthResult floor = PasswordStrength.Evaluate("riverbank1", 8, requireCharacterTypes: false);
        Assert.False(floor.MeetsPolicy);
        Assert.Equal(PasswordStrength.MinimumLength, floor.MinimumLength);
    }

    [Fact]
    public void TheBuiltInList_HoldsAboutTenThousandCommonPasswords_IgnoringCase()
    {
        // rc.5 (ASVS V2.1.7): the fallback when the online service does not answer.
        Assert.InRange(PasswordStrength.CommonPasswordCount, 10_000, 10_100);
        Assert.True(PasswordStrength.IsCommon("qwerty123456"));
        Assert.True(PasswordStrength.IsCommon("QWERTY123456"));
        Assert.True(PasswordStrength.IsCommon("India@123456"));
        Assert.False(PasswordStrength.IsCommon("monsoon tea at baner"));
    }
}
