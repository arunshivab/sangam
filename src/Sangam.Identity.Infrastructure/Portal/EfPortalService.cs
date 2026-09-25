using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Shared.Constants;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Sangam.Identity.Infrastructure.Portal;

/// <summary><see cref="IPortalService"/> over the identity database and OpenIddict's stores.</summary>
public sealed class EfPortalService : IPortalService
{
    /// <summary>How long a deletion request waits before the account is purged.</summary>
    public static readonly TimeSpan DeletionGrace = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SangamDbContext _db;
    private readonly UserManager<SangamUser> _users;
    private readonly IOpenIddictApplicationManager _applications;
    private readonly IOpenIddictAuthorizationManager _authorizations;
    private readonly IOpenIddictTokenManager _tokens;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    /// <summary>Initialises the service.</summary>
    public EfPortalService(
        SangamDbContext db,
        UserManager<SangamUser> users,
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        IOpenIddictTokenManager tokens,
        IAuditWriter audit,
        IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _applications = applications ?? throw new ArgumentNullException(nameof(applications));
        _authorizations = authorizations ?? throw new ArgumentNullException(nameof(authorizations));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<PortalOverview> GetOverviewAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        int apps = await _db.AppGrants.CountAsync(g => g.UserId == userId && g.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        int orgs = await _db.OrgMemberships.Where(m => m.UserId == userId && m.RevokedAt == null).Select(m => m.OrgId).Distinct().CountAsync(cancellationToken).ConfigureAwait(false);
        int sessions = await _db.UserSessions.CountAsync(s => s.UserId == userId && s.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        DateTimeOffset? lastSignIn = await _db.AuditEvents
            .Where(e => e.ActorUserId == userId && e.Action == AuditActions.UserLoginSuccess)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => (DateTimeOffset?)e.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        SignInMode preference = await _db.Users.Where(u => u.Id == userId).Select(u => u.SignInPreference).FirstAsync(cancellationToken).ConfigureAwait(false);

        return new PortalOverview(apps, orgs, sessions, lastSignIn, preference == SignInMode.Password);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LinkedApp>> GetLinkedAppsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        string subject = userId.ToString("D");

        var grants = await _db.AppGrants
            .AsNoTracking()
            .Where(g => g.UserId == userId && g.RevokedAt == null)
            .Select(g => new { g.AppId, g.GrantedAt, App = g.App! })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var consents = await _db.Consents
            .AsNoTracking()
            .Where(c => c.UserId == userId && c.RevokedAt == null)
            .Select(c => new { c.AppId, c.Scope })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var memberships = await _db.OrgMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.RevokedAt == null)
            .Select(m => new { m.AppId, OrgName = m.Org!.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var lastIssued = await _db.AuditEvents
            .AsNoTracking()
            .Where(e => e.ActorUserId == userId && e.Action == AuditActions.TokenIssue && e.ActorAppId != null)
            .GroupBy(e => e.ActorAppId!.Value)
            .Select(g => new { AppId = g.Key, At = g.Max(e => e.OccurredAt) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        List<LinkedApp> result = [];
        foreach (var grant in grants)
        {
            int live = 0;
            string? applicationId = await ApplicationIdAsync(grant.App.ClientId, cancellationToken).ConfigureAwait(false);
            if (applicationId is not null)
            {
                await foreach (object token in _tokens.FindAsync(subject, applicationId, Statuses.Valid, type: null, cancellationToken).ConfigureAwait(false))
                {
                    _ = token;
                    live++;
                }
            }

            result.Add(new LinkedApp(
                grant.AppId,
                grant.App.ClientId,
                grant.App.DisplayName,
                grant.App.Description,
                grant.App.OwnerCompanyName,
                grant.App.BrandColour,
                grant.App.Glyph,
                grant.App.PrivacyUrl,
                grant.GrantedAt,
                lastIssued.FirstOrDefault(l => l.AppId == grant.AppId)?.At,
                [.. consents.Where(c => c.AppId == grant.AppId).SelectMany(c => c.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                [.. memberships.Where(m => m.AppId == grant.AppId).Select(m => m.OrgName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                live));
        }

        return [.. result.OrderByDescending(a => a.LastUsedAt ?? a.LinkedAt)];
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAppAsync(Guid userId, Guid appId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        App? app = await _db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, cancellationToken).ConfigureAwait(false);
        bool granted = await _db.AppGrants.AnyAsync(g => g.UserId == userId && g.AppId == appId && g.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (app is null || !granted)
        {
            return false;
        }

        DateTimeOffset now = _clock.UtcNow;
        await _db.AppGrants.Where(g => g.UserId == userId && g.AppId == appId && g.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(g => g.RevokedAt, now), cancellationToken).ConfigureAwait(false);
        await _db.Consents.Where(c => c.UserId == userId && c.AppId == appId && c.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.RevokedAt, now), cancellationToken).ConfigureAwait(false);

        await RevokeOpenIddictAsync(userId, app.ClientId, cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(new AuditEntry(AuditActions.AppAccessRevoke, AuditActorType.User, userId, appId, "app", appId, IpAddress: ipAddress), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortalSession>> GetSessionsAsync(Guid userId, Guid? currentSessionId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.UserSessions
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .OrderByDescending(s => s.LastSeenAt)
            .Select(s => new { s.Id, s.DeviceLabel, s.IpAddress, s.UserAgent, s.SignInMode, s.CreatedAt, s.LastSeenAt, AppName = s.App != null ? s.App.DisplayName : null })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. rows
                .Select(r => new PortalSession(r.Id, r.DeviceLabel, UserAgentSummary.Describe(r.UserAgent), DescribeAddress(r.IpAddress), r.AppName, r.SignInMode, r.CreatedAt, r.LastSeenAt, r.Id == currentSessionId))
                .OrderByDescending(s => s.IsCurrent)
                .ThenByDescending(s => s.LastSeenAt),
        ];
    }

    /// <inheritdoc />
    public async Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        int changed = await _db.UserSessions
            .Where(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokedReason, "user"), cancellationToken)
            .ConfigureAwait(false);
        if (changed == 0)
        {
            return false;
        }

        await _audit.WriteAsync(new AuditEntry(AuditActions.UserSessionRevoke, AuditActorType.User, userId, TargetType: "session", TargetId: sessionId, IpAddress: ipAddress), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task RevokeAllSessionsAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        await _db.UserSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokedReason, "user_all"), cancellationToken)
            .ConfigureAwait(false);

        // Rotating the stamp ends every cookie within the validation interval, even on other nodes.
        SangamUser? user = await _users.FindByIdAsync(userId.ToString("D")).ConfigureAwait(false);
        if (user is not null)
        {
            await _users.UpdateSecurityStampAsync(user).ConfigureAwait(false);
        }

        await _audit.WriteAsync(new AuditEntry(AuditActions.UserSessionRevokeAll, AuditActorType.User, userId, TargetType: "user", TargetId: userId, IpAddress: ipAddress), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AuditPage> GetAuditAsync(Guid userId, DateTimeOffset? since, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        page = Math.Max(0, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        IQueryable<AuditEvent> query = _db.AuditEvents.AsNoTracking()
            .Where(e => e.ActorUserId == userId || (e.TargetType == "user" && e.TargetId == userId));
        if (since is DateTimeOffset from)
        {
            query = query.Where(e => e.OccurredAt >= from);
        }

        int total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        List<AuditEvent> events = await query
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<Guid, string> appNames = await AppNamesAsync(events.Select(e => e.ActorAppId), cancellationToken).ConfigureAwait(false);
        return new AuditPage([.. events.Select(e => AuditNarrator.Describe(e, appNames))], total, page, pageSize);
    }

    /// <inheritdoc />
    public async Task<string> ExportAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        SangamUser user = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);

        var memberships = await _db.OrgMemberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new { organisation = m.Org!.Name, organisationType = m.Org.OrgTypeCode, application = m.App!.DisplayName, role = m.Role!.Code, appliesToDescendants = m.AppliesToDescendants, grantedAt = m.GrantedAt, revokedAt = m.RevokedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var consents = await _db.Consents.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => new { application = c.App!.DisplayName, scope = c.Scope, consentVersion = c.ConsentVersion, grantedAt = c.GrantedAt, revokedAt = c.RevokedAt, ipAddress = c.IpAddress })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var grants = await _db.AppGrants.AsNoTracking()
            .Where(g => g.UserId == userId)
            .Select(g => new { application = g.App!.DisplayName, grantedAt = g.GrantedAt, revokedAt = g.RevokedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var sessions = await _db.UserSessions.AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new { s.DeviceLabel, s.IpAddress, s.UserAgent, signInMode = s.SignInMode, createdAt = s.CreatedAt, lastSeenAt = s.LastSeenAt, revokedAt = s.RevokedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        List<AuditEvent> events = await _db.AuditEvents.AsNoTracking()
            .Where(e => e.ActorUserId == userId || (e.TargetType == "user" && e.TargetId == userId))
            .OrderByDescending(e => e.OccurredAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<Guid, string> appNames = await AppNamesAsync(events.Select(e => e.ActorAppId), cancellationToken).ConfigureAwait(false);

        var export = new
        {
            exportedAt = _clock.UtcNow,
            note = "Everything Sangam holds about you. Your password is not included: it is stored only as a one-way hash and cannot be read, by us or by anyone else.",
            profile = new
            {
                id = user.Id,
                firstName = user.FirstName,
                lastName = user.LastName,
                email = user.Email,
                emailVerified = user.EmailConfirmed,
                mobile = user.PhoneNumber,
                mobileVerified = user.PhoneNumberConfirmed,
                dateOfBirth = user.DateOfBirth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                gender = Genders.ToCode(user.Gender),
                locale = user.Locale,
                timeZone = user.TimeZone,
                signInPreference = SignInModes.ToCode(user.SignInPreference),
                status = user.Status.ToString().ToLowerInvariant(),
                createdAt = user.CreatedAt,
                lastPasswordChangeAt = user.LastPasswordChangeAt,
                deletionRequestedFor = user.PurgeAfter,
            },
            applications = grants,
            consents,
            organisationMemberships = memberships,
            sessions,
            activity = events.Select(e => new
            {
                at = e.OccurredAt,
                action = e.Action,
                description = AuditNarrator.Describe(e, appNames).Summary,
                actor = AuditNarrator.Describe(e, appNames).Actor,
                ipAddress = e.IpAddress,
                userAgent = e.UserAgent,
            }),
        };

        await _audit.WriteAsync(new AuditEntry(AuditActions.UserDataExport, AuditActorType.User, userId, TargetType: "user", TargetId: userId, IpAddress: ipAddress), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(export, ExportOptions);
    }

    /// <inheritdoc />
    public async Task<DeletionState> GetDeletionStateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var row = await _db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.PurgeAfter, u.HoldPlacedAt }).FirstAsync(cancellationToken).ConfigureAwait(false);
        return new DeletionState(row.PurgeAfter is not null, row.PurgeAfter, row.HoldPlacedAt is not null);
    }

    /// <inheritdoc />
    public async Task RequestDeletionAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset purgeAfter = now + DeletionGrace;

        SangamUser user = await _db.Users.FirstAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        user.Status = UserStatus.DeletedSoft;
        user.DeletedAt = now;
        user.PurgeAfter = purgeAfter;
        user.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Revoke every application and end every session, so nothing keeps acting for the user.
        List<Guid> appIds = await _db.AppGrants.Where(g => g.UserId == userId && g.RevokedAt == null).Select(g => g.AppId).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (Guid appId in appIds)
        {
            await RevokeAppAsync(userId, appId, ipAddress, cancellationToken).ConfigureAwait(false);
        }

        await RevokeAllSessionsAsync(userId, ipAddress, cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.UserAccountDeletionRequest, AuditActorType.User, userId, TargetType: "user", TargetId: userId,
                Metadata: $"{{\"purge_after\":\"{purgeAfter:O}\"}}", IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CancelDeletionAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        SangamUser user = await _db.Users.FirstAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        if (user.PurgeAfter is null)
        {
            return;
        }

        user.Status = UserStatus.Active;
        user.DeletedAt = null;
        user.PurgeAfter = null;
        user.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(new AuditEntry(AuditActions.UserAccountDeletionCancel, AuditActorType.User, userId, TargetType: "user", TargetId: userId, IpAddress: ipAddress), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ProfileUpdateResult> UpdateProfileAsync(Guid userId, ProfileUpdate update, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (string.IsNullOrWhiteSpace(update.FirstName))
        {
            return ProfileUpdateResult.Failed(nameof(update.FirstName), "Enter your first name.");
        }

        if (string.IsNullOrWhiteSpace(update.LastName))
        {
            return ProfileUpdateResult.Failed(nameof(update.LastName), "Enter your last name.");
        }

        CountryCode country = CountryCodes.FindByIso(update.CountryIso) ?? CountryCodes.Default;
        string national = new([.. (update.MobileNumber ?? string.Empty).Where(char.IsDigit)]);
        if (country.NationalDigits > 0 && national.Length != country.NationalDigits)
        {
            return ProfileUpdateResult.Failed(nameof(update.MobileNumber), $"Enter your {country.NationalDigits}-digit {country.Name} mobile number.");
        }

        if (national.Length == 0)
        {
            return ProfileUpdateResult.Failed(nameof(update.MobileNumber), "Enter your mobile number.");
        }

        string mobile = country.DialCode + national;
        SangamUser user = await _db.Users.FirstAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        bool mobileChanged = !string.Equals(user.PhoneNumber, mobile, StringComparison.Ordinal);

        if (mobileChanged)
        {
            bool taken = await _db.Users
                .AnyAsync(u => u.Id != userId && u.PhoneNumber == mobile && u.Status != UserStatus.DeletedHard, cancellationToken)
                .ConfigureAwait(false);
            if (taken)
            {
                return ProfileUpdateResult.Failed(nameof(update.MobileNumber), "That mobile number is already on another Sangam account.");
            }
        }

        user.FirstName = update.FirstName.Trim();
        user.LastName = update.LastName.Trim();
        user.Locale = string.IsNullOrWhiteSpace(update.Locale) ? user.Locale : update.Locale.Trim();
        user.Gender = update.Gender;
        if (mobileChanged)
        {
            // A new number is unverified until mobile OTP ships; nothing else may assume otherwise.
            user.PhoneNumber = mobile;
            user.PhoneNumberConfirmed = false;
        }

        // updated_at is what lets an application notice that the copy it cached has moved.
        user.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.UserProfileUpdate, AuditActorType.User, userId, TargetType: "user", TargetId: userId,
                Metadata: $"{{\"mobile_changed\":{(mobileChanged ? "true" : "false")}}}", IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);

        return ProfileUpdateResult.Ok(mobileChanged);
    }

    private async Task<Dictionary<Guid, string>> AppNamesAsync(IEnumerable<Guid?> appIds, CancellationToken cancellationToken)
    {
        Guid[] ids = [.. appIds.Where(id => id is not null).Select(id => id!.Value).Distinct()];
        return ids.Length == 0
            ? []
            : await _db.Apps.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.DisplayName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The address as the user should read it. A loopback address means the browser is on the
    /// same machine as the server, which only happens in development — saying so is clearer than
    /// showing "::1", and it is still the truth rather than a guessed location.
    /// </summary>
    private static string? DescribeAddress(string? ipAddress)
        => ipAddress is "::1" or "127.0.0.1" ? ipAddress + " · this computer" : ipAddress;

    private async Task<string?> ApplicationIdAsync(string clientId, CancellationToken cancellationToken)
    {
        object? application = await _applications.FindByClientIdAsync(clientId, cancellationToken).ConfigureAwait(false);
        return application is null ? null : await _applications.GetIdAsync(application, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Revokes this user's authorizations and tokens for one application, leaving other apps alone.</summary>
    private async Task RevokeOpenIddictAsync(Guid userId, string clientId, CancellationToken cancellationToken)
    {
        string subject = userId.ToString("D");
        string? applicationId = await ApplicationIdAsync(clientId, cancellationToken).ConfigureAwait(false);
        if (applicationId is null)
        {
            return;
        }

        List<object> authorizations = [];
        await foreach (object authorization in _authorizations.FindAsync(subject, applicationId, status: null, type: null, scopes: null, cancellationToken).ConfigureAwait(false))
        {
            authorizations.Add(authorization);
        }

        foreach (object authorization in authorizations)
        {
            string? id = await _authorizations.GetIdAsync(authorization, cancellationToken).ConfigureAwait(false);
            if (id is not null)
            {
                await _tokens.RevokeByAuthorizationIdAsync(id, cancellationToken).ConfigureAwait(false);
            }

            await _authorizations.TryRevokeAsync(authorization, cancellationToken).ConfigureAwait(false);
        }

        List<object> tokens = [];
        await foreach (object token in _tokens.FindAsync(subject, applicationId, status: null, type: null, cancellationToken).ConfigureAwait(false))
        {
            tokens.Add(token);
        }

        foreach (object token in tokens)
        {
            await _tokens.TryRevokeAsync(token, cancellationToken).ConfigureAwait(false);
        }
    }
}
