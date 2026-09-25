namespace Sangam.Identity.Domain;

/// <summary>
/// Who may hold a Sangam account of their own.
/// <para>
/// India's DPDP Act treats anyone under 18 as a child, and processing a child's personal data
/// requires verifiable consent from a parent or lawful guardian — a mechanism Sangam does not
/// have. So self-registration is closed below 18. A lower bar drawn from employment rules (14
/// for non-hazardous work, 16 for healthcare trainees) would leave those users inside the Act's
/// definition of a child with no guardian consent recorded anywhere.
/// </para>
/// <para>
/// This is a declared age, not a proven one: until identity verification exists, it is an honest
/// barrier and a compliance posture rather than an assurance. An account for a child is a
/// different object — created by a guardian, linked to them, and handed over at 18 — and would be
/// built as such rather than by lowering this number.
/// </para>
/// </summary>
public static class AgePolicy
{
    /// <summary>Youngest age that may register a Sangam account without a guardian.</summary>
    public const int MinimumRegistrationAge = 18;

    /// <summary>Oldest plausible age, used to reject typing slips such as a year in the 1800s.</summary>
    public const int MaximumPlausibleAge = 120;

    /// <summary>Completed years between <paramref name="dateOfBirth"/> and <paramref name="today"/>.</summary>
    /// <param name="dateOfBirth">Date of birth.</param>
    /// <param name="today">Today's date.</param>
    /// <returns>The age in completed years; negative when the date is in the future.</returns>
    public static int AgeOn(DateOnly dateOfBirth, DateOnly today)
    {
        int age = today.Year - dateOfBirth.Year;
        if (dateOfBirth > today.AddYears(-age))
        {
            age--;
        }

        return age;
    }

    /// <summary>Whether a person born on <paramref name="dateOfBirth"/> may register.</summary>
    /// <param name="dateOfBirth">Date of birth.</param>
    /// <param name="today">Today's date.</param>
    public static bool MayRegister(DateOnly dateOfBirth, DateOnly today)
        => AgeOn(dateOfBirth, today) >= MinimumRegistrationAge;

    /// <summary>Whether the date is a plausible date of birth at all (not future, not absurdly old).</summary>
    /// <param name="dateOfBirth">Date of birth.</param>
    /// <param name="today">Today's date.</param>
    public static bool IsPlausible(DateOnly dateOfBirth, DateOnly today)
        => dateOfBirth <= today && dateOfBirth >= today.AddYears(-MaximumPlausibleAge);

    /// <summary>The latest date of birth that may register today, for the form's <c>max</c> attribute.</summary>
    /// <param name="today">Today's date.</param>
    public static DateOnly LatestEligibleBirthDate(DateOnly today) => today.AddYears(-MinimumRegistrationAge);
}
