using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Tenancy;

/// <summary>
/// rc.12 (M12): emergency access on PostgreSQL. Sangam keeps who, where, when, why and the application's own case reference,
/// never anything about the patient. Each use is audited in the person's name (their Activity and the compliance evidence
/// read the audit trail) and e-mailed at once to the reviewers of the organisation and of every organisation above it.
/// </summary>
public sealed class EfEmergencyAccessService : IEmergencyAccessService
{
    private readonly SangamDbContext _db;
    private readonly StaffRights _rights;
    private readonly IMessageTemplates _templates;
    private readonly IEmailSender _email;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly string _partner;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="rights">Who manages what.</param>
    /// <param name="templates">Message templates.</param>
    /// <param name="email">E-mail.</param>
    /// <param name="audit">Audit trail.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="configuration">Reads <c>Sangam:Partner:BaseUrl</c> for the review link.</param>
    public EfEmergencyAccessService(SangamDbContext db, StaffRights rights, IMessageTemplates templates, IEmailSender email, IAuditWriter audit, IClock clock, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _rights = rights ?? throw new ArgumentNullException(nameof(rights));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _partner = (configuration["Sangam:Partner:BaseUrl"] ?? "https://partners.sangamid.in").TrimEnd('/');
    }

    /// <summary>Whether a case reference is usable: 1 to 64 characters, letters, digits and <c>-_./:#</c> only.</summary>
    /// <param name="caseRef">The reference.</param>
    /// <returns><see langword="true"/> when usable.</returns>
    public static bool IsCaseRef(string? caseRef)
        => !string.IsNullOrEmpty(caseRef) && caseRef.Length <= 64 && caseRef.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '#');

    /// <inheritdoc />
    public async Task<bool> IsOnAsync(Guid appId, Guid orgId, CancellationToken cancellationToken = default)
    {
        Organisation? org = await _rights.OrganisationAsync(appId, orgId, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return false;
        }

        List<Guid> path = [.. OrganisationPath.Ids(org.Path)];
        return await _db.Organisations.AsNoTracking().AnyAsync(o => path.Contains(o.Id) && o.EmergencyAccess, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EmergencyStartResult> StartAsync(EmergencyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string reason = (request.Reason ?? string.Empty).Trim();
        if (!IsCaseRef(request.CaseRef))
        {
            return new EmergencyStartResult(EmergencyStartStatus.CaseRequired);
        }

        if (reason.Length < 3)
        {
            return new EmergencyStartResult(EmergencyStartStatus.ReasonRequired);
        }

        reason = reason.Length > 500 ? reason[..500] : reason;
        DateTimeOffset now = _clock.UtcNow;
        Organisation? org = await _rights.OrganisationAsync(request.AppId, request.OrgId, cancellationToken).ConfigureAwait(false);
        bool staff = org is not null && org.Status != OrganisationStatus.Closed && await _db.OrgMemberships.AsNoTracking().AnyAsync(
            m => m.AppId == request.AppId && m.OrgId == request.OrgId && m.UserId == request.UserId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now),
            cancellationToken).ConfigureAwait(false);
        if (!staff)
        {
            await RefusedAsync(request, "not_staff", cancellationToken).ConfigureAwait(false);
            return new EmergencyStartResult(EmergencyStartStatus.NotStaff);
        }

        if (!await IsOnAsync(request.AppId, request.OrgId, cancellationToken).ConfigureAwait(false))
        {
            await RefusedAsync(request, "switched_off", cancellationToken).ConfigureAwait(false);
            return new EmergencyStartResult(EmergencyStartStatus.SwitchedOff);
        }

        // At most three a day (India time), across every application.
        DateTimeOffset dayStart = new DateTimeOffset(StaffCodes.IndiaToday(now).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)).ToUniversalTime();
        int today = await _db.EmergencyAccesses.CountAsync(e => e.UserId == request.UserId && e.StartedAt >= dayStart, cancellationToken).ConfigureAwait(false);
        if (today >= IEmergencyAccessService.DailyLimit)
        {
            await RefusedAsync(request, "daily_limit", cancellationToken).ConfigureAwait(false);
            return new EmergencyStartResult(EmergencyStartStatus.DailyLimit, UsedToday: today);
        }

        EmergencyAccess use = new()
        {
            Id = Guid.NewGuid(),
            AppId = request.AppId,
            OrgId = request.OrgId,
            UserId = request.UserId,
            SessionId = request.SessionId,
            Reason = reason,
            CaseRef = request.CaseRef,
            Method = request.Method,
            StartedAt = now,
            EndsAt = now + IEmergencyAccessService.Longest,
        };
        _db.EmergencyAccesses.Add(use);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.EmergencyStart, AuditActorType.User, request.UserId, request.AppId, "emergency_access", use.Id,
                JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["organisation"] = request.OrgId,
                    ["case"] = use.CaseRef,
                    ["reason"] = reason,
                    ["method"] = use.Method,
                    ["ends_at"] = use.EndsAt.ToString("O", CultureInfo.InvariantCulture),
                })),
            cancellationToken).ConfigureAwait(false);
        await TellReviewersAsync(use, org!, cancellationToken).ConfigureAwait(false);
        return new EmergencyStartResult(EmergencyStartStatus.Started, Grant(use), today + 1);
    }

    /// <inheritdoc />
    public async Task<EmergencyGrant?> ActiveAsync(Guid userId, Guid appId, Guid? sessionId, string caseRef, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        EmergencyAccess? use = await _db.EmergencyAccesses.AsNoTracking()
            .Where(e => e.UserId == userId && e.AppId == appId && e.SessionId == sessionId && e.CaseRef == caseRef && e.EndedAt == null && e.EndsAt > now)
            .OrderByDescending(e => e.StartedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return use is null ? null : Grant(use);
    }

    /// <inheritdoc />
    public async Task<EmergencyGrant?> ActiveByIdAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        EmergencyAccess? use = await _db.EmergencyAccesses.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId && e.EndedAt == null && e.EndsAt > now, cancellationToken)
            .ConfigureAwait(false);
        return use is null ? null : Grant(use);
    }

    /// <inheritdoc />
    public async Task<bool> EndAsync(Guid userId, Guid appId, Guid? sessionId, string reason, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        string why = (reason ?? string.Empty).Trim();
        List<EmergencyAccess> open = await _db.EmergencyAccesses
            .Where(e => e.UserId == userId && e.AppId == appId && e.SessionId == sessionId && e.EndedAt == null && e.EndsAt > now)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (open.Count == 0 || why.Length == 0)
        {
            return false;
        }

        foreach (EmergencyAccess use in open)
        {
            use.EndedAt = now;
            use.EndReason = why.Length > 500 ? why[..500] : why;
            await _audit.WriteAsync(
                new AuditEntry(AuditActions.EmergencyEnd, AuditActorType.Api, userId, appId, "emergency_access", use.Id,
                    JsonSerializer.Serialize(new Dictionary<string, object> { ["reason"] = use.EndReason })),
                cancellationToken).ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmergencyUse>?> LogAsync(Guid viewerUserId, Guid appId, Guid orgId, int days = 90, CancellationToken cancellationToken = default)
    {
        Organisation? org = await _rights.OrganisationAsync(appId, orgId, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return null;
        }

        bool admin = await _rights.ManagesAsync(appId, org, viewerUserId, cancellationToken).ConfigureAwait(false);
        List<string> reviewed = await ReviewerPathsAsync(viewerUserId, appId, cancellationToken).ConfigureAwait(false);
        if (!admin && !reviewed.Any(p => org.Path.StartsWith(p, StringComparison.Ordinal)))
        {
            return null;
        }

        DateTimeOffset since = _clock.UtcNow.AddDays(-Math.Clamp(days, 1, 400));
        string path = org.Path;
        var rows = await _db.EmergencyAccesses.AsNoTracking()
            .Where(e => e.AppId == appId && e.StartedAt >= since)
            .Join(_db.Organisations.Where(o => o.Path.StartsWith(path)), e => e.OrgId, o => o.Id, (e, o) => new { Use = e, o.Name, o.Path })
            .Join(_db.Users, x => x.Use.UserId, u => u.Id, (x, u) => new { x.Use, x.Name, x.Path, u.FirstName, u.LastName })
            .OrderByDescending(x => x.Use.StartedAt)
            .Take(500)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<Guid> reviewerIds = [.. rows.Where(r => r.Use.ReviewedByUserId != null).Select(r => r.Use.ReviewedByUserId!.Value).Distinct()];
        Dictionary<Guid, string> reviewers = await _db.Users.AsNoTracking()
            .Where(u => reviewerIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => (u.FirstName + " " + u.LastName).Trim(), cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(r => new EmergencyUse(
            r.Use.Id,
            (r.FirstName + " " + r.LastName).Trim(),
            r.Use.UserId,
            r.Name,
            r.Use.CaseRef,
            r.Use.Reason,
            r.Use.Method,
            r.Use.StartedAt,
            r.Use.EndedAt ?? r.Use.EndsAt,
            r.Use.EndReason,
            r.Use.Verdict,
            r.Use.ReviewNote,
            r.Use.ReviewedByUserId is Guid by && reviewers.TryGetValue(by, out string? name) ? name : null,
            r.Use.ReviewedAt,
            r.Use.UserId != viewerUserId && reviewed.Any(p => r.Path.StartsWith(p, StringComparison.Ordinal))))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmergencyWaiting>> WaitingAsync(Guid appAdminUserId, Guid appId, CancellationToken cancellationToken = default)
    {
        if (!await _db.AppAdmins.AnyAsync(a => a.AppId == appId && a.UserId == appAdminUserId && a.RevokedAt == null, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        DateTimeOffset now = _clock.UtcNow;
        var waiting = await _db.EmergencyAccesses.AsNoTracking()
            .Where(e => e.AppId == appId && e.ReviewedAt == null)
            .Join(_db.Organisations, e => e.OrgId, o => o.Id, (e, o) => new { e.StartedAt, o.Path })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<Guid> customers = [.. waiting.Select(w => OrganisationPath.CustomerId(w.Path)).Distinct()];
        Dictionary<Guid, string> names = await _db.Organisations.AsNoTracking().Where(o => customers.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Name, cancellationToken).ConfigureAwait(false);
        return [.. waiting
            .GroupBy(w => OrganisationPath.CustomerId(w.Path))
            .Select(g => new EmergencyWaiting(
                g.Key,
                names.GetValueOrDefault(g.Key, string.Empty),
                g.Count(w => IEmergencyAccessService.AgeOf(w.StartedAt, false, now) == EmergencyReviewAge.Fresh),
                g.Count(w => IEmergencyAccessService.AgeOf(w.StartedAt, false, now) == EmergencyReviewAge.Due),
                g.Count(w => IEmergencyAccessService.AgeOf(w.StartedAt, false, now) == EmergencyReviewAge.Late)))
            .OrderByDescending(w => w.Late)
            .ThenByDescending(w => w.Due)
            .ThenBy(w => w.CustomerName, StringComparer.CurrentCulture)];
    }

    /// <inheritdoc />
    public async Task<bool> ReviewAsync(Guid reviewerUserId, Guid appId, Guid useId, EmergencyVerdict verdict, string? note, CancellationToken cancellationToken = default)
    {
        string text = (note ?? string.Empty).Trim();
        if (verdict == EmergencyVerdict.NotJustified && text.Length == 0)
        {
            return false;
        }

        EmergencyAccess? use = await _db.EmergencyAccesses.FirstOrDefaultAsync(e => e.Id == useId && e.AppId == appId, cancellationToken).ConfigureAwait(false);
        string? orgPath = use is null ? null : await _db.Organisations.AsNoTracking().Where(o => o.Id == use.OrgId).Select(o => o.Path).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (use is null || orgPath is null || use.UserId == reviewerUserId)
        {
            return false;
        }

        List<string> reviewed = await ReviewerPathsAsync(reviewerUserId, appId, cancellationToken).ConfigureAwait(false);
        if (!reviewed.Any(p => orgPath.StartsWith(p, StringComparison.Ordinal)))
        {
            return false;
        }

        use.Verdict = verdict;
        use.ReviewNote = text.Length == 0 ? null : text.Length > 1000 ? text[..1000] : text;
        use.ReviewedAt = _clock.UtcNow;
        use.ReviewedByUserId = reviewerUserId;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.EmergencyReview, AuditActorType.Admin, reviewerUserId, appId, "emergency_access", useId,
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["verdict"] = verdict == EmergencyVerdict.Justified ? "justified" : "not_justified", ["note"] = use.ReviewNote })),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static EmergencyGrant Grant(EmergencyAccess use) => new(use.Id, use.OrgId, use.CaseRef, use.StartedAt, use.EndsAt);

    /// <summary>The paths of the organisations where the person reviews emergency access in the application.</summary>
    private async Task<List<string>> ReviewerPathsAsync(Guid userId, Guid appId, CancellationToken cancellationToken)
        => await _db.OrgAdmins.AsNoTracking()
            .Where(a => a.AppId == appId && a.UserId == userId && a.RevokedAt == null && a.Right == OrgAdminRight.EmergencyReviewer)
            .Join(_db.Organisations, a => a.OrgId, o => o.Id, (a, o) => o.Path)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    private Task RefusedAsync(EmergencyRequest request, string why, CancellationToken cancellationToken)
        => _audit.WriteAsync(
            new AuditEntry(AuditActions.EmergencyRefused, AuditActorType.User, request.UserId, request.AppId, "organisation", request.OrgId,
                JsonSerializer.Serialize(new Dictionary<string, object> { ["why"] = why, ["case"] = IsCaseRef(request.CaseRef) ? request.CaseRef : string.Empty })),
            cancellationToken);

    private async Task TellReviewersAsync(EmergencyAccess use, Organisation org, CancellationToken cancellationToken)
    {
        List<Guid> path = [.. OrganisationPath.Ids(org.Path)];
        List<Guid> reviewerIds = await _db.OrgAdmins.AsNoTracking()
            .Where(a => a.AppId == use.AppId && a.RevokedAt == null && a.Right == OrgAdminRight.EmergencyReviewer && path.Contains(a.OrgId) && a.UserId != use.UserId)
            .Select(a => a.UserId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (reviewerIds.Count == 0)
        {
            return;
        }

        string application = await _db.Apps.AsNoTracking().Where(a => a.Id == use.AppId).Select(a => a.DisplayName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        string member = await _db.Users.AsNoTracking().Where(u => u.Id == use.UserId).Select(u => (u.FirstName + " " + u.LastName).Trim()).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        Guid customer = OrganisationPath.CustomerId(org.Path);
        string link = $"{_partner}/org/{customer:D}/emergency";
        foreach (SangamUser reviewer in await _db.Users.AsNoTracking().Where(u => reviewerIds.Contains(u.Id) && u.Email != null).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            EmailMessage message = await _templates.EmailAsync(
                MessageTemplateKinds.EmergencyAccessUsed,
                reviewer.Locale,
                use.AppId,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["name"] = reviewer.FirstName,
                    ["member"] = member,
                    ["organisation"] = org.Name,
                    ["application"] = application,
                    ["case"] = use.CaseRef,
                    ["reason"] = use.Reason,
                    ["start"] = Sangam.Shared.IndiaTime.Stamp(use.StartedAt),
                    ["end"] = Sangam.Shared.IndiaTime.Stamp(use.EndsAt),
                    ["link"] = link,
                },
                reviewer.Email!,
                reviewer.FirstName,
                cancellationToken).ConfigureAwait(false);
            await _email.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }
}
