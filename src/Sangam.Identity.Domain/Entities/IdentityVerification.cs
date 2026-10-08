using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// A person's identity verified against a government record (PR-26, SGM-201, SGM-204): today through DigiLocker. Holds what the
/// record said and a keyed hash of the DigiLocker id — never the id itself, and never an Aadhaar number. One per account,
/// and one account per DigiLocker identity.
/// </summary>
public class IdentityVerification
{
    /// <summary>Verification id.</summary>
    public Guid Id { get; set; }

    /// <summary>The person.</summary>
    public Guid UserId { get; set; }

    /// <summary>How it was verified: <c>digilocker</c>.</summary>
    public string Method { get; set; } = "digilocker";

    /// <summary>HMAC-SHA256 of the provider's subject id, keyed with Sangam's secret, as lowercase hex.</summary>
    public string SubjectHash { get; set; } = string.Empty;

    /// <summary>The name on the record.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The date of birth on the record.</summary>
    public DateOnly DateOfBirth { get; set; }

    /// <summary>The gender on the record.</summary>
    public Gender Gender { get; set; }

    /// <summary>When it was verified.</summary>
    public DateTimeOffset VerifiedAt { get; set; }
}
