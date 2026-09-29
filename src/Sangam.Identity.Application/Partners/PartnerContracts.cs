using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Partners;

/// <summary>An application the signed-in person administers.</summary>
/// <param name="AppId">App id.</param>
/// <param name="ClientId">OAuth client id.</param>
/// <param name="DisplayName">Name.</param>
/// <param name="BrandColour">Hex brand colour for the tile.</param>
/// <param name="Glyph">One or two characters for the tile.</param>
/// <param name="Role">Their rank over it.</param>
/// <param name="Status">Whether the platform has the application enabled.</param>
public sealed record PartnerAppRow(Guid AppId, string ClientId, string DisplayName, string BrandColour, string Glyph, AppAdminRole Role, AppStatus Status);

/// <summary>
/// A person who has linked the application. The partner console can only ever see these people —
/// never Sangam's wider directory — so an application cannot be used to look up who else is on the
/// platform.
/// </summary>
/// <param name="UserId">User id.</param>
/// <param name="DisplayName">Name.</param>
/// <param name="Email">Email address.</param>
/// <param name="LinkedAt">When they linked the application.</param>
public sealed record LinkedUserRow(Guid UserId, string DisplayName, string Email, DateTimeOffset LinkedAt);

/// <summary>A member of an organisation, with a name the console can show.</summary>
/// <param name="UserId">User id.</param>
/// <param name="DisplayName">Name.</param>
/// <param name="Email">Email address.</param>
/// <param name="Role">Role code.</param>
/// <param name="AppliesToDescendants">Whether the role flows down the organisation tree.</param>
/// <param name="GrantedAt">When granted.</param>
public sealed record PartnerMemberRow(Guid UserId, string DisplayName, string Email, string Role, bool AppliesToDescendants, DateTimeOffset GrantedAt);

/// <summary>One of the application's administrators.</summary>
/// <param name="UserId">User id.</param>
/// <param name="DisplayName">Name.</param>
/// <param name="Email">Email address.</param>
/// <param name="Role">Rank.</param>
/// <param name="MfaEnrolled">Whether an authenticator is enrolled; without one the partner console refuses them.</param>
/// <param name="GrantedAt">When granted.</param>
public sealed record AppAdminRow(Guid UserId, string DisplayName, string Email, AppAdminRole Role, bool MfaEnrolled, DateTimeOffset GrantedAt);

/// <summary>The settings a partner may change about their own application.</summary>
/// <param name="DisplayName">Name — shown, not editable here (it appears on consent screens).</param>
/// <param name="Description">One-line description.</param>
/// <param name="BrandColour">Hex brand colour.</param>
/// <param name="Glyph">One or two characters for the tile.</param>
/// <param name="SignInPolicy">The application's sign-in rule.</param>
public sealed record PartnerAppSettings(string DisplayName, string? Description, string BrandColour, string Glyph, SignInPolicy SignInPolicy);

/// <summary>Outcome of a partner-console action.</summary>
/// <param name="Succeeded">Whether it was applied.</param>
/// <param name="Message">What to tell the person.</param>
public sealed record PartnerResult(bool Succeeded, string? Message)
{
    /// <summary>Applied.</summary>
    /// <param name="message">What to tell the person.</param>
    public static PartnerResult Ok(string message) => new(true, message);

    /// <summary>Refused.</summary>
    /// <param name="message">Why.</param>
    public static PartnerResult Refused(string message) => new(false, message);
}

/// <summary>A kind of organisation, with where it may sit in a tree.</summary>
/// <param name="Code">Code stored on the organisation.</param>
/// <param name="DisplayName">Name to show.</param>
/// <param name="CanBeRoot">Whether it may be a top-level organisation.</param>
/// <param name="CanHaveChildren">Whether other organisations may sit under it.</param>
public sealed record OrgTypeRow(string Code, string DisplayName, bool CanBeRoot, bool CanHaveChildren);
