using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Verification;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Provisioning;
using Sangam.Identity.Infrastructure.Sms;

namespace Sangam.Identity.Infrastructure.Verification;

/// <summary>
/// <see cref="IIdentityVerificationService"/> over the identity database (PR-26). The DigiLocker id is stored only as an
/// HMAC keyed with <c>Sangam:DigiLocker:SubjectKey</c>, which is what makes one DigiLocker identity verify one account.
/// Applying a verification sets the profile's name, date of birth and gender to the record's and locks them; removing it
/// unlocks them (the values stay). Both are audited, and raise <c>user.updated</c> for SCIM and webhooks.
/// </summary>
public sealed class EfIdentityVerificationService : IIdentityVerificationService
{
    private readonly SangamDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly DigiLockerSettings _settings;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="settings">DigiLocker settings (for the subject key).</param>
    public EfIdentityVerificationService(SangamDbContext db, IAuditWriter audit, IClock clock, DigiLockerSettings settings)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <inheritdoc />
    public async Task<VerificationStatus> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        IdentityVerification? verification = await _db.IdentityVerifications.AsNoTracking().FirstOrDefaultAsync(v => v.UserId == userId, cancellationToken).ConfigureAwait(false);
        return verification is null
            ? new VerificationStatus(false, null, null, null)
            : new VerificationStatus(true, verification.Method, verification.VerifiedAt, verification.Name);
    }

    /// <inheritdoc />
    public async Task<VerificationResult> ApplyAsync(Guid userId, VerifiedIdentity identity, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string hash = SubjectHash(identity.Method, identity.Subject);
        if (await _db.IdentityVerifications.AnyAsync(v => v.Method == identity.Method && v.SubjectHash == hash && v.UserId != userId, cancellationToken).ConfigureAwait(false))
        {
            await _audit.WriteAsync(new AuditEntry(AuditActions.IdentityVerifyRefused, AuditActorType.User, userId, TargetType: "user", TargetId: userId,
                Metadata: JsonSerializer.Serialize(new { method = identity.Method, reason = "identity_on_another_account" }), IpAddress: ipAddress), cancellationToken).ConfigureAwait(false);
            return new VerificationResult(false, "taken");
        }

        SangamUser user = await _db.Users.FirstAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _clock.UtcNow;
        IdentityVerification? verification = await _db.IdentityVerifications.FirstOrDefaultAsync(v => v.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (verification is null)
        {
            verification = new IdentityVerification { Id = Guid.NewGuid(), UserId = userId };
            _db.IdentityVerifications.Add(verification);
        }

        verification.Method = identity.Method;
        verification.SubjectHash = hash;
        verification.Name = identity.Name;
        verification.DateOfBirth = identity.DateOfBirth;
        verification.Gender = identity.Gender;
        verification.VerifiedAt = now;

        (string first, string last) = SplitName(identity.Name);
        bool changed = user.FirstName != first || user.LastName != last || user.DateOfBirth != identity.DateOfBirth || user.Gender != identity.Gender;
        user.FirstName = first;
        user.LastName = last;
        user.DateOfBirth = identity.DateOfBirth;
        user.Gender = identity.Gender;
        user.IdentityVerifiedAt = now;
        user.UpdatedAt = now;
        await AppEventLog.AddAsync(_db, AppEventTypes.UserUpdated, null, userId, null, new Dictionary<string, object?> { ["identity_verified"] = true }, now, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.IdentityVerify, AuditActorType.User, userId, TargetType: "user", TargetId: userId,
            Metadata: JsonSerializer.Serialize(new { method = identity.Method, profile_changed = changed }), IpAddress: ipAddress), cancellationToken).ConfigureAwait(false);
        return new VerificationResult(true, "verified");
    }

    /// <inheritdoc />
    public async Task<VerificationResult> RemoveAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        IdentityVerification? verification = await _db.IdentityVerifications.FirstOrDefaultAsync(v => v.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (verification is null)
        {
            return new VerificationResult(false, "none");
        }

        SangamUser user = await _db.Users.FirstAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _clock.UtcNow;
        _db.IdentityVerifications.Remove(verification);
        user.IdentityVerifiedAt = null;
        user.UpdatedAt = now;
        await AppEventLog.AddAsync(_db, AppEventTypes.UserUpdated, null, userId, null, new Dictionary<string, object?> { ["identity_verified"] = false }, now, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.IdentityUnverify, AuditActorType.User, userId, TargetType: "user", TargetId: userId,
            Metadata: JsonSerializer.Serialize(new { method = verification.Method }), IpAddress: ipAddress), cancellationToken).ConfigureAwait(false);
        return new VerificationResult(true, "removed");
    }

    /// <summary>The keyed hash a provider's subject id is stored as.</summary>
    /// <param name="method">The method.</param>
    /// <param name="subject">The provider's id.</param>
    public string SubjectHash(string method, string subject)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(subject);
        return SmsNumbers.Hash(_settings.EffectiveSubjectKey, method + ":" + subject.Trim());
    }

    /// <summary>
    /// Splits a record's single name into Sangam's given and family names: the last word is the family name. A one-word
    /// name stays whole as the given name, with no family name. Letters and case are kept as the record has them.
    /// </summary>
    /// <param name="name">The name on the record.</param>
    public static (string First, string Last) SplitName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length switch
        {
            0 => (string.Empty, string.Empty),
            1 => (words[0], string.Empty),
            _ => (string.Join(' ', words[..^1]), words[^1]),
        };
    }
}
