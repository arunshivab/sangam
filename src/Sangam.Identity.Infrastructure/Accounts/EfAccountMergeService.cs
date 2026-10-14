using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using OpenIddict.Abstractions;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Application.Verification;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Admin;
using Sangam.Identity.Infrastructure.Maintenance;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Provisioning;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Verification;

namespace Sangam.Identity.Infrastructure.Accounts;

/// <summary>
/// <see cref="IAccountMergeService"/> (rc.7, SGM-914 section 9). The founder's rules of 11 October 2026: the surviving
/// account's own wins wherever both hold the same thing; applications are told with <c>account.merged</c> and the closed
/// id answers "merged into"; the closed account's e-mail and mobile are released; the merge waits 24 hours (72 when
/// either account is privileged) with a cancel link to both; operators' accounts, inactive accounts and two accounts
/// verified with different DigiLocker identities are refused. An application administrator's role moves with the
/// account, and the application's other owners are told.
/// </summary>
public sealed class EfAccountMergeService : IAccountMergeService
{
    /// <summary>How many "merged into" links are followed before giving up (a loop is impossible, but cheap to guard).</summary>
    private const int MaxHops = 10;

    private readonly SangamDbContext _db;
    private readonly IPortalService _portal;
    private readonly IEmailSender _email;
    private readonly IMessageTemplates _templates;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly IConfiguration _configuration;
    private readonly EfIdentityVerificationService _verification;
    private readonly IOpenIddictAuthorizationManager _authorizations;
    private readonly IOpenIddictTokenManager _tokens;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="portal">Portal service, to end the closed account's sessions.</param>
    /// <param name="email">E-mail.</param>
    /// <param name="templates">Message templates.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="configuration">Configuration (<c>Sangam:Recovery</c> waiting periods, the identity server's address).</param>
    /// <param name="verification">Identity verification, to carry a DigiLocker identity to the surviving account.</param>
    /// <param name="authorizations">OpenIddict authorizations, to revoke the closed account's.</param>
    /// <param name="tokens">OpenIddict tokens, to revoke the closed account's.</param>
    public EfAccountMergeService(SangamDbContext db, IPortalService portal, IEmailSender email, IMessageTemplates templates, IAuditWriter audit, IClock clock, IConfiguration configuration, EfIdentityVerificationService verification, IOpenIddictAuthorizationManager authorizations, IOpenIddictTokenManager tokens)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _portal = portal ?? throw new ArgumentNullException(nameof(portal));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _verification = verification ?? throw new ArgumentNullException(nameof(verification));
        _authorizations = authorizations ?? throw new ArgumentNullException(nameof(authorizations));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    /// <inheritdoc />
    public async Task<string?> CanStartAsync(Guid userId, CancellationToken cancellationToken = default)
        => await AccountRefusalAsync(userId, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<MergePreview> PreviewAsync(Guid firstUserId, Guid secondUserId, CancellationToken cancellationToken = default)
    {
        MergeAccountView first = await ViewAsync(firstUserId, cancellationToken).ConfigureAwait(false);
        MergeAccountView second = await ViewAsync(secondUserId, cancellationToken).ConfigureAwait(false);
        string? refusal = await RefusalAsync(firstUserId, secondUserId, cancellationToken).ConfigureAwait(false);
        bool privileged = await IsPrivilegedAsync(firstUserId, cancellationToken).ConfigureAwait(false) || await IsPrivilegedAsync(secondUserId, cancellationToken).ConfigureAwait(false);
        return new MergePreview(
            first,
            second,
            privileged,
            (int)EfMfaResetService.CoolingOff(_configuration, privileged).TotalHours,
            refusal,
            refusal is null ? await LossesAsync(firstUserId, secondUserId, cancellationToken).ConfigureAwait(false) : [],
            refusal is null ? await LossesAsync(secondUserId, firstUserId, cancellationToken).ConfigureAwait(false) : []);
    }

    /// <inheritdoc />
    public async Task<MergeOutcome> RequestAsync(Guid survivorUserId, Guid closedUserId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        string? refusal = await RefusalAsync(survivorUserId, closedUserId, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return new MergeOutcome(false, refusal);
        }

        DateTimeOffset now = _clock.UtcNow;
        bool privileged = await IsPrivilegedAsync(survivorUserId, cancellationToken).ConfigureAwait(false) || await IsPrivilegedAsync(closedUserId, cancellationToken).ConfigureAwait(false);
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        AccountMergeRequest request = new()
        {
            Id = Guid.NewGuid(),
            SurvivorUserId = survivorUserId,
            ClosedUserId = closedUserId,
            Privileged = privileged,
            RequestedAt = now,
            EffectiveAt = now + EfMfaResetService.CoolingOff(_configuration, privileged),
            CancelTokenHash = Hash(token),
        };
        _db.AccountMergeRequests.Add(request);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.Entry(request).State = EntityState.Detached;

        string metadata = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["request_id"] = request.Id,
            ["survivor"] = survivorUserId,
            ["closed"] = closedUserId,
            ["privileged"] = privileged,
            ["effective_at"] = request.EffectiveAt.ToString("O", CultureInfo.InvariantCulture),
        });
        foreach (Guid account in new[] { survivorUserId, closedUserId })
        {
            await _audit.WriteAsync(new AuditEntry(AuditActions.UserAccountMergeRequest, AuditActorType.User, account, TargetType: "user", TargetId: account, Metadata: metadata, IpAddress: ipAddress), cancellationToken).ConfigureAwait(false);
        }

