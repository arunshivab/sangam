using Microsoft.Extensions.Configuration;

namespace Sangam.Identity.Infrastructure.Verification;

/// <summary>
/// DigiLocker as an identity-verification source (PR-26), bound from <c>Sangam:DigiLocker</c>. The endpoints default to
/// DigiLocker's published OAuth 2.0 API (Requester API Specification v1.12) and are configurable, so the sandbox, the
/// MeriPehchaan host or a test server can stand in.
/// </summary>
public sealed class DigiLockerSettings
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Sangam:DigiLocker";

    /// <summary>The name of the HTTP client that calls DigiLocker.</summary>
    public const string ClientName = "sangam.digilocker";

    private const string DevelopmentSubjectKey = "sangam-development-digilocker-subject-key-not-secret"; // i18n-ignore: a development key

    /// <summary>Whether people may verify with DigiLocker.</summary>
    public bool Enabled { get; set; }

    /// <summary>The client id DigiLocker issued.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The client secret DigiLocker issued.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>The authorisation endpoint.</summary>
    public string AuthorizeUrl { get; set; } = "https://api.digitallocker.gov.in/public/oauth2/1/authorize";

    /// <summary>The token endpoint.</summary>
    public string TokenUrl { get; set; } = "https://api.digitallocker.gov.in/public/oauth2/1/token";

    /// <summary>The user-details endpoint, read when the token response leaves something out.</summary>
    public string UserUrl { get; set; } = "https://api.digitallocker.gov.in/public/oauth2/1/user";

    /// <summary>The address DigiLocker sends the person back to, as registered with it; empty means the portal's own <c>/verify/digilocker/callback</c>.</summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// rc.6 (SGM-914): the identity server's return address, registered with DigiLocker as well, for an application that
    /// requires verification and for recovering an account; empty means the identity server's own
    /// <c>/identity/digilocker/callback</c>.
    /// </summary>
    public string IdentityRedirectUri { get; set; } = string.Empty;

    /// <summary>The key DigiLocker ids are hashed with (32 characters or more outside Development).</summary>
    public string SubjectKey { get; set; } = string.Empty;

    /// <summary>The key in use; a fixed development key when none is set.</summary>
    public string EffectiveSubjectKey => string.IsNullOrWhiteSpace(SubjectKey) ? DevelopmentSubjectKey : SubjectKey;

    /// <summary>Reads the settings.</summary>
    /// <param name="configuration">Configuration.</param>
    public static DigiLockerSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        DigiLockerSettings settings = new();
        configuration.GetSection(SectionName).Bind(settings);
        return settings;
    }
}
