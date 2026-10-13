namespace Sangam.Identity.Domain.Tests;

/// <summary>rc.11: an email, a mobile number and a login ID are told apart, and a login ID's rules hold.</summary>
public sealed class SignInIdentifierTests
{
    [Theory]
    [InlineData("devi@example.in", SignInIdentifierKind.Email)]
    [InlineData("  Devi.Nair@Example.IN ", SignInIdentifierKind.Email)]
    [InlineData("98765 43210", SignInIdentifierKind.Mobile)]
    [InlineData("+91 98765-43210", SignInIdentifierKind.Mobile)]
    [InlineData("(0)98765 43210", SignInIdentifierKind.Mobile)]
    [InlineData("devi.nair", SignInIdentifierKind.LoginId)]
    [InlineData("Dr_Arun2", SignInIdentifierKind.LoginId)]
    [InlineData("", SignInIdentifierKind.Invalid)]
    [InlineData("12345", SignInIdentifierKind.Invalid)]
    [InlineData("9+8765432101", SignInIdentifierKind.Invalid)]
    [InlineData("a@b@c", SignInIdentifierKind.Invalid)]
    [InlineData("@example.in", SignInIdentifierKind.Invalid)]
    [InlineData("devi nair", SignInIdentifierKind.Invalid)]
    [InlineData("2devi", SignInIdentifierKind.Invalid)]
    public void Classify_TellsTheThreeApart(string typed, SignInIdentifierKind expected)
    {
        Assert.Equal(expected, SignInIdentifiers.Classify(typed));
    }

    [Theory]
    [InlineData("devi.nair", null)]
    [InlineData("drarun", null)]
    [InlineData("a_b.c1", null)]
    [InlineData("abc", LoginIds.ProblemLength)]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcde", LoginIds.ProblemLength)]
    [InlineData("devi-nair", LoginIds.ProblemCharacters)]
    [InlineData("देवी.nair", LoginIds.ProblemCharacters)]
    [InlineData("1devi", LoginIds.ProblemStart)]
    [InlineData(".devi", LoginIds.ProblemStart)]
    [InlineData("devi.", LoginIds.ProblemPunctuation)]
    [InlineData("devi..nair", LoginIds.ProblemPunctuation)]
    [InlineData("devi._nair", LoginIds.ProblemPunctuation)]
    [InlineData("support", LoginIds.ProblemReserved)]
    [InlineData("sangam.id", LoginIds.ProblemReserved)]
    public void Problem_FollowsTheRules(string normalised, string? expected)
    {
        Assert.Equal(expected, LoginIds.Problem(normalised));
    }

    [Theory]
    [InlineData("Devi", "Nair", "x@example.in", "devi.nair")]
    [InlineData("José", "D'Souza", "x@example.in", "jose.dsouza")]
    [InlineData("Arun", "", "x@example.in", "arun")]
    [InlineData("देवी", "नायर", "devi.n-1989@example.in", "devi.n.1989")]
    [InlineData("अ", "", "42@example.in", "")]
    public void Suggest_IsFirstDotLast_OrTheEmailsName(string first, string last, string email, string expected)
    {
        Assert.Equal(expected, LoginIds.Suggest(first, last, email));
    }

    [Fact]
    public void Numbered_StaysWithinTheLength()
    {
        Assert.Equal("devi.nair2", LoginIds.Numbered("devi.nair", 2));
        string longest = new('a', LoginIds.MaxLength);
        Assert.Equal(LoginIds.MaxLength, LoginIds.Numbered(longest, 12).Length);
        Assert.EndsWith("12", LoginIds.Numbered(longest, 12), StringComparison.Ordinal);
    }
}
