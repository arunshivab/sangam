namespace Sangam.Client;

/// <summary>rc.12: a person's details as one of your admins entered them, for the check and for adding them.</summary>
/// <param name="FirstName">First name.</param>
/// <param name="LastName">Last name.</param>
/// <param name="Email">Email; Sangam sends the activation link here.</param>
/// <param name="Mobile">Mobile in E.164 form (<c>+91…</c>), if known.</param>
/// <param name="DateOfBirth">Date of birth.</param>
public sealed record SangamStaffPerson(string FirstName, string LastName, string Email, string? Mobile, DateOnly DateOfBirth);

/// <summary>
/// rc.12: one person added to one organisation. Set exactly one of <paramref name="Person"/>, <paramref name="JoinCode"/>,
/// <paramref name="StaffRef"/> and <paramref name="UserId"/>.
/// </summary>
/// <param name="Person">Their details (they get an activation code your admin hands over in person).</param>
/// <param name="JoinCode">The join code they made in their Sangam portal.</param>
/// <param name="StaffRef">The <c>staffRef</c> an earlier addition returned, for their other organisations.</param>
/// <param name="UserId">The <c>sub</c> of someone already in this customer.</param>
/// <param name="Roles">Role codes at this organisation; none for staff whose roles your application keeps.</param>
/// <param name="StartsOn">The first day of access (India time), for someone who starts later.</param>
/// <param name="ExpiresAt">When the roles end, if they do.</param>
/// <param name="Confirmed">Your admin saw the check's warning and proceeds.</param>
public sealed record SangamStaffAddition(
    SangamStaffPerson? Person = null,
    string? JoinCode = null,
    Guid? StaffRef = null,
    Guid? UserId = null,
    IReadOnlyList<string>? Roles = null,
    DateOnly? StartsOn = null,
    DateTimeOffset? ExpiresAt = null,
    bool Confirmed = false);
