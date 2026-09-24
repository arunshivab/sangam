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
    [InlineData("Abcdef1")]
    [InlineData("abcdefgh1!")]
    [InlineData("ABCDEFGH1!")]
    [InlineData("Abcdefghij!")]
    [InlineData("Abcdefghij1")]
    [InlineData("Password1!")]
    [InlineData("Admin@123")]
    public void BelowPolicy_IsWeak(string password)
    {
        PasswordStrengthResult r = PasswordStrength.Evaluate(password);
        Assert.Equal(PasswordVerdict.Weak, r.Verdict);
        Assert.Equal(1, r.Segments);
        Assert.False(r.MeetsPolicy);
    }

    [Fact]
    public void EightCharsAllClasses_IsFairAndAccepted()
    {
        PasswordStrengthResult r = PasswordStrength.Evaluate("Kaveri#7");
        Assert.Equal(PasswordVerdict.Fair, r.Verdict);
        Assert.Equal(2, r.Segments);
        Assert.True(r.MeetsPolicy);
        Assert.True(r.HasMinimumLength);
        Assert.True(r.HasAllClasses);
        Assert.True(r.IsNotBlocklisted);
    }

    [Fact]
    public void FourteenPlusAllClasses_IsStrong()
    {
        PasswordStrengthResult r = PasswordStrength.Evaluate("Correct-Horse-2026!");
        Assert.Equal(PasswordVerdict.Strong, r.Verdict);
        Assert.Equal(4, r.Segments);
    }

    [Fact]
    public void RequirementRows_ReportEachRuleSeparately()
    {
        PasswordStrengthResult r = PasswordStrength.Evaluate("abcdefghij");
        Assert.True(r.HasMinimumLength);
        Assert.False(r.HasAllClasses);
        Assert.True(r.IsNotBlocklisted);
        Assert.False(r.MeetsPolicy);
    }
}
