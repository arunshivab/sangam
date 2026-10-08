using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
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
/// Passkeys on ASP.NET Core Identity's built-in WebAuthn support (D-G; PR-14, SGM-205). Identity's passkey handler
/// makes the options and verifies every answer — challenge, origin, relying party, user verification, signature and
/// counter — and its store keeps the keys. Sangam keeps the ceremony state in its own table (so a challenge works
/// once, across hosts), its record of each passkey (name, last use, removal) and the audit trail. The WebAuthn user
/// handle is the account id, never the e-mail address.
/// </summary>
public sealed class EfPasskeyService : IPasskeyService
{
    private const string Register = "register";
    private const string Assert = "assert";
    private readonly SangamDbContext _db;
    private readonly UserManager<SangamUser> _users;
    private readonly IPasskeyHandler<SangamUser> _handler;
    private readonly IHttpContextAccessor _http;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="users">Identity's user manager (its passkey store).</param>
    /// <param name="handler">Identity's passkey handler.</param>
    /// <param name="http">The current request, which Identity's handler receives.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="configuration">Configuration (<c>Sangam:Passkeys:Enabled</c>).</param>
    public EfPasskeyService(SangamDbContext db, UserManager<SangamUser> users, IPasskeyHandler<SangamUser> handler, IHttpContextAccessor http, IAuditWriter audit, IClock clock, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Enabled = configuration.GetValue("Sangam:Passkeys:Enabled", true);
    }

    /// <inheritdoc />
    public bool Enabled { get; }

