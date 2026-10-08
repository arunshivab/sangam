namespace Sangam.Identity.Application.Customisation;

/// <summary>
/// The application (and organisation) a request is acting for, when the sign-in screens know it (PR-19). Scoped to
/// one request: the screens set it from the authorization request, and the e-mails sent during that request use the
/// application's templates and colours.
/// </summary>
public sealed class CurrentApplication
{
    /// <summary>The application, or null for Sangam itself.</summary>
    public Guid? AppId { get; set; }

    /// <summary>The organisation the application named, or null.</summary>
    public Guid? OrgId { get; set; }
}
