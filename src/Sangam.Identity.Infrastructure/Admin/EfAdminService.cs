using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Accounts;
using Sangam.Identity.Infrastructure.Maintenance;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Admin;

/// <summary><see cref="IAdminService"/> over the identity database.</summary>
public sealed partial class EfAdminService : IAdminService
{
    private readonly SangamDbContext _db;
    private readonly IPortalService _portal;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly UserManager<SangamUser> _users;
    private readonly IEmailSender _email;

    /// <summary>Initialises the service.</summary>
    public EfAdminService(SangamDbContext db, IPortalService portal, IAuditWriter audit, IClock clock, UserManager<SangamUser> users, IEmailSender email)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _portal = portal ?? throw new ArgumentNullException(nameof(portal));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _email = email ?? throw new ArgumentNullException(nameof(email));
    }

    /// <inheritdoc />
    public async Task<PlatformRole?> GetRoleAsync(Guid operatorUserId, CancellationToken cancellationToken = default)
    {
        PlatformOperator? op = await _db.PlatformOperators.AsNoTracking()
            .FirstOrDefaultAsync(o => o.UserId == operatorUserId && o.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        return op?.Role;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdminUserRow>> SearchUsersAsync(Guid operatorUserId, string? query, int take, CancellationToken cancellationToken = default)
    {
        if (await GetRoleAsync(operatorUserId, cancellationToken).ConfigureAwait(false) is null)
        {
            return [];
        }

        take = Math.Clamp(take, 1, 200);
        IQueryable<SangamUser> users = _db.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query))
        {
            string term = query.Trim();
            string digits = new([.. term.Where(char.IsDigit)]);
            users = users.Where(u =>
                EF.Functions.ILike(u.Email!, $"%{term}%")
                || EF.Functions.ILike(u.FirstName + " " + u.LastName, $"%{term}%")
                || (digits.Length >= 4 && u.PhoneNumber != null && EF.Functions.ILike(u.PhoneNumber, $"%{digits}%")));
        }

        var rows = await users
            .OrderByDescending(u => u.CreatedAt)
            .Take(take)
            .Select(u => new
            {
                u.Id,
                u.FirstName,
                u.LastName,
                u.Email,
                u.PhoneNumber,
                u.Status,
                u.EmailConfirmed,
                u.TwoFactorEnabled,
                u.CreatedAt,
                u.PurgeAfter,
                u.HoldPlacedAt,
                IsOperator = _db.PlatformOperators.Any(o => o.UserId == u.Id && o.RevokedAt == null),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. rows.Select(r => new AdminUserRow(
                r.Id,
                (r.FirstName + " " + r.LastName).Trim(),
                r.Email ?? string.Empty,
                r.PhoneNumber,
                r.Status,
                r.EmailConfirmed,
                r.TwoFactorEnabled,
                r.IsOperator,
                r.CreatedAt,
                r.PurgeAfter,
                r.HoldPlacedAt is not null)),
        ];
    }

    /// <inheritdoc />
    public async Task<AdminUserDetail?> OpenUserAsync(Guid operatorUserId, Guid userId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        if (await GetRoleAsync(operatorUserId, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        SangamUser? user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        // Looking at someone's record is itself a privacy event: it is audited like any change,
        // and the user sees it in their own log.
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AdminUserRead, AuditActorType.Admin, operatorUserId, TargetType: "user", TargetId: userId, IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);

        DateTimeOffset? lastSignIn = await _db.AuditEvents.AsNoTracking()
            .Where(e => e.ActorUserId == userId && e.Action == AuditActions.UserLoginSuccess)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => (DateTimeOffset?)e.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        int sessions = await _db.UserSessions.CountAsync(s => s.UserId == userId && s.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        List<string> apps = await _db.AppGrants.AsNoTracking()
            .Where(g => g.UserId == userId && g.RevokedAt == null)
            .Select(g => g.App!.DisplayName)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<string> orgs = await _db.OrgMemberships.AsNoTracking()
            .Where(m => m.UserId == userId && m.RevokedAt == null)
            .Select(m => m.Org!.Name)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        bool isOperator = await _db.PlatformOperators.AnyAsync(o => o.UserId == userId && o.RevokedAt == null, cancellationToken).ConfigureAwait(false);

        AdminUserRow row = new(
            user.Id,
            (user.FirstName + " " + user.LastName).Trim(),
            user.Email ?? string.Empty,
            user.PhoneNumber,
            user.Status,
            user.EmailConfirmed,
            user.TwoFactorEnabled,
            isOperator,
            user.CreatedAt,
            user.PurgeAfter,
            user.HoldPlacedAt is not null);

        return new AdminUserDetail(row, user.DateOfBirth, user.Gender, user.SignInPreference, lastSignIn, sessions, apps, orgs, user.HoldReason);
    }

    /// <inheritdoc />
    public async Task<AdminResult> SuspendUserAsync(Guid operatorUserId, Guid userId, string reason, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        AdminResult? refusal = await RequireAsync(operatorUserId, PlatformRole.Support, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        SangamUser? user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return AdminResult.Refused("That user does not exist.");
        }

        if (user.Status == UserStatus.DeletedHard)
        {
            return AdminResult.Refused("That account has already been deleted.");
        }

        user.Status = UserStatus.Suspended;
        user.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _portal.RevokeAllSessionsAsync(userId, ipAddress, cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AdminUserSuspend, AuditActorType.Admin, operatorUserId, TargetType: "user", TargetId: userId,
                Metadata: Reason(reason), IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        return AdminResult.Ok("The account is suspended and every session has ended.");
    }

    /// <inheritdoc />
    public async Task<AdminResult> ReinstateUserAsync(Guid operatorUserId, Guid userId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        AdminResult? refusal = await RequireAsync(operatorUserId, PlatformRole.Support, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        SangamUser? user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.Status != UserStatus.Suspended)
        {
            return AdminResult.Refused("That account is not suspended.");
        }

        user.Status = UserStatus.Active;
        user.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AdminUserReinstate, AuditActorType.Admin, operatorUserId, TargetType: "user", TargetId: userId, IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        return AdminResult.Ok("The account is active again.");
    }

    /// <inheritdoc />
    public async Task<AdminResult> ForceSignOutAsync(Guid operatorUserId, Guid userId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        AdminResult? refusal = await RequireAsync(operatorUserId, PlatformRole.Support, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!await _db.Users.AnyAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false))
        {
            return AdminResult.Refused("That user does not exist.");
        }

        await _portal.RevokeAllSessionsAsync(userId, ipAddress, cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AdminUserForceLogout, AuditActorType.Admin, operatorUserId, TargetType: "user", TargetId: userId, IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        return AdminResult.Ok("Every session has ended.");
    }

    /// <inheritdoc />
    public async Task<AdminResult> PlaceHoldAsync(Guid operatorUserId, Guid userId, string reason, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        AdminResult? refusal = await RequireAsync(operatorUserId, PlatformRole.Support, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        SangamUser? user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return AdminResult.Refused("That user does not exist.");
        }

        user.HoldPlacedAt = _clock.UtcNow;
        user.HoldReason = reason.Trim();
        user.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AdminUserHoldPlace, AuditActorType.Admin, operatorUserId, TargetType: "user", TargetId: userId,
                Metadata: Reason(reason), IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        return AdminResult.Ok("The purge will not run while the hold is in place.");
    }

    /// <inheritdoc />
    public async Task<AdminResult> ClearHoldAsync(Guid operatorUserId, Guid userId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        AdminResult? refusal = await RequireAsync(operatorUserId, PlatformRole.Support, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        SangamUser? user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.HoldPlacedAt is null)
        {
            return AdminResult.Refused("There is no hold on that account.");
        }

        user.HoldPlacedAt = null;
        user.HoldReason = null;
        user.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AdminUserHoldClear, AuditActorType.Admin, operatorUserId, TargetType: "user", TargetId: userId, IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        return AdminResult.Ok("The hold is cleared.");
    }

    /// <inheritdoc />
    public async Task<AdminResult> DeleteNowAsync(Guid operatorUserId, Guid userId, string reason, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        AdminResult? refusal = await RequireAsync(operatorUserId, PlatformRole.Owner, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        SangamUser? user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return AdminResult.Refused("That user does not exist.");
        }

        if (user.Status == UserStatus.DeletedHard)
        {
            return AdminResult.Refused("That account has already been deleted.");
        }

        if (await _db.PlatformOperators.AnyAsync(o => o.UserId == userId && o.RevokedAt == null, cancellationToken).ConfigureAwait(false))
        {
            return AdminResult.Refused("Revoke this person's console access before deleting their account.");
        }

        await _portal.RevokeAllSessionsAsync(userId, ipAddress, cancellationToken).ConfigureAwait(false);
        AccountPurgeService.Pseudonymise(user, _clock.UtcNow);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AdminUserDeleteNow, AuditActorType.Admin, operatorUserId, TargetType: "user", TargetId: userId,
                Metadata: Reason(reason), IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        return AdminResult.Ok("The account's personal data has been destroyed. The audit trail remains.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdminAppRow>> ListAppsAsync(Guid operatorUserId, CancellationToken cancellationToken = default)
    {
        if (await GetRoleAsync(operatorUserId, cancellationToken).ConfigureAwait(false) is null)
        {
            return [];
        }

        var rows = await _db.Apps.AsNoTracking()
            .OrderBy(a => a.DisplayName)
            .Select(a => new
            {
                a.Id,
                a.ClientId,
                a.DisplayName,
                a.OwnerCompanyName,
                a.Status,
                a.SignInPolicy,
                Users = _db.AppGrants.Count(g => g.AppId == a.Id && g.RevokedAt == null),
                Organisations = _db.Organisations.Count(o => o.RegisteredViaAppId == a.Id),
                PartnerOwners = _db.AppAdmins.Count(x => x.AppId == a.Id && x.Role == AppAdminRole.Owner && x.RevokedAt == null),
                a.IsPlatform,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(r => new AdminAppRow(r.Id, r.ClientId, r.DisplayName, r.OwnerCompanyName, r.Status, r.SignInPolicy, r.Users, r.Organisations, r.PartnerOwners, r.IsPlatform))];
    }

    /// <inheritdoc />
    public async Task<AdminResult> SetAppStatusAsync(Guid operatorUserId, Guid appId, AppStatus status, string? ipAddress, CancellationToken cancellationToken = default)
    {
        AdminResult? refusal = await RequireAsync(operatorUserId, PlatformRole.AppManager, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        App? app = await _db.Apps.FirstOrDefaultAsync(a => a.Id == appId, cancellationToken).ConfigureAwait(false);
        if (app is null)
        {
            return AdminResult.Refused("That application does not exist.");
        }

        if (app.IsPlatform && status != AppStatus.Active)
        {
            return AdminResult.Refused($"{app.DisplayName} is part of Sangam itself. Disabling it here could lock every operator out of the tool needed to turn it back on.");
        }

        app.Status = status;
        app.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AdminAppUpdate, AuditActorType.Admin, operatorUserId, appId, "app", appId,
                Metadata: $"{{\"status\":\"{status.ToString().ToLowerInvariant()}\"}}", IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);

        return AdminResult.Ok(status == AppStatus.Active
            ? $"{app.DisplayName} can sign users in again."
            : $"{app.DisplayName} can no longer sign anyone in.");
    }

    /// <inheritdoc />
    public async Task<AdminResult> AssignAppOwnerAsync(Guid operatorUserId, Guid appId, string email, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        AdminResult? refusal = await RequireAsync(operatorUserId, PlatformRole.AppManager, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        App? app = await _db.Apps.FirstOrDefaultAsync(a => a.Id == appId, cancellationToken).ConfigureAwait(false);
        if (app is null)
        {
            return AdminResult.Refused("That application does not exist.");
        }

        if (app.IsPlatform)
        {
            return AdminResult.Refused($"{app.DisplayName} is part of Sangam itself, not a partner's application. It cannot have partner owners.");
        }

        string normalised = email.Trim().ToUpperInvariant();
        SangamUser? user = await _db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalised, cancellationToken).ConfigureAwait(false);
        if (user is null || user.Status != UserStatus.Active || !user.EmailConfirmed)
        {
            return AdminResult.Refused("No active, verified Sangam account has that email address. They must register first.");
        }

        AppAdmin? existing = await _db.AppAdmins.FirstOrDefaultAsync(a => a.AppId == appId && a.UserId == user.Id && a.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            existing.Role = AppAdminRole.Owner;
        }
        else
        {
            _db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = appId, UserId = user.Id, Role = AppAdminRole.Owner, GrantedByUserId = operatorUserId, GrantedAt = _clock.UtcNow });
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            // Platform capacity: no actor app, or the person's log would call a Sangam operator
            // "an administrator of" the application. The application is named in the metadata.
            new AuditEntry(AuditActions.AppAdminGrant, AuditActorType.Admin, operatorUserId, null, "user", user.Id,
                Metadata: OwnerGrantMetadata(app.DisplayName), IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);

        return AdminResult.Ok(user.TwoFactorEnabled
            ? $"{user.FirstName} now owns {app.DisplayName} on the partner console."
            : $"{user.FirstName} now owns {app.DisplayName}, and must set up an authenticator app before the partner console will let them in.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OperatorRow>> ListOperatorsAsync(Guid operatorUserId, CancellationToken cancellationToken = default)
    {
        if (await GetRoleAsync(operatorUserId, cancellationToken).ConfigureAwait(false) is not PlatformRole.Owner)
        {
            return [];
        }

        var rows = await _db.PlatformOperators.AsNoTracking()
            .Where(o => o.RevokedAt == null)
            .OrderBy(o => o.GrantedAt)
            .Select(o => new
            {
                o.UserId,
                o.Role,
                o.GrantedAt,
                o.GrantedByUserId,
                Name = o.User!.FirstName + " " + o.User.LastName,
                o.User.Email,
                o.User.TwoFactorEnabled,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        Guid[] grantors = [.. rows.Where(r => r.GrantedByUserId is not null).Select(r => r.GrantedByUserId!.Value).Distinct()];
        Dictionary<Guid, string> names = grantors.Length == 0
            ? []
            : await _db.Users.AsNoTracking().Where(u => grantors.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => (u.FirstName + " " + u.LastName).Trim(), cancellationToken).ConfigureAwait(false);

        return
        [
            .. rows.Select(r => new OperatorRow(
                r.UserId,
                r.Name.Trim(),
                r.Email ?? string.Empty,
                r.Role,
                r.TwoFactorEnabled,
                r.GrantedAt,
                r.GrantedByUserId is Guid by && names.TryGetValue(by, out string? name) ? name : null)),
        ];
    }

    /// <inheritdoc />
    public async Task<AdminResult> GrantOperatorAsync(Guid operatorUserId, string email, PlatformRole role, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        AdminResult? refusal = await RequireAsync(operatorUserId, PlatformRole.Owner, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        string normalised = email.Trim().ToUpperInvariant();
        SangamUser? user = await _db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalised, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return AdminResult.Refused("No Sangam account has that email address. They must register first.");
        }

        if (user.Status != UserStatus.Active || !user.EmailConfirmed)
        {
            return AdminResult.Refused("That account is not active and verified.");
        }

        PlatformOperator? existing = await _db.PlatformOperators.FirstOrDefaultAsync(o => o.UserId == user.Id && o.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            existing.Role = role;
        }
        else
        {
            _db.PlatformOperators.Add(new PlatformOperator
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Role = role,
                GrantedByUserId = operatorUserId,
                GrantedAt = _clock.UtcNow,
            });
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AdminOperatorGrant, AuditActorType.Admin, operatorUserId, TargetType: "user", TargetId: user.Id,
                Metadata: $"{{\"role\":\"{role.ToString().ToLowerInvariant()}\"}}", IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);

        return AdminResult.Ok(user.TwoFactorEnabled
            ? $"{user.FirstName} can use the console now."
            : $"{user.FirstName} must set up an authenticator app before the console will let them in.");
    }

    /// <inheritdoc />
    public async Task<AdminResult> RevokeOperatorAsync(Guid operatorUserId, Guid userId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        AdminResult? refusal = await RequireAsync(operatorUserId, PlatformRole.Owner, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        PlatformOperator? op = await _db.PlatformOperators.FirstOrDefaultAsync(o => o.UserId == userId && o.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (op is null)
        {
            return AdminResult.Refused("That person does not have console access.");
        }

        if (op.Role == PlatformRole.Owner)
        {
            int owners = await _db.PlatformOperators.CountAsync(o => o.Role == PlatformRole.Owner && o.RevokedAt == null, cancellationToken).ConfigureAwait(false);
            if (owners <= 1)
            {
                return AdminResult.Refused("This is the last owner. Grant owner access to someone else first, or the console would lock everyone out.");
            }
        }

        op.RevokedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AdminOperatorRevoke, AuditActorType.Admin, operatorUserId, TargetType: "user", TargetId: userId, IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        return AdminResult.Ok("Console access is revoked.");
    }

    /// <summary>Returns a refusal when the operator lacks <paramref name="minimum"/>, else <see langword="null"/>.</summary>
    private async Task<AdminResult?> RequireAsync(Guid operatorUserId, PlatformRole minimum, CancellationToken cancellationToken)
    {
        PlatformRole? role = await GetRoleAsync(operatorUserId, cancellationToken).ConfigureAwait(false);
        return role is null
            ? AdminResult.Refused("You do not have console access.")
            : role < minimum ? AdminResult.Refused($"That needs {PlatformRanks.Label(minimum)} access.") : null;
    }

    /// <inheritdoc />
    public async Task<AdminResult> ResetTwoStepAsync(Guid operatorUserId, Guid userId, IdentityProofingMethod method, string reference, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        AdminResult? refusal = await RequireAsync(operatorUserId, PlatformRole.Support, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        if (operatorUserId == userId)
        {
            return AdminResult.Refused("You cannot reset your own two-step sign-in. Ask another operator.");
        }

        if (LongNumberRegex().IsMatch(reference))
        {
            return AdminResult.Refused("The reference looks like it contains an identity-document number. Record the ticket number or a short note only — never Aadhaar, PAN or passport numbers.");
        }

        bool targetIsOperator = await _db.PlatformOperators.AnyAsync(o => o.UserId == userId && o.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (targetIsOperator && await GetRoleAsync(operatorUserId, cancellationToken).ConfigureAwait(false) != PlatformRole.Owner)
        {
            return AdminResult.Refused("Only an Owner can reset an operator's two-step sign-in.");
        }

        SangamUser? user = await _users.FindByIdAsync(userId.ToString("D")).ConfigureAwait(false);
        if (user is null || user.Status == UserStatus.DeletedHard)
        {
            return AdminResult.Refused("That user does not exist.");
        }

        if (!await _users.GetTwoFactorEnabledAsync(user).ConfigureAwait(false))
        {
            return AdminResult.Refused("This person has no authenticator to reset.");
        }

        await _users.SetTwoFactorEnabledAsync(user, false).ConfigureAwait(false);
        await _users.ResetAuthenticatorKeyAsync(user).ConfigureAwait(false);
        await _users.UpdateSecurityStampAsync(user).ConfigureAwait(false);
        await _portal.RevokeAllSessionsAsync(userId, ipAddress, cancellationToken).ConfigureAwait(false);
        await _email.SendAsync(AccountEmails.TwoStepResetNotice(user.Email!, user.DisplayName), cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AdminUserMfaReset, AuditActorType.Admin, operatorUserId, TargetType: "user", TargetId: userId,
                Metadata: System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["method"] = method switch
                    {
                        IdentityProofingMethod.VideoCall => "video_call",
                        IdentityProofingMethod.InPerson => "in_person",
                        _ => "verified_mobile_callback",
                    },
                    ["reference"] = reference.Trim(),
                }),
                IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        return AdminResult.Ok("Two-step sign-in reset. The authenticator is removed, every session has ended, and the person has been told by e-mail.");
    }

    // Eight or more digits in a row look like an identity-document number, which must never be recorded.
    [GeneratedRegex("[0-9]{8,}")]
    private static partial Regex LongNumberRegex();

    private static string Reason(string reason)
        => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["reason"] = reason.Trim() });

    private static string OwnerGrantMetadata(string appName)
        => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["role"] = "owner", ["app"] = appName });
}