        var people = await _db.Users.AsNoTracking().Where(u => u.Id == survivorUserId || u.Id == closedUserId)
            .Select(u => new { u.Id, u.Email, u.FirstName, u.LastName, u.Locale }).ToListAsync(cancellationToken).ConfigureAwait(false);
        string survivorEmail = LogRedaction.MaskEmail(people.First(p => p.Id == survivorUserId).Email ?? string.Empty);
        string closedEmail = LogRedaction.MaskEmail(people.First(p => p.Id == closedUserId).Email ?? string.Empty);
        foreach (var person in people)
        {
            await SendAsync(person.Email, $"{person.FirstName} {person.LastName}".Trim(), person.Locale, MessageTemplateKinds.AccountMergeRequested, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = person.FirstName,
                ["survivor"] = survivorEmail,
                ["closed"] = closedEmail,
                ["hours"] = ((int)Math.Round((request.EffectiveAt - now).TotalHours)).ToString(CultureInfo.InvariantCulture),
                ["effective"] = request.EffectiveAt.ToOffset(Sangam.Shared.IndiaTime.Offset).ToString("d MMM yyyy, HH:mm 'IST'", CultureInfo.InvariantCulture),
                ["link"] = CancelLink(token),
            }, cancellationToken).ConfigureAwait(false);
        }

        return new MergeOutcome(true, null, request.EffectiveAt);
    }

    /// <inheritdoc />
    public async Task<PendingMerge?> PendingAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        AccountMergeRequest? request = await _db.AccountMergeRequests.AsNoTracking()
            .Where(r => (r.SurvivorUserId == userId || r.ClosedUserId == userId) && r.CancelledAt == null && r.AppliedAt == null)
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return request is null ? null : await ViewAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PendingMerge?> FindByTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        string hash = Hash(token);
        AccountMergeRequest? request = await _db.AccountMergeRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.CancelTokenHash == hash && r.CancelledAt == null && r.AppliedAt == null, cancellationToken).ConfigureAwait(false);
        return request is null ? null : await ViewAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> CancelByTokenAsync(string token, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        string hash = Hash(token);
        AccountMergeRequest? request = await _db.AccountMergeRequests.AsNoTracking().FirstOrDefaultAsync(r => r.CancelTokenHash == hash, cancellationToken).ConfigureAwait(false);
        return request is not null && await CancelAsync(request, "owner", null, ipAddress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> CancelByOwnerAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        AccountMergeRequest? request = await _db.AccountMergeRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => (r.SurvivorUserId == userId || r.ClosedUserId == userId) && r.CancelledAt == null && r.AppliedAt == null, cancellationToken).ConfigureAwait(false);
        return request is not null && await CancelAsync(request, "owner", null, ipAddress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> ApplyDueAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        List<AccountMergeRequest> due = await _db.AccountMergeRequests.AsNoTracking()
            .Where(r => r.CancelledAt == null && r.AppliedAt == null && r.EffectiveAt <= now)
            .OrderBy(r => r.EffectiveAt)
            .Take(50)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        int applied = 0;
        foreach (AccountMergeRequest request in due)
        {
            // The rules are checked again: an account may have been suspended, held or verified in the meantime.
            string? refusal = await RefusalAsync(request.SurvivorUserId, request.ClosedUserId, cancellationToken, ignoring: request.Id).ConfigureAwait(false);
            if (refusal is not null)
            {
                await CancelAsync(request, "refused", refusal, null, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (await ApplyAsync(request, cancellationToken).ConfigureAwait(false))
            {
                applied++;
            }
        }

        return applied;
    }

    /// <inheritdoc />
    public async Task<MergedInto?> MergedIntoAsync(Guid appId, Guid userId, CancellationToken cancellationToken = default)
    {
        // Only for an application that knew the closed account: it kept its (now revoked) connection.
        bool known = await _db.AppGrants.AsNoTracking().AnyAsync(g => g.AppId == appId && g.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (!known)
        {
            return null;
        }

        Guid current = userId;
        DateTimeOffset? mergedAt = null;
        for (int hop = 0; hop < MaxHops; hop++)
        {
            var next = await _db.AccountMergeRequests.AsNoTracking()
                .Where(r => r.ClosedUserId == current && r.AppliedAt != null)
                .Select(r => new { r.SurvivorUserId, r.AppliedAt })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                break;
            }

            mergedAt ??= next.AppliedAt;
            current = next.SurvivorUserId;
        }

        return mergedAt is DateTimeOffset at ? new MergedInto(current, at) : null;
    }

    // ------------------------------------------------------------------ rules

    /// <summary>Why the two accounts may not be merged, or <see langword="null"/>.</summary>
    private async Task<string?> RefusalAsync(Guid a, Guid b, CancellationToken cancellationToken, Guid? ignoring = null)
    {
        if (a == b)
        {
            return "same";
        }

        if (await AccountRefusalAsync(a, cancellationToken, ignoring).ConfigureAwait(false) is string first)
        {
            return first;
        }

        if (await AccountRefusalAsync(b, cancellationToken, ignoring).ConfigureAwait(false) is string second)
        {
            return second;
        }

        // Two different DigiLocker identities are two different people (SGM-914 section 9).
        List<string> hashes = await _db.IdentityVerifications.AsNoTracking()
            .Where(v => v.UserId == a || v.UserId == b)
            .Select(v => v.Method + ":" + v.SubjectHash)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return hashes.Count == 2 && hashes[0] != hashes[1] ? "different_identity" : null;
    }

    private async Task<string?> AccountRefusalAsync(Guid userId, CancellationToken cancellationToken, Guid? ignoring = null)
    {
        var user = await _db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.Status, u.HoldPlacedAt, u.PurgeAfter })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (user is null || user.Status != UserStatus.Active || user.HoldPlacedAt != null || user.PurgeAfter != null)
        {
            return "inactive";
        }

        if (await _db.PlatformOperators.AsNoTracking().AnyAsync(o => o.UserId == userId && o.RevokedAt == null, cancellationToken).ConfigureAwait(false))
        {
            return "operator";
        }

        bool merging = await _db.AccountMergeRequests.AsNoTracking()
            .AnyAsync(r => (r.SurvivorUserId == userId || r.ClosedUserId == userId) && r.CancelledAt == null && r.AppliedAt == null && r.Id != ignoring, cancellationToken).ConfigureAwait(false);
        bool recovering = await _db.MfaResetRequests.AsNoTracking()
            .AnyAsync(r => r.UserId == userId && r.CancelledAt == null && r.AppliedAt == null, cancellationToken).ConfigureAwait(false);
        return merging || recovering ? "pending" : null;
    }

    private async Task<bool> IsPrivilegedAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _db.AppAdmins.AsNoTracking().AnyAsync(a => a.UserId == userId && a.RevokedAt == null, cancellationToken).ConfigureAwait(false)
            || await _db.OrgAdmins.AsNoTracking().AnyAsync(a => a.UserId == userId && a.RevokedAt == null, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ views

    private async Task<MergeAccountView> ViewAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.FirstName, u.LastName, u.Email, u.IdentityVerifiedAt })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        List<string> apps = await _db.AppGrants.AsNoTracking()
            .Where(g => g.UserId == userId && g.RevokedAt == null && !g.App!.IsPlatform)
            .OrderBy(g => g.App!.DisplayName)
            .Select(g => g.App!.DisplayName)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<string> admin = await _db.AppAdmins.AsNoTracking()
            .Where(a => a.UserId == userId && a.RevokedAt == null)
            .OrderBy(a => a.App!.DisplayName)
            .Select(a => a.App!.DisplayName)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return user is null
            ? new MergeAccountView(userId, string.Empty, string.Empty, false, [], [])
            : new MergeAccountView(userId, $"{user.FirstName} {user.LastName}".Trim(), user.Email ?? string.Empty, user.IdentityVerifiedAt != null, apps, admin);
    }

    private async Task<PendingMerge> ViewAsync(AccountMergeRequest r, CancellationToken cancellationToken)
    {
        Dictionary<Guid, string?> emails = await _db.Users.AsNoTracking().Where(u => u.Id == r.SurvivorUserId || u.Id == r.ClosedUserId)
            .ToDictionaryAsync(u => u.Id, u => u.Email, cancellationToken).ConfigureAwait(false);
        return new PendingMerge(r.Id, r.SurvivorUserId, r.ClosedUserId,
            LogRedaction.MaskEmail(emails.GetValueOrDefault(r.SurvivorUserId) ?? string.Empty),
            LogRedaction.MaskEmail(emails.GetValueOrDefault(r.ClosedUserId) ?? string.Empty),
            r.RequestedAt, r.EffectiveAt);
    }

    /// <summary>What <paramref name="closed"/> would lose, application by application, if <paramref name="survivor"/> stays.</summary>
    private async Task<IReadOnlyList<MergeLoss>> LossesAsync(Guid survivor, Guid closed, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        var memberships = await _db.OrgMemberships.AsNoTracking()
            .Where(m => m.UserId == closed && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now))
            .Where(m => _db.OrgMemberships.Any(s => s.UserId == survivor && s.OrgId == m.OrgId && s.AppId == m.AppId && s.RevokedAt == null && (s.ExpiresAt == null || s.ExpiresAt > now)))
            .GroupBy(m => m.AppId).Select(g => new { AppId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var attributes = await _db.UserAttributeValues.AsNoTracking()
            .Where(v => v.UserId == closed && _db.UserAttributeValues.Any(s => s.UserId == survivor && s.DefinitionId == v.DefinitionId))
            .Join(_db.UserAttributeDefinitions, v => v.DefinitionId, d => d.Id, (v, d) => d.AppId)
            .GroupBy(app => app).Select(g => new { AppId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<Guid> apps = [.. memberships.Select(m => m.AppId).Union(attributes.Select(a => a.AppId))];
        Dictionary<Guid, string> names = await _db.Apps.AsNoTracking().Where(a => apps.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.DisplayName, cancellationToken).ConfigureAwait(false);
        return [.. apps
            .Select(app => new MergeLoss(names.GetValueOrDefault(app) ?? string.Empty, memberships.FirstOrDefault(m => m.AppId == app)?.Count ?? 0, attributes.FirstOrDefault(a => a.AppId == app)?.Count ?? 0))
            .OrderBy(l => l.Application, StringComparer.Ordinal)];
    }

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// Merges once — whichever host claims it first. Everything the closed account holds is copied to the survivor and
    /// revoked on the closed account (so each application's history stays true), except where the survivor already holds
    /// the same thing; attribute values move; a DigiLocker identity moves; the closed account is pseudonymised.
    /// </summary>
    private async Task<bool> ApplyAsync(AccountMergeRequest request, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        Guid survivor = request.SurvivorUserId;
        Guid closed = request.ClosedUserId;

        IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        MergeMoves? moves;
        await using (transaction.ConfigureAwait(false))
        {
            moves = await MoveAsync(request, now, cancellationToken).ConfigureAwait(false);
            if (moves is null)
            {
                return false;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await _portal.RevokeAllSessionsAsync(closed, null, cancellationToken).ConfigureAwait(false);
        await RevokeTokensAsync(closed, cancellationToken).ConfigureAwait(false);

        string summary = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["request_id"] = request.Id,
            ["survivor"] = survivor,
            ["closed"] = closed,
            ["applications"] = moves.Applications,
            ["memberships_moved"] = moves.MembershipsMoved,
            ["memberships_kept_by_survivor"] = moves.MembershipsDropped,
            ["attributes_moved"] = moves.AttributesMoved,
            ["attributes_kept_by_survivor"] = moves.AttributesDropped,
            ["identity_moved"] = moves.IdentityMoved,
            ["admin_roles"] = moves.AdminMoves.Count,
        });
        await _audit.WriteAsync(new AuditEntry(AuditActions.UserAccountMerge, AuditActorType.System, survivor, TargetType: "user", TargetId: survivor, Metadata: summary), cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.UserAccountMerge, AuditActorType.System, survivor, TargetType: "user", TargetId: closed, Metadata: summary), cancellationToken).ConfigureAwait(false);

        SangamUser survivorUser = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == survivor, cancellationToken).ConfigureAwait(false);
        await SendAsync(survivorUser.Email, survivorUser.DisplayName, survivorUser.Locale, MessageTemplateKinds.AccountMerged,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = survivorUser.FirstName, ["closed"] = moves.ClosedEmail }, cancellationToken).ConfigureAwait(false);
        foreach ((Guid appId, AppAdminRole role) in moves.AdminMoves)
        {
            await TellOwnersAsync(appId, survivorUser, closed, role, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>The database part of a merge, inside its transaction; <see langword="null"/> when another host claimed it.</summary>
    private async Task<MergeMoves?> MoveAsync(AccountMergeRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Guid survivor = request.SurvivorUserId;
        Guid closed = request.ClosedUserId;
        int claimed = await _db.AccountMergeRequests
            .Where(r => r.Id == request.Id && r.CancelledAt == null && r.AppliedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.AppliedAt, now), cancellationToken).ConfigureAwait(false);
        if (claimed == 0)
        {
            return null;
        }

        SangamUser closedUser = await _db.Users.FirstAsync(u => u.Id == closed, cancellationToken).ConfigureAwait(false);
        SangamUser survivorUser = await _db.Users.FirstAsync(u => u.Id == survivor, cancellationToken).ConfigureAwait(false);
        string closedEmail = LogRedaction.MaskEmail(closedUser.Email ?? string.Empty);

        // Applications: tell each one the closed account knew, before its connection goes.
        List<AppGrant> closedGrants = await _db.AppGrants.Where(g => g.UserId == closed && g.RevokedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        HashSet<Guid> survivorApps = [.. await _db.AppGrants.Where(g => g.UserId == survivor && g.RevokedAt == null).Select(g => g.AppId).ToListAsync(cancellationToken).ConfigureAwait(false)];
        foreach (AppGrant grant in closedGrants)
        {
            await AppEventLog.AddAsync(_db, AppEventTypes.AccountMerged, grant.AppId, closed, null, new Dictionary<string, object?> { ["merged_into"] = survivor.ToString("D") }, now, cancellationToken).ConfigureAwait(false);
            if (!survivorApps.Contains(grant.AppId))
            {
                _db.AppGrants.Add(new AppGrant { Id = Guid.NewGuid(), UserId = survivor, AppId = grant.AppId, GrantedAt = grant.GrantedAt, LastUsedAt = grant.LastUsedAt });
            }

            grant.RevokedAt = now;
        }

        // Consents.
        List<Consent> closedConsents = await _db.Consents.Where(c => c.UserId == closed && c.RevokedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        HashSet<Guid> survivorConsents = [.. await _db.Consents.Where(c => c.UserId == survivor && c.RevokedAt == null).Select(c => c.AppId).ToListAsync(cancellationToken).ConfigureAwait(false)];
        foreach (Consent consent in closedConsents)
        {
            if (!survivorConsents.Contains(consent.AppId))
            {
                _db.Consents.Add(new Consent { Id = Guid.NewGuid(), UserId = survivor, AppId = consent.AppId, Scope = consent.Scope, ConsentVersion = consent.ConsentVersion, GrantedAt = consent.GrantedAt });
            }

            consent.RevokedAt = now;
        }

        // Roles at organisations: the survivor's own wins at an organisation where both are members.
        List<OrgMembership> closedMemberships = await _db.OrgMemberships.Where(m => m.UserId == closed && m.RevokedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        HashSet<(Guid AppId, Guid OrgId)> survivorOrgs = [.. (await _db.OrgMemberships.Where(m => m.UserId == survivor && m.RevokedAt == null).Select(m => new { m.AppId, m.OrgId }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(m => (m.AppId, m.OrgId))];
        int membershipsMoved = 0;
        foreach (OrgMembership membership in closedMemberships)
        {
            if (!survivorOrgs.Contains((membership.AppId, membership.OrgId)) && (membership.ExpiresAt == null || membership.ExpiresAt > now))
            {
                _db.OrgMemberships.Add(new OrgMembership
                {
                    Id = Guid.NewGuid(),
                    UserId = survivor,
                    OrgId = membership.OrgId,
                    AppId = membership.AppId,
                    RoleId = membership.RoleId,
                    GrantedByUserId = membership.GrantedByUserId,
                    GrantedAt = membership.GrantedAt,
                    ExpiresAt = membership.ExpiresAt,
                });
                membershipsMoved++;
            }

            membership.RevokedAt = now;
        }

        // rc.12: customer and branch admin rights (and emergency-review rights) move too, unless the survivor already has them.
        List<OrgAdmin> closedOrgRights = await _db.OrgAdmins.Where(a => a.UserId == closed && a.RevokedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        HashSet<(Guid AppId, Guid OrgId, OrgAdminRight Right)> survivorOrgRights = [.. (await _db.OrgAdmins.Where(a => a.UserId == survivor && a.RevokedAt == null)
            .Select(a => new { a.AppId, a.OrgId, a.Right }).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(a => (a.AppId, a.OrgId, a.Right))];
        foreach (OrgAdmin right in closedOrgRights)
        {
            if (survivorOrgRights.Add((right.AppId, right.OrgId, right.Right)))
            {
                _db.OrgAdmins.Add(new OrgAdmin { Id = Guid.NewGuid(), AppId = right.AppId, OrgId = right.OrgId, UserId = survivor, Right = right.Right, GrantedByUserId = right.GrantedByUserId, GrantedAt = right.GrantedAt });
            }

            right.RevokedAt = now;
        }

        // Administrators: the role moves; where both administer the same application the survivor keeps the higher role,
        // so a merge never leaves an application without its owner.
        List<AppAdmin> closedAdmins = await _db.AppAdmins.Where(a => a.UserId == closed && a.RevokedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<Guid, AppAdmin> survivorAdmins = await _db.AppAdmins.Where(a => a.UserId == survivor && a.RevokedAt == null).ToDictionaryAsync(a => a.AppId, cancellationToken).ConfigureAwait(false);
        List<(Guid AppId, AppAdminRole Role)> adminMoves = [];
        foreach (AppAdmin admin in closedAdmins)
        {
            admin.RevokedAt = now;
            if (survivorAdmins.TryGetValue(admin.AppId, out AppAdmin? existing))
            {
                if (admin.Role == AppAdminRole.Owner && existing.Role != AppAdminRole.Owner)
                {
                    existing.Role = AppAdminRole.Owner;
                }

                adminMoves.Add((admin.AppId, existing.Role));
            }
            else
            {
                _db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = admin.AppId, UserId = survivor, Role = admin.Role, GrantedByUserId = admin.GrantedByUserId, GrantedAt = admin.GrantedAt });
                adminMoves.Add((admin.AppId, admin.Role));
            }
        }

        // The closed account's passkeys and authenticator go with it.
        await _db.PasskeyCredentials.Where(p => p.UserId == closed && p.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.RevokedAt, now), cancellationToken).ConfigureAwait(false);
        await _db.UserTokens.Where(t => t.UserId == closed).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await _db.UserLogins.Where(l => l.UserId == closed).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        closedUser.TwoFactorEnabled = false;

        // Every application the survivor now holds is told, so SCIM brings it in step.
        foreach (Guid app in closedGrants.Select(g => g.AppId).Distinct())
        {
            await AppEventLog.AddAsync(_db, AppEventTypes.UserUpdated, app, survivor, null, new Dictionary<string, object?> { ["merged_from"] = closed.ToString("D") }, now, cancellationToken).ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Values applications keep: they move, except where the survivor has its own for the same key.
        int attributesDropped = await _db.UserAttributeValues
            .Where(v => v.UserId == closed && _db.UserAttributeValues.Any(s => s.UserId == survivor && s.DefinitionId == v.DefinitionId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        int attributesMoved = await _db.UserAttributeValues.Where(v => v.UserId == closed)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.UserId, survivor), cancellationToken).ConfigureAwait(false);

        // A DigiLocker identity on the closed account moves to the survivor (both verified is the same identity or refused).
        IdentityVerification? closedIdentity = await _db.IdentityVerifications.AsNoTracking().FirstOrDefaultAsync(v => v.UserId == closed, cancellationToken).ConfigureAwait(false);
        bool identityMoved = false;
        if (closedIdentity is not null)
        {
            await _db.IdentityVerifications.Where(v => v.UserId == closed).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            if (survivorUser.IdentityVerifiedAt is null)
            {
                VerificationResult moved = await _verification.ApplyHashedAsync(survivor, new VerifiedIdentity(closedIdentity.Method, closedIdentity.SubjectHash, closedIdentity.Name, closedIdentity.DateOfBirth, closedIdentity.Gender), null, cancellationToken).ConfigureAwait(false);
                identityMoved = moved.Succeeded;
            }
        }

        // The closed account: its e-mail and mobile are released, its personal data destroyed; the row stays for the links.
        AccountPurgeService.Pseudonymise(closedUser, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new MergeMoves(closedEmail, closedGrants.Count, membershipsMoved, closedMemberships.Count - membershipsMoved, attributesMoved, attributesDropped, identityMoved, adminMoves);
    }

    /// <summary>The application's own audit records the move, and its other owners are told by e-mail.</summary>
    private async Task TellOwnersAsync(Guid appId, SangamUser survivor, Guid closed, AppAdminRole role, CancellationToken cancellationToken)
    {
        await _audit.WriteAsync(new AuditEntry(AuditActions.AppAdminMerge, AuditActorType.System, survivor.Id, appId, "app", appId,
            Metadata: JsonSerializer.Serialize(new Dictionary<string, object> { ["from_user"] = closed, ["to_user"] = survivor.Id, ["role"] = role == AppAdminRole.Owner ? "owner" : "admin" })), cancellationToken).ConfigureAwait(false);
        string application = await _db.Apps.AsNoTracking().Where(a => a.Id == appId).Select(a => a.DisplayName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        var owners = await _db.AppAdmins.AsNoTracking()
            .Where(a => a.AppId == appId && a.RevokedAt == null && a.Role == AppAdminRole.Owner && a.UserId != survivor.Id)
            .Select(a => new { a.User!.Email, a.User.FirstName, a.User.LastName, a.User.Locale })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var owner in owners)
        {
            await SendAsync(owner.Email, $"{owner.FirstName} {owner.LastName}".Trim(), owner.Locale, MessageTemplateKinds.AppAdminMerged,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = owner.FirstName, ["application"] = application, ["admin"] = $"{survivor.DisplayName} ({survivor.Email})" }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> CancelAsync(AccountMergeRequest request, string by, string? reason, string? ipAddress, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        int cancelled = await _db.AccountMergeRequests
            .Where(r => r.Id == request.Id && r.CancelledAt == null && r.AppliedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CancelledAt, now).SetProperty(r => r.CancelledBy, by), cancellationToken).ConfigureAwait(false);
        if (cancelled == 0)
        {
            return false;
        }

        Dictionary<string, object> metadata = new() { ["request_id"] = request.Id, ["by"] = by };
        if (reason is not null)
        {
            metadata["reason"] = reason;
        }

        string json = JsonSerializer.Serialize(metadata);
        foreach (Guid account in new[] { request.SurvivorUserId, request.ClosedUserId })
        {
            await _audit.WriteAsync(new AuditEntry(AuditActions.UserAccountMergeCancel, by == "owner" ? AuditActorType.User : AuditActorType.System, by == "owner" ? account : null, TargetType: "user", TargetId: account, Metadata: json, IpAddress: ipAddress), cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task RevokeTokensAsync(Guid userId, CancellationToken cancellationToken)
    {
        List<object> found = [];
        await foreach (object authorization in _authorizations.FindAsync(userId.ToString("D"), null, null, null, null, cancellationToken).ConfigureAwait(false))
        {
            found.Add(authorization);
        }

        foreach (object authorization in found)
        {
            string? id = await _authorizations.GetIdAsync(authorization, cancellationToken).ConfigureAwait(false);
            if (id is not null)
            {
                await _tokens.RevokeByAuthorizationIdAsync(id, cancellationToken).ConfigureAwait(false);
            }

            await _authorizations.TryRevokeAsync(authorization, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(string? email, string name, string locale, string kind, Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(email))
        {
            await _email.SendAsync(await _templates.EmailAsync(kind, locale, null, null, values, email, name, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
    }

    private string CancelLink(string token)
    {
        string origin = (_configuration["Sangam:Issuer"] ?? _configuration["Sangam:Authority"] ?? "https://id.sangamid.in").TrimEnd('/');
        return origin + "/account/merge/cancel/" + token;
    }

    /// <summary>What a merge moved, for the audit and the notices.</summary>
    private sealed record MergeMoves(string ClosedEmail, int Applications, int MembershipsMoved, int MembershipsDropped, int AttributesMoved, int AttributesDropped, bool IdentityMoved, List<(Guid AppId, AppAdminRole Role)> AdminMoves);

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
