using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Accounts;

/// <summary><see cref="ILoginIdService"/> over the identity database (rc.11).</summary>
public sealed class EfLoginIdService : ILoginIdService
{
    private const int Alternatives = 20;

    private readonly SangamDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    public EfLoginIdService(SangamDbContext db, IAuditWriter audit, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<string?> ResolveEmailAsync(string identifier, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        string value = identifier.Trim();
        switch (SignInIdentifiers.Classify(value))
        {
            case SignInIdentifierKind.Email:
                return value;

            case SignInIdentifierKind.Mobile:
                // Only a verified number signs in: anyone can type a number at registration, only its owner can verify it.
                string mobile = AccountService.NormaliseMobile(value);
                List<string?> byMobile = await _db.Users.AsNoTracking()
                    .Where(u => u.PhoneNumber == mobile && u.PhoneNumberConfirmed && u.Status != UserStatus.DeletedHard)
                    .Select(u => u.Email)
                    .Take(2)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                return byMobile.Count == 1 ? byMobile[0] : null;

            case SignInIdentifierKind.LoginId:
                string loginId = LoginIds.Normalise(value);
                return await _db.Users.AsNoTracking()
                    .Where(u => u.LoginId == loginId && u.Status != UserStatus.DeletedHard)
                    .Select(u => u.Email)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            default:
                return null;
        }
    }

    /// <inheritdoc />
    public async Task<LoginIdState> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.LoginId, u.FirstName, u.LastName, u.Email, u.PhoneNumberConfirmed })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return new LoginIdState(null, string.Empty, false);
        }

        string suggestion = user.LoginId is null
            ? await FreeNearAsync(userId, LoginIds.Suggest(user.FirstName, user.LastName, user.Email), cancellationToken).ConfigureAwait(false) ?? string.Empty
            : string.Empty;
        return new LoginIdState(user.LoginId, suggestion, user.PhoneNumberConfirmed);
    }

    /// <inheritdoc />
    public async Task<LoginIdCheck> CheckAsync(Guid userId, string candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        string value = LoginIds.Normalise(candidate);
        string? problem = LoginIds.Problem(value);
        if (problem is not null)
        {
            return new LoginIdCheck(value, problem, null);
        }

        if (await IsFreeAsync(userId, value, cancellationToken).ConfigureAwait(false))
        {
            return new LoginIdCheck(value, null, null);
        }

        return new LoginIdCheck(value, LoginIdCheck.ProblemTaken, await FreeNearAsync(userId, value, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<LoginIdCheck> SetAsync(Guid userId, string candidate, string? ipAddress, CancellationToken cancellationToken = default)
    {
        LoginIdCheck check = await CheckAsync(userId, candidate, cancellationToken).ConfigureAwait(false);
        if (!check.Ok)
        {
            return check;
        }

        SangamUser user = await _db.Users.FirstAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        string? previous = user.LoginId;
        if (string.Equals(previous, check.Value, StringComparison.Ordinal))
        {
            return check;
        }

        DateTimeOffset now = _clock.UtcNow;
        if (previous is not null)
        {
            _db.LoginIdReleases.Add(new LoginIdRelease { Id = Guid.NewGuid(), LoginId = previous, UserId = userId, ReleasedAt = now });
        }

        user.LoginId = check.Value;
        user.UpdatedAt = now;
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Someone took it between the check and the save: the unique index decides.
            _db.ChangeTracker.Clear();
            return new LoginIdCheck(check.Value, LoginIdCheck.ProblemTaken, await FreeNearAsync(userId, check.Value, cancellationToken).ConfigureAwait(false));
        }

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.UserLoginIdSet,
                AuditActorType.User,
                userId,
                TargetType: "user",
                TargetId: userId,
                Metadata: JsonSerializer.Serialize(new { login_id = check.Value, previous }),
                IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        return check;
    }

    /// <inheritdoc />
    public async Task RemoveAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        SangamUser user = await _db.Users.FirstAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        if (user.LoginId is not string previous)
        {
            return;
        }

        DateTimeOffset now = _clock.UtcNow;
        _db.LoginIdReleases.Add(new LoginIdRelease { Id = Guid.NewGuid(), LoginId = previous, UserId = userId, ReleasedAt = now });
        user.LoginId = null;
        user.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.UserLoginIdRemove,
                AuditActorType.User,
                userId,
                TargetType: "user",
                TargetId: userId,
                Metadata: JsonSerializer.Serialize(new { previous }),
                IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Free for this person: nobody else holds it, and nobody else gave it up within the hold.</summary>
    private async Task<bool> IsFreeAsync(Guid userId, string value, CancellationToken cancellationToken)
    {
        DateTimeOffset since = _clock.UtcNow - LoginIds.ReleaseHold;
        bool held = await _db.Users.AsNoTracking().AnyAsync(u => u.LoginId == value && u.Id != userId, cancellationToken).ConfigureAwait(false);
        return !held && !await _db.LoginIdReleases.AsNoTracking()
            .AnyAsync(r => r.LoginId == value && r.UserId != userId && r.ReleasedAt > since, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><paramref name="value"/> when it is free, else the first free numbered variant, else <see langword="null"/>.</summary>
    private async Task<string?> FreeNearAsync(Guid userId, string value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(value) || LoginIds.Problem(value) is not null)
        {
            return null;
        }

        if (await IsFreeAsync(userId, value, cancellationToken).ConfigureAwait(false))
        {
            return value;
        }

        for (int n = 2; n < Alternatives + 2; n++)
        {
            string candidate = LoginIds.Numbered(value, n);
            if (LoginIds.Problem(candidate) is null && await IsFreeAsync(userId, candidate, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        return null;
    }
}