    private HttpContext Http => _http.HttpContext ?? new DefaultHttpContext();

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
        PasskeyCreationOptionsResult options = await _handler.MakeCreationOptionsAsync(
            new PasskeyUserEntity
            {
                Id = userId.ToString("D", CultureInfo.InvariantCulture),
                Name = user.Email ?? string.Empty,
                DisplayName = (user.FirstName + " " + user.LastName).Trim(),
            },
            Http).ConfigureAwait(false);
        Guid challengeId = await SaveChallengeAsync(userId, Register, options.AttestationState ?? string.Empty, cancellationToken).ConfigureAwait(false);
        return new PasskeyCeremony(challengeId, options.CreationOptionsJson);
    }

    /// <inheritdoc />
    public async Task<PasskeyResult> CompleteRegistrationAsync(Guid userId, Guid challengeId, string attestationJson, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attestationJson);
        ArgumentNullException.ThrowIfNull(name);
        const string Refused = "This passkey could not be added. Please try again.";
        PasskeyChallenge? challenge = await TakeChallengeAsync(challengeId, Register, userId, cancellationToken).ConfigureAwait(false);
        if (challenge is null)
        {
            return new PasskeyResult(false, "That took too long or was already used. Please try again.");
        }

        PasskeyAttestationResult attestation;
        try
        {
            attestation = await _handler.PerformAttestationAsync(new PasskeyAttestationContext
            {
                CredentialJson = attestationJson,
                AttestationState = challenge.OptionsJson,
                HttpContext = Http,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or PasskeyException)
        {
            return new PasskeyResult(false, Refused);
        }

        if (!attestation.Succeeded || attestation.Passkey is null
            || !string.Equals(attestation.UserEntity?.Id, userId.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            return new PasskeyResult(false, Refused);
        }

        UserPasskeyInfo passkey = attestation.Passkey;
        if (await _users.FindByPasskeyIdAsync(passkey.CredentialId).ConfigureAwait(false) is not null
            || await _db.PasskeyCredentials.AnyAsync(c => c.CredentialId == passkey.CredentialId, cancellationToken).ConfigureAwait(false))
        {
            return new PasskeyResult(false, Refused);
        }

        SangamUser user = await _users.FindByIdAsync(userId.ToString("D")).ConfigureAwait(false)
            ?? throw new InvalidOperationException("User not found.");
        string label = name.Trim();
        label = label.Length == 0 ? "Passkey" : label[..Math.Min(label.Length, 60)];
        passkey.Name = label;
        IdentityResult stored = await _users.AddOrUpdatePasskeyAsync(user, passkey).ConfigureAwait(false);
        if (!stored.Succeeded)
        {
            return new PasskeyResult(false, Refused);
        }

        PasskeyCredential row = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CredentialId = passkey.CredentialId,
            IsBackupEligible = passkey.IsBackupEligible,
            Name = label,
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
        // Discoverable credentials: no account is named, so the options reveal nothing about who has passkeys.
        PasskeyRequestOptionsResult options = await _handler.MakeRequestOptionsAsync(null!, Http).ConfigureAwait(false);
        Guid challengeId = await SaveChallengeAsync(null, Assert, options.AssertionState ?? string.Empty, cancellationToken).ConfigureAwait(false);
        return new PasskeyCeremony(challengeId, options.RequestOptionsJson);
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

        PasskeyAssertionResult<SangamUser> assertion;
        try
        {
            assertion = await _handler.PerformAssertionAsync(new PasskeyAssertionContext
            {
                CredentialJson = assertionJson,
                AssertionState = challenge.OptionsJson,
                HttpContext = Http,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or PasskeyException)
        {
            await FailAsync(null, "unreadable_answer", cancellationToken).ConfigureAwait(false);
            return new PasskeyResult(false, Refused);
        }

        if (!assertion.Succeeded || assertion.User is null || assertion.Passkey is null)
        {
            string reason = assertion.Failure?.Message is string message && message.Contains("count", StringComparison.OrdinalIgnoreCase)
                ? "counter_regression"
                : assertion.User is null ? "unknown_credential" : "verification_failed";
            await FailAsync(assertion.User?.Id, reason, cancellationToken).ConfigureAwait(false);
            return new PasskeyResult(false, Refused);
        }

        SangamUser user = assertion.User;
        PasskeyCredential? record = await _db.PasskeyCredentials
            .FirstOrDefaultAsync(c => c.CredentialId == assertion.Passkey.CredentialId && c.UserId == user.Id && c.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            await FailAsync(user.Id, "unknown_credential", cancellationToken).ConfigureAwait(false);
            return new PasskeyResult(false, Refused);
        }

        if (user.Status != UserStatus.Active || !user.EmailConfirmed)
        {
            await FailAsync(user.Id, "account_not_active", cancellationToken).ConfigureAwait(false);
            return new PasskeyResult(false, Refused);
        }

        // The new signature counter and backup state, so a cloned authenticator is caught next time.
        await _users.AddOrUpdatePasskeyAsync(user, assertion.Passkey).ConfigureAwait(false);
        record.LastUsedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new PasskeyResult(true, "Signed in.", user.Id);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(Guid userId, Guid passkeyId, CancellationToken cancellationToken = default)
    {
        PasskeyCredential? record = await _db.PasskeyCredentials
            .FirstOrDefaultAsync(c => c.Id == passkeyId && c.UserId == userId && c.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return false;
        }

        if (await _users.FindByIdAsync(userId.ToString("D")).ConfigureAwait(false) is SangamUser user)
        {
            await _users.RemovePasskeyAsync(user, record.CredentialId).ConfigureAwait(false);
        }

        record.RevokedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.UserPasskeyRemove, AuditActorType.User, userId, TargetType: "user", TargetId: userId, Metadata: JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = record.Name })), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<Guid> SaveChallengeAsync(Guid? userId, string kind, string state, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        PasskeyChallenge challenge = new() { Id = Guid.NewGuid(), UserId = userId, Kind = kind, OptionsJson = state, CreatedAt = now, ExpiresAt = now + PasskeySettings.CeremonyLifetime };
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
        Monitoring.SangamMetrics.SignInFailureCount.Add(1);
        return _audit.WriteAsync(new AuditEntry(AuditActions.UserPasskeyFail, userId is null ? AuditActorType.Anonymous : AuditActorType.User, userId, TargetType: userId is null ? null : "user", TargetId: userId,
            Metadata: JsonSerializer.Serialize(new Dictionary<string, string> { ["reason"] = reason })), cancellationToken);
    }
}
