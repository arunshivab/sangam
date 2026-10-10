using Sangam.Identity.Application.Verification;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Tests.Verification;

/// <summary>rc.6 (SGM-914 section 6): the rule for recovering an account that was never verified.</summary>
public sealed class RecoveryMatchTests
{
    private static readonly DateOnly Born = new(1979, 6, 3);

    [Theory]
    [InlineData("Arun", "Balasubramanian", "Arun Shiva Balasubramanian")]
    [InlineData("Arun Shiva", "B", "Arun Shiva Balasubramanian")]
    [InlineData("arun", "BALASUBRAMANIAN", "  Arun   Shiva  Balasubramanian ")]
    [InlineData("Balasubramanian", "Arun", "Arun Shiva Balasubramanian")]
    [InlineData("A. S.", "Balasubramanian", "Arun Shiva Balasubramanian")]
    [InlineData("Mary-Ann", "D'Souza", "Mary Ann D Souza")]
    public void AMissingMiddleName_SwappedNames_InitialsAndSpacing_StillMatch(string first, string last, string record)
    {
        RecoveryMatchResult result = RecoveryMatch.Compare(first, last, Born, Gender.Male, new VerifiedIdentity("digilocker", "x", record, Born, Gender.Male));
        Assert.True(result.Matches);
        Assert.Empty(result.Mismatches);
    }

    [Theory]
    [InlineData("Arun", "Kumar", "Arun Shiva Balasubramanian")]
    [InlineData("Arun", "Arun", "Arun Shiva Balasubramanian")]
    [InlineData("Arjun", "Balasubramanian", "Arun Shiva Balasubramanian")]
    [InlineData("K", "Balasubramanian", "Arun Shiva Balasubramanian")]
    [InlineData("", "", "Arun Shiva Balasubramanian")]
    public void AWordNotInTheRecord_OrUsedTwice_DoesNotMatch(string first, string last, string record)
    {
        RecoveryMatchResult result = RecoveryMatch.Compare(first, last, Born, Gender.Male, new VerifiedIdentity("digilocker", "x", record, Born, Gender.Male));
        Assert.False(result.Matches);
        Assert.Equal([RecoveryMatch.Name], result.Mismatches);
    }

    [Fact]
    public void TheDateOfBirth_MustBeTheSameExactly()
    {
        RecoveryMatchResult result = RecoveryMatch.Compare("Arun", "Balasubramanian", new DateOnly(1979, 3, 6), Gender.Male, new VerifiedIdentity("digilocker", "x", "Arun Balasubramanian", Born, Gender.Male));
        Assert.Equal([RecoveryMatch.DateOfBirth], result.Mismatches);
    }

    [Theory]
    [InlineData(Gender.Male, Gender.Female, false)]
    [InlineData(Gender.PreferNotToSay, Gender.Female, true)]
    [InlineData(Gender.Other, Gender.Other, true)]
    public void TheGender_IsComparedOnlyWhenBothGiveOne(Gender profile, Gender record, bool matches)
    {
        RecoveryMatchResult result = RecoveryMatch.Compare("Kala", "Devi", Born, profile, new VerifiedIdentity("digilocker", "x", "Kala Devi", Born, record));
        Assert.Equal(matches, result.Matches);
    }
}
