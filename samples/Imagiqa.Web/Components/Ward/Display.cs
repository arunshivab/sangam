using System.Globalization;

namespace Imagiqa.Web.Components.Ward;

/// <summary>How imagiQa shows ages, dates and readings.</summary>
public static class Display
{
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    /// <summary>Age in years, or in months under two.</summary>
    /// <param name="dateOfBirth">Date of birth.</param>
    /// <param name="today">Today.</param>
    /// <returns>For example <c>34 y</c> or <c>7 mo</c>.</returns>
    public static string Age(DateOnly dateOfBirth, DateOnly today)
    {
        int months = ((today.Year - dateOfBirth.Year) * 12) + today.Month - dateOfBirth.Month - (today.Day < dateOfBirth.Day ? 1 : 0);
        return months < 24 ? $"{Math.Max(months, 0)} mo" : $"{months / 12} y";
    }

    /// <summary>Sex as a single letter.</summary>
    /// <param name="sex">Stored value.</param>
    /// <returns><c>F</c>, <c>M</c> or <c>O</c>.</returns>
    public static string SexLetter(string sex) => sex switch { "female" => "F", "male" => "M", _ => "O" };

    /// <summary>A date and time in India's time zone.</summary>
    /// <param name="when">The moment.</param>
    /// <returns>For example <c>30 Sep 2026, 14:05</c>.</returns>
    public static string When(DateTimeOffset when)
        => when.ToOffset(TimeSpan.FromHours(5.5)).ToString("d MMM yyyy, HH:mm", India);

    /// <summary>A date.</summary>
    /// <param name="date">The date.</param>
    /// <returns>For example <c>12 Jun 1986</c>.</returns>
    public static string Date(DateOnly date) => date.ToString("d MMM yyyy", India);

    /// <summary>Whether a set of readings has any value outside the usual adult range.</summary>
    /// <param name="pulse">Pulse.</param>
    /// <param name="systolic">Systolic.</param>
    /// <param name="temperature">Temperature.</param>
    /// <param name="spO2">Oxygen saturation.</param>
    /// <param name="respiratoryRate">Breathing rate.</param>
    /// <returns>One flag per reading, in that order.</returns>
    public static (bool Pulse, bool Systolic, bool Temperature, bool SpO2, bool Rate) Flags(int? pulse, int? systolic, decimal? temperature, int? spO2, int? respiratoryRate)
        => (pulse is < 50 or > 120, systolic is < 90 or >= 180, temperature is < 35m or >= 38m, spO2 is < 94, respiratoryRate is < 10 or > 24);
}
