using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Maintenance;

/// <summary>
/// rc.6 (SGM-910 section 7): ends connections and deletes accounts that are no longer used. Part of the hourly sweep.
/// <list type="bullet">
/// <item>An application with an inactivity limit: a person who has not used it for that long is told 30 days ahead;
/// then their connection ends — access revoked, their roles, attributes and consent for that application removed,
/// their tokens revoked, and the application told by SCIM or webhook so it can erase its own copy.</item>
/// <item>An account with no connection to a partner application and no sign-in for three years is told 30 days
/// ahead, then deleted by the usual purge. Operators and application administrators are never deleted this way.</item>
/// </list>
/// A notice is cleared by any use, so a person who signs in again keeps everything.
/// </summary>
public sealed partial class InactivityService
{
    /// <summary>How long before the end a person is told.</summary>
    public static readonly TimeSpan NoticePeriod = TimeSpan.FromDays(30);

    /// <summary>Years without sign-in after which an unconnected account is deleted.</summary>
    public const int AccountYears = 3;

    /// <summary>The most people handled per kind in one sweep, so a large backlog is spread over several hours.</summary>
    public const int BatchSize = 200;

    private readonly SangamDbContext _db;
    private readonly IEmailSender _email;
    private readonly IMessageTemplates _templates;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly IOpenIddictApplicationManager _applications;
    private readonly IOpenIddictAuthorizationManager _authorizations;
    private readonly IOpenIddictTokenManager _tokens;
    private readonly ILogger<InactivityService> _logger;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="email">E-mail sender.</param>
    /// <param name="templates">Message templates.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="applications">OpenIddict applications.</param>
    /// <param name="authorizations">OpenIddict authorizations.</param>
    /// <param name="tokens">OpenIddict tokens.</param>
    /// <param name="logger">Logger.</param>
    public InactivityService(SangamDbContext db, IEmailSender email, IMessageTemplates templates, IAuditWriter audit, IClock clock, IOpenIddictApplicationManager applications, IOpenIddictAuthorizationManager authorizations, IOpenIddictTokenManager tokens, ILogger<InactivityService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _applications = applications ?? throw new ArgumentNullException(nameof(applications));
        _authorizations = authorizations ?? throw new ArgumentNullException(nameof(authorizations));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Runs one sweep.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Notices sent, connections ended and accounts scheduled for deletion.</returns>
    public async Task<(int Notices, int Ended, int Accounts)> RunAsync(CancellationToken cancellationToken = default)
    {
        int notices = 0;
        int ended = 0;
        var apps = await _db.Apps.AsNoTracking()
            .Where(a => a.InactivityLimitYears != null && a.Status == AppStatus.Active)
            .Select(a => new { a.Id, a.ClientId, a.DisplayName, Years = a.InactivityLimitYears!.Value })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var app in apps)
        {
            (int n, int e) = await SweepAppAsync(app.Id, app.ClientId, app.DisplayName, app.Years, cancellationToken).ConfigureAwait(false);
            notices += n;
            ended += e;
        }

        (int accountNotices, int accounts) = await SweepAccountsAsync(cancellationToken).ConfigureAwait(false);
        return (notices + accountNotices, ended, accounts);
    }

