using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Verification;

/// <summary>
/// PR-26 (SGM-201, SGM-204): verifying a person's name, date of birth and gender against DigiLocker. The person agrees first that
/// their profile will take the record's values and be locked; Sangam keeps the values and a keyed hash of the DigiLocker
/// id, never the id itself and never an Aadhaar number, and one DigiLocker identity verifies one account only.
/// </summary>
public interface IIdentityVerificationService
{
    /// <summary>Whether, and how, the person is verified.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<VerificationStatus> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Records a verified identity and makes the profile match it.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="identity">What DigiLocker returned.</param>
    /// <param name="ipAddress">Where the person was.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<VerificationResult> ApplyAsync(Guid userId, VerifiedIdentity identity, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Removes the verification, unlocking the profile.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="ipAddress">Where the person was.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<VerificationResult> RemoveAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default);
}

/// <summary>An identity as a government record states it.</summary>
/// <param name="Method">How: <c>digilocker</c>.</param>
/// <param name="Subject">The provider's stable id for the person (hashed before it is stored).</param>
/// <param name="Name">The name on the record.</param>
/// <param name="DateOfBirth">The date of birth on the record.</param>
/// <param name="Gender">The gender on the record.</param>
public sealed record VerifiedIdentity(string Method, string Subject, string Name, DateOnly DateOfBirth, Gender Gender);

/// <summary>Whether a person is verified.</summary>
/// <param name="Verified">Verified.</param>
/// <param name="Method">How, when verified.</param>
/// <param name="VerifiedAt">When, when verified.</param>
/// <param name="Name">The verified name.</param>
public sealed record VerificationStatus(bool Verified, string? Method, DateTimeOffset? VerifiedAt, string? Name);

/// <summary>The outcome of a verification change.</summary>
/// <param name="Succeeded">Applied.</param>
/// <param name="Code">A short code for the screen: <c>verified</c>, <c>removed</c>, <c>taken</c>, <c>none</c>.</param>
public sealed record VerificationResult(bool Succeeded, string Code);
