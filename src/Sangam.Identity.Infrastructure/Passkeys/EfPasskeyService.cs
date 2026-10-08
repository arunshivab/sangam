using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Passkeys;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Passkeys;

/// <summary>
/// Passkeys on Fido2NetLib (PR-14, SGM-205). User verification is required for every ceremony;
/// attestation is not collected (privacy, SGM-205 §7). The WebAuthn user handle is the account id,
/// never the e-mail address. Fido2NetLib is a pending vendor choice (ADR-0012) and sits behind
/// <see cref="IPasskeyService"/>, so it can be replaced without touching the pages.
/// </summary>
public sealed class EfPasskeyService : IPasskeyService
{
    /// <summary>How long a ceremony may take.</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    private const string Register = "register";
    private const string Assert = "assert";
    private readonly SangamDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly Fido2 _fido2;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="configuration">Configuration (<c>Sangam:Passkeys</c>, <c>Sangam:Issuer</c>).</param>
    public EfPasskeyService(SangamDbContext db, IAuditWriter audit, IClock clock, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Enabled = configuration.GetValue("Sangam:Passkeys:Enabled", true);
        _fido2 = new Fido2(PasskeySettings.ToFido2Configuration(configuration));
    }

    /// <inheritdoc />
    public bool Enabled { get; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PasskeyRow>> ListAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _db.PasskeyCredentials.AsNoTracking()
            .Where(c => c.UserId == userId && c.RevokedAt == null)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new PasskeyRow(c.Id, c.Name, c.IsBackupEligible, c.CreatedAt, c.LastUsedAt))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PasskeyCeremony> BeginRegistrationAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        SangamUser user = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        List<PublicKeyCredentialDescriptor> existing = await _db.PasskeyCredentials.AsNoTracking()
            .Where(c => c.UserId == userId && c.RevokedAt == null)
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        CredentialCreateOptions options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = userId.ToByteArray(), Name = user.Email ?? string.Empty, DisplayName = (user.FirstName + " " + user.LastName).Trim() },
            ExcludeCredentials = existing,
            AuthenticatorSelection = new AuthenticatorSelection { ResidentKey = ResidentKeyRequirement.Preferred, UserVerification = UserVerificationRequirement.Required },
            AttestationPreference = AttestationConveyancePreference.None,
        });
        string json = options.ToJson();
        return new PasskeyCeremony(await SaveChallengeAsync(userId, Register, json, cancellationToken).ConfigureAwait(false), json);
    }

    /// <inheritdoc />
    public async Task<PasskeyResult> CompleteRegistrationAsync(Guid userId, Guid challengeId, string attestationJson, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attestationJson);
        ArgumentNullException.ThrowIfNull(name);
        PasskeyChallenge? challenge = await TakeChallengeAsync(challengeId, Register, userId, cancellationToken).ConfigureAwait(false);
        if (challenge is null)
        {
            return new PasskeyResult(false, "That took too long or was already used. Please try again.");
        }

        RegisteredPublicKeyCredential credential;
        try
        {
            AuthenticatorAttestationRawResponse response = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(attestationJson)
                ?? throw new JsonException("Empty attestation.");
            credential = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = CredentialCreateOptions.FromJson(challenge.OptionsJson),
                IsCredentialIdUniqueToUserCallback = async (args, ct) => !await _db.PasskeyCredentials.AnyAsync(c => c.CredentialId == args.CredentialId, ct).ConfigureAwait(false),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Fido2VerificationException or JsonException or FormatException)
        {
            return new PasskeyResult(false, "This passkey could not be added. Please try again.");
        }

        string label = name.Trim();
        PasskeyCredential row = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CredentialId = credential.Id,
            PublicKey = credential.PublicKey,
            SignCount = credential.SignCount,
            AaGuid = credential.AaGuid,
            Transports = credential.Transports is null ? string.Empty : string.Join(',', credential.Transports.Select(t => t.ToString().ToLowerInvariant())),
            IsBackupEligible = credential.IsBackupEligible,
            IsBackedUp = credential.IsBackedUp,
            Name = label.Length == 0 ? "Passkey" : label[..Math.Min(label.Length, 60)],
            CreatedAt = _clock.UtcNow,
        };
        _db.PasskeyCredentials.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.UserPasskeyAdd, AuditActorType.User, userId, TargetType: "user", TargetId: userId, Metadata: JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = row.Name, ["synced"] = row.IsBackupEligible ? "yes" : "no" })), cancellationToken).ConfigureAwait(false);
        return new PasskeyResult(true, $"Passkey “{row.Name}” added. You can now sign in with it.", userId);
    }

    /// <inheritdoc />
    public async Task<PasskeyCeremony> BeginSignInAsync(CancellationToken cancellationToken = default)
    {
        AssertionOptions options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Required,
        });
        string json = options.ToJson();
        return new PasskeyCeremony(await SaveChallengeAsync(null, Assert, json, cancellationToken).ConfigureAwait(false), json);
    }

    /// <inheritdoc />
    public async Task<PasskeyResult> CompleteSignInAsync(Guid challengeId, string assertionJson, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assertionJson);
        const string Refused = "That passkey could not sign you in.";
        PasskeyChallenge? challenge = await TakeChallengeAsync(challengeId, Assert, null, cancellationToken).ConfigureAwait(false);
        if (challenge is null)
        {
            return new PasskeyResult(false, "That took too long or was already used. Please try again.");
        }

        AuthenticatorAssertionRawResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionJson);
        }
        catch (JsonException)
        {
            response = null;
        }

        PasskeyCredential? credential = response?.RawId is null ? null : await _db.PasskeyCredentials
            .FirstOrDefaultAsync(c => c.CredentialId == response.RawId && c.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (response is null || credential is null)
        {
            await FailAsync(null, "unknown_credential", cancellationToken).ConfigureAwait(false);
            return new PasskeyResult(false, Refused);
        }

        VerifyAssertionResult verified;
        try
        {
            verified = await _fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = AssertionOptions.FromJson(challenge.OptionsJson),
                StoredPublicKey = credential.PublicKey,
                StoredSignatureCounter = (uint)credential.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(credential.UserId.ToByteArray())),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Fido2VerificationException ex)
        {
            await FailAsync(credential.UserId, ex.Message.Contains("counter", StringComparison.OrdinalIgnoreCase) ? "counter_regression" : "verification_failed", cancellationToken).ConfigureAwait(false);
            return new PasskeyResult(false, Refused);
        }

        SangamUser? user = await _db.Users.FirstOrDefaultAsync(u => u.Id == credential.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.Status != UserStatus.Active || !user.EmailConfirmed)
        {
            await FailAsync(credential.UserId, "account_not_active", cancellationToken).ConfigureAwait(false);
            return new PasskeyResult(false, Refused);
        }

        credential.SignCount = verified.SignCount;
        credential.IsBackedUp = verified.IsBackedUp;
        credential.LastUsedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new PasskeyResult(true, "Signed in.", user.Id);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(Guid userId, Guid passkeyId, CancellationToken cancellationToken = default)
    {
        PasskeyCredential? credential = await _db.PasskeyCredentials
            .FirstOrDefaultAsync(c => c.Id == passkeyId && c.UserId == userId && c.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            return false;
        }

        credential.RevokedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.UserPasskeyRemove, AuditActorType.User, userId, TargetType: "user", TargetId: userId, Metadata: JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = credential.Name })), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<Guid> SaveChallengeAsync(Guid? userId, string kind, string json, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        PasskeyChallenge challenge = new() { Id = Guid.NewGuid(), UserId = userId, Kind = kind, OptionsJson = json, CreatedAt = now, ExpiresAt = now + ChallengeLifetime };
        _db.PasskeyChallenges.Add(challenge);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return challenge.Id;
    }

    /// <summary>Marks the challenge used in one statement, so two answers cannot both win.</summary>
    private async Task<PasskeyChallenge?> TakeChallengeAsync(Guid challengeId, string kind, Guid? userId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        int taken = await _db.PasskeyChallenges
            .Where(c => c.Id == challengeId && c.Kind == kind && c.UserId == userId && c.UsedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), cancellationToken).ConfigureAwait(false);
        return taken == 1 ? await _db.PasskeyChallenges.AsNoTracking().FirstAsync(c => c.Id == challengeId, cancellationToken).ConfigureAwait(false) : null;
    }

    private Task FailAsync(Guid? userId, string reason, CancellationToken cancellationToken)
    {
        return _audit.WriteAsync(new AuditEntry(AuditActions.UserPasskeyFail, userId is null ? AuditActorType.Anonymous : AuditActorType.User, userId, TargetType: userId is null ? null : "user", TargetId: userId,
            Metadata: JsonSerializer.Serialize(new Dictionary<string, string> { ["reason"] = reason })), cancellationToken);
    }
}