    private async Task<(int Notices, int Ended)> SweepAppAsync(Guid appId, string clientId, string appName, int years, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset expireBefore = now.AddYears(-years);
        DateTimeOffset noticeBefore = expireBefore + NoticePeriod;
        DateTimeOffset noticeSentBefore = now - NoticePeriod;

        var toTell = await _db.AppGrants.AsNoTracking()
            .Where(g => g.AppId == appId && g.RevokedAt == null && g.InactivityNoticeAt == null && (g.LastUsedAt ?? g.GrantedAt) < noticeBefore)
            .OrderBy(g => g.LastUsedAt ?? g.GrantedAt)
            .Take(BatchSize)
            .Select(g => new { g.Id, g.UserId, LastUsed = g.LastUsedAt ?? g.GrantedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var grant in toTell)
        {
            DateTimeOffset ends = Later(grant.LastUsed.AddYears(years), now + NoticePeriod);
            int marked = await _db.AppGrants.Where(g => g.Id == grant.Id && g.InactivityNoticeAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(g => g.InactivityNoticeAt, now), cancellationToken).ConfigureAwait(false);
            if (marked == 0)
            {
                continue;
            }

            await SendAsync(grant.UserId, MessageTemplateKinds.AppInactivityNotice, new Dictionary<string, string>(StringComparer.Ordinal) { ["application"] = appName, ["date"] = Day(ends) }, cancellationToken).ConfigureAwait(false);
            await _audit.WriteAsync(
                new AuditEntry(AuditActions.ConsentInactivityNotice, AuditActorType.System, ActorAppId: appId, TargetType: "user", TargetId: grant.UserId,
                    Metadata: JsonSerializer.Serialize(new Dictionary<string, object> { ["ends"] = ends.ToString("O", CultureInfo.InvariantCulture), ["limit_years"] = years })),
                cancellationToken).ConfigureAwait(false);
        }

        List<Guid> due = await _db.AppGrants.AsNoTracking()
            .Where(g => g.AppId == appId && g.RevokedAt == null && g.InactivityNoticeAt != null && g.InactivityNoticeAt <= noticeSentBefore && (g.LastUsedAt ?? g.GrantedAt) < expireBefore)
            .OrderBy(g => g.LastUsedAt ?? g.GrantedAt)
            .Take(BatchSize)
            .Select(g => g.UserId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        int ended = 0;
        foreach (Guid userId in due)
        {
            if (await EndConnectionAsync(userId, appId, clientId, appName, years, cancellationToken).ConfigureAwait(false))
            {
                ended++;
            }
        }

        return (toTell.Count, ended);
    }

    /// <summary>Ends one person's connection to one application.</summary>
    private async Task<bool> EndConnectionAsync(Guid userId, Guid appId, string clientId, string appName, int years, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        int revoked = await _db.AppGrants.Where(g => g.UserId == userId && g.AppId == appId && g.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(g => g.RevokedAt, now), cancellationToken).ConfigureAwait(false);
        if (revoked == 0)
        {
            return false;
        }

        await _db.Consents.Where(c => c.UserId == userId && c.AppId == appId && c.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.RevokedAt, now), cancellationToken).ConfigureAwait(false);
        int roles = await _db.OrgMemberships.Where(m => m.UserId == userId && m.AppId == appId && m.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.RevokedAt, now), cancellationToken).ConfigureAwait(false);
        int attributes = await _db.UserAttributeValues
            .Where(v => v.UserId == userId && _db.UserAttributeDefinitions.Any(d => d.Id == v.DefinitionId && d.AppId == appId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await RevokeTokensAsync(userId, clientId, cancellationToken).ConfigureAwait(false);
        await Provisioning.AppEventLog.RaiseAsync(_db, AppEventTypes.ConsentRevoked, appId, userId, null, new Dictionary<string, object?> { ["reason"] = "inactive" }, now, cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.ConsentInactivityExpire, AuditActorType.System, ActorAppId: appId, TargetType: "user", TargetId: userId,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object> { ["limit_years"] = years, ["roles_removed"] = roles, ["attributes_removed"] = attributes })),
            cancellationToken).ConfigureAwait(false);
        await SendAsync(userId, MessageTemplateKinds.AppConnectionEnded, new Dictionary<string, string>(StringComparer.Ordinal) { ["application"] = appName }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<(int Notices, int Accounts)> SweepAccountsAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset deleteBefore = now.AddYears(-AccountYears);
        DateTimeOffset noticeBefore = deleteBefore + NoticePeriod;
        DateTimeOffset noticeSentBefore = now - NoticePeriod;

        IQueryable<SangamUser> unused = _db.Users
            .Where(u => u.Status == UserStatus.Active && u.PurgeAfter == null && u.HoldPlacedAt == null)
            .Where(u => !_db.AppGrants.Any(g => g.UserId == u.Id && g.RevokedAt == null && !g.App!.IsPlatform))
            .Where(u => !_db.PlatformOperators.Any(o => o.UserId == u.Id && o.RevokedAt == null))
            .Where(u => !_db.AppAdmins.Any(a => a.UserId == u.Id && a.RevokedAt == null));

        var toTell = await unused.AsNoTracking()
            .Where(u => u.InactivityNoticeAt == null && (u.LastSignInAt ?? u.CreatedAt) < noticeBefore)
            .OrderBy(u => u.LastSignInAt ?? u.CreatedAt)
            .Take(BatchSize)
            .Select(u => new { u.Id, LastSeen = u.LastSignInAt ?? u.CreatedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var user in toTell)
        {
            DateTimeOffset deleted = Later(user.LastSeen.AddYears(AccountYears), now + NoticePeriod);
            int marked = await _db.Users.Where(u => u.Id == user.Id && u.InactivityNoticeAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.InactivityNoticeAt, now), cancellationToken).ConfigureAwait(false);
            if (marked == 0)
            {
                continue;
            }

            await SendAsync(user.Id, MessageTemplateKinds.AccountInactivityNotice, new Dictionary<string, string>(StringComparer.Ordinal) { ["date"] = Day(deleted) }, cancellationToken).ConfigureAwait(false);
            await _audit.WriteAsync(
                new AuditEntry(AuditActions.UserAccountInactivityNotice, AuditActorType.System, TargetType: "user", TargetId: user.Id,
                    Metadata: JsonSerializer.Serialize(new Dictionary<string, object> { ["deleted_on"] = deleted.ToString("O", CultureInfo.InvariantCulture) })),
                cancellationToken).ConfigureAwait(false);
        }

        List<SangamUser> due = await unused
            .Where(u => u.InactivityNoticeAt != null && u.InactivityNoticeAt <= noticeSentBefore && (u.LastSignInAt ?? u.CreatedAt) < deleteBefore)
            .OrderBy(u => u.LastSignInAt ?? u.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (SangamUser user in due)
        {
            // Deleted by the usual purge (the next step of the same sweep): pseudonymised, the audit trail kept.
            user.Status = UserStatus.DeletedSoft;
            user.DeletedAt = now;
            user.PurgeAfter = now;
            user.UpdatedAt = now;
        }

        if (due.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            foreach (SangamUser user in due)
            {
                await _audit.WriteAsync(new AuditEntry(AuditActions.UserAccountInactivityDeletion, AuditActorType.System, TargetType: "user", TargetId: user.Id), cancellationToken).ConfigureAwait(false);
            }
        }

        return (toTell.Count, due.Count);
    }

    private async Task RevokeTokensAsync(Guid userId, string clientId, CancellationToken cancellationToken)
    {
        object? application = await _applications.FindByClientIdAsync(clientId, cancellationToken).ConfigureAwait(false);
        string? applicationId = application is null ? null : await _applications.GetIdAsync(application, cancellationToken).ConfigureAwait(false);
        if (applicationId is null)
        {
            return;
        }

        List<object> found = [];
        await foreach (object authorization in _authorizations.FindAsync(userId.ToString("D"), applicationId, status: null, type: null, scopes: null, cancellationToken).ConfigureAwait(false))
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

    private async Task SendAsync(Guid userId, string kind, Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        var user = await _db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.Email, u.FirstName, u.LastName, u.Locale })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (user is null || string.IsNullOrWhiteSpace(user.Email))
        {
            return;
        }

        string name = $"{user.FirstName} {user.LastName}".Trim();
        values["name"] = user.FirstName;
        try
        {
            await _email.SendAsync(await _templates.EmailAsync(kind, user.Locale, null, null, values, user.Email, name, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNotSent(kind, userId, ex);
        }
    }

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static string Day(DateTimeOffset at) => at.ToOffset(Sangam.Shared.IndiaTime.Offset).ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    [LoggerMessage(EventId = 7311, Level = LogLevel.Warning, Message = "Inactivity notice {Kind} could not be sent to user {UserId}.")]
    private partial void LogNotSent(string kind, Guid userId, Exception exception);
}
