using System.Globalization;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Verification;

/// <summary>How a DigiLocker record compared with an account (rc.6, SGM-914 section 6).</summary>
/// <param name="Matches">Whether every part matched.</param>
/// <param name="Mismatches">The parts that did not: <c>name</c>, <c>date_of_birth</c>, <c>gender</c>.</param>
public sealed record RecoveryMatchResult(bool Matches, IReadOnlyList<string> Mismatches);

/// <summary>
/// The matching rule for recovering an account that was never verified (rc.6, SGM-914 section 6): the date of birth the
/// same exactly; every word of the profile's name present in the DigiLocker name, in any order, ignoring case and
/// spacing, an initial matching a word that starts with that letter; the gender the same when both give one. A missing
/// middle name or swapped first and last names still match. The comparison is made only for the check: nothing it
/// reads is changed.
/// </summary>
public static class RecoveryMatch
{
    /// <summary>The name part.</summary>
    public const string Name = "name";

    /// <summary>The date-of-birth part.</summary>
    public const string DateOfBirth = "date_of_birth";

    /// <summary>The gender part.</summary>
    public const string GenderPart = "gender";

    private static readonly char[] Separators = [' ', '\t', '.', ',', '-', '\''];

    /// <summary>Compares an account's profile with a DigiLocker record.</summary>
    /// <param name="firstName">The profile's first name.</param>
    /// <param name="lastName">The profile's last name.</param>
    /// <param name="dateOfBirth">The profile's date of birth.</param>
    /// <param name="gender">The profile's gender.</param>
    /// <param name="record">What DigiLocker said.</param>
    public static RecoveryMatchResult Compare(string? firstName, string? lastName, DateOnly dateOfBirth, Gender gender, VerifiedIdentity record)
    {
        ArgumentNullException.ThrowIfNull(record);
        List<string> mismatches = [];
        if (!NameMatches(((firstName ?? string.Empty) + " " + (lastName ?? string.Empty)).Trim(), record.Name))
        {
            mismatches.Add(Name);
        }

        if (dateOfBirth != record.DateOfBirth)
        {
            mismatches.Add(DateOfBirth);
        }

        if (Known(gender) && Known(record.Gender) && gender != record.Gender)
        {
            mismatches.Add(GenderPart);
        }

        return new RecoveryMatchResult(mismatches.Count == 0, mismatches);
    }

    /// <summary>Whether every word of <paramref name="profileName"/> is in <paramref name="recordName"/> (initials allowed).</summary>
    /// <param name="profileName">The profile's full name.</param>
    /// <param name="recordName">The record's name.</param>
    public static bool NameMatches(string? profileName, string? recordName)
    {
        string[] profile = Words(profileName);
        string[] record = Words(recordName);
        if (profile.Length == 0 || record.Length == 0)
        {
            return false;
        }

        List<string> unused = [.. record];
        foreach (string word in profile.OrderByDescending(w => w.Length))
        {
            int at = word.Length == 1
                ? unused.FindIndex(r => r.StartsWith(word, StringComparison.Ordinal))
                : unused.FindIndex(r => string.Equals(r, word, StringComparison.Ordinal));
            if (at < 0)
            {
                return false;
            }

            unused.RemoveAt(at);
        }

        return true;
    }

    private static string[] Words(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? []
            : [.. name.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.ToLower(CultureInfo.InvariantCulture))];

    private static bool Known(Gender gender) => gender is Gender.Female or Gender.Male or Gender.Other;
}
