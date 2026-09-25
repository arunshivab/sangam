
namespace Sangam.Identity.Domain.Tests;

public sealed class AgePolicyTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);

    [Fact]
    public void MinimumIsEighteen_MatchingTheDpdpDefinitionOfAChild()
    {
        Assert.Equal(18, AgePolicy.MinimumRegistrationAge);
    }

    [Theory]
    [InlineData(2008, 9, 25, 18)]
    [InlineData(2008, 9, 26, 17)]
    [InlineData(2008, 12, 31, 17)]
    [InlineData(1986, 6, 12, 40)]
    [InlineData(2026, 9, 25, 0)]
    public void AgeOn_CountsCompletedYears_IncludingTheBirthdayItself(int year, int month, int day, int expected)
    {
        Assert.Equal(expected, AgePolicy.AgeOn(new DateOnly(year, month, day), Today));
    }

    [Fact]
    public void MayRegister_OnTheEighteenthBirthday_ButNotTheDayBefore()
    {
        Assert.True(AgePolicy.MayRegister(new DateOnly(2008, 9, 25), Today));
        Assert.False(AgePolicy.MayRegister(new DateOnly(2008, 9, 26), Today));
        Assert.False(AgePolicy.MayRegister(new DateOnly(2012, 1, 1), Today));
        Assert.True(AgePolicy.MayRegister(new DateOnly(1970, 1, 1), Today));
    }

    [Fact]
    public void LeapDayBirthdays_TurnEighteenOnTheFirstOfMarch()
    {
        DateOnly leapling = new(2008, 2, 29);
        Assert.False(AgePolicy.MayRegister(leapling, new DateOnly(2026, 2, 28)));
        Assert.True(AgePolicy.MayRegister(leapling, new DateOnly(2026, 3, 1)));
    }

    [Fact]
    public void IsPlausible_RejectsTheFutureAndTheAbsurdlyOld()
    {
        Assert.True(AgePolicy.IsPlausible(new DateOnly(1950, 4, 3), Today));
        Assert.True(AgePolicy.IsPlausible(Today, Today));
        Assert.False(AgePolicy.IsPlausible(Today.AddDays(1), Today));
        Assert.False(AgePolicy.IsPlausible(new DateOnly(1880, 1, 1), Today));
    }

    [Fact]
    public void LatestEligibleBirthDate_IsExactlyEighteenYearsBack()
    {
        Assert.Equal(new DateOnly(2008, 9, 25), AgePolicy.LatestEligibleBirthDate(Today));
        Assert.True(AgePolicy.MayRegister(AgePolicy.LatestEligibleBirthDate(Today), Today));
    }
}
