using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Partners;

/// <summary><see cref="IAccessReviewService"/> over the identity database (rc.11, R6).</summary>
public sealed class EfAccessReviewService : IAccessReviewService
{
    /// <summary>The longest reason kept with an ended role.</summary>
    private const int NoteLength = 300;

    /// <summary>How many decisions the Decided list shows.</summary>
    private const int DecidedShown = 100;

    private const string EndingList = "ending";
    private const string UnusedList = "unused";

    private readonly SangamDbContext _db;
    private readonly IManagementService _management;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="management">The management service, which grants and ends roles (and audits them).</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    public EfAccessReviewService(SangamDbContext db, IManagementService management, IAuditWriter audit, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _management = management ?? throw new ArgumentNullException(nameof(management));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<AccessReview?> GetAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
    {
        if (!await AdministersAsync(userId, appId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        List<Candidate> candidates = await CandidatesAsync(appId, cancellationToken).ConfigureAwait(false);
        List<AccessReviewItem> ending = [.. candidates.Where(c => c.List == EndingList).OrderBy(c => c.ExpiresAt).Select(c => c.Item)];
        List<AccessReviewItem> unused = [.. candidates.Where(c => c.List == UnusedList).OrderBy(c => c.Item.LastSignInAt ?? c.Item.GrantedAt).Select(c => c.Item)];

        var decided = await _db.AccessReviewDecisions.AsNoTracking()
            .Where(d => d.AppId == appId)
            .OrderByDescending(d => d.DecidedAt)
            .Take(DecidedShown)
            .Select(d => new
            {
                d.DecidedAt,
                Person = _db.Users.Where(u => u.Id == d.MemberUserId).Select(u => (u.FirstName + " " + u.LastName).Trim()).FirstOrDefault(),
                Organisation = _db.Organisations.Where(o => o.Id == d.OrgId).Select(o => o.Name).FirstOrDefault(),
                Role = _db.Roles.Where(r => r.Id == d.RoleId).Select(r => r.DisplayName == string.Empty ? r.Code : r.DisplayName).FirstOrDefault(),
                d.Outcome,
                d.NewExpiresAt,
                d.Note,
                DecidedBy = _db.Users.Where(u => u.Id == d.DecidedByUserId).Select(u => (u.FirstName + " " + u.LastName).Trim()).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new AccessReview(
            ending,
            unused,
            [.. decided.Select(d => new AccessReviewDecided(d.DecidedAt, d.Person ?? string.Empty, d.Organisation ?? string.Empty, d.Role ?? string.Empty, d.Outcome, d.NewExpiresAt, d.Note, d.DecidedBy ?? string.Empty))]);
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
        => await AdministersAsync(userId, appId, cancellationToken).ConfigureAwait(false)
            ? (await CandidatesAsync(appId, cancellationToken).ConfigureAwait(false)).Count
            : 0;

    /// <inheritdoc />
    public async Task<PartnerResult> DecideAsync(Guid userId, Guid appId, AccessReviewInput decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (!await AdministersAsync(userId, appId, cancellationToken).ConfigureAwait(false))
        {
            return PartnerResult.Refused("You do not administer that application.");
        }

        Candidate? candidate = (await CandidatesAsync(appId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(c => c.Item.MembershipId == decision.MembershipId);
        if (candidate is null)
        {
            return PartnerResult.Refused("That role is no longer waiting for a review.");
        }

        string? note = string.IsNullOrWhiteSpace(decision.Note) ? null : decision.Note.Trim();
        if (note is { Length: > NoteLength })
        {
            note = note[..NoteLength];
        }

        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset? newExpiry = null;
        ManagementActor actor = ManagementActor.AppAdmin(userId) with { Reason = note };
        string message;
        switch (decision.Outcome)
        {
            case AccessReviewOutcome.Keep:
                message = "Kept. It will not be listed again for this review.";
                break;

            case AccessReviewOutcome.Extend:
                if (decision.ExtendMonths is not (3 or 6 or 12))
                {
                    return PartnerResult.Refused("Choose 3, 6 or 12 months.");
                }

                DateTimeOffset from = candidate.ExpiresAt is DateTimeOffset ends && ends > now ? ends : now;
                newExpiry = from.AddMonths(decision.ExtendMonths.Value).ToUniversalTime();
                ManagementResult<MembershipDto> extended = await _management.UpsertMembershipAsync(
                    appId, candidate.OrgId, candidate.MemberUserId, new MembershipUpsert(candidate.RoleCode, candidate.AppliesToDescendants, newExpiry), actor, cancellationToken).ConfigureAwait(false);
                if (extended.Status != ManagementStatus.Ok)
                {
                    return PartnerResult.Refused(extended.Message ?? "That could not be saved.");
                }

                message = "Extended.";
                break;

            case AccessReviewOutcome.End:
                if (note is null)
                {
                    return PartnerResult.Refused("Give a reason. The person sees it in their activity.");
                }

                ManagementResult<MembershipDto> ended = await _management.RevokeMembershipAsync(appId, candidate.OrgId, candidate.MemberUserId, actor, cancellationToken).ConfigureAwait(false);
                if (ended.Status != ManagementStatus.Ok)
                {
                    return PartnerResult.Refused(ended.Message ?? "That could not be saved.");
                }

                message = "Role ended.";
                break;

            default:
                return PartnerResult.Refused("Choose Keep, Extend or End.");
        }

        _db.AccessReviewDecisions.Add(new AccessReviewDecision
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            MembershipId = decision.MembershipId,
            MemberUserId = candidate.MemberUserId,
            OrgId = candidate.OrgId,
            RoleId = candidate.RoleId,
            Reason = candidate.List,
            Outcome = decision.Outcome,
            NewExpiresAt = newExpiry,
            Note = decision.Outcome == AccessReviewOutcome.End ? note : null,
            DecidedByUserId = userId,
            DecidedAt = now,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AppAccessReview, AuditActorType.Admin, userId, appId, "user", candidate.MemberUserId,
                JsonSerializer.Serialize(new
                {
                    outcome = decision.Outcome.ToString().ToLowerInvariant(),
                    list = candidate.List,
                    org_id = candidate.OrgId,
                    role = candidate.RoleCode,
                    new_expires_at = newExpiry,
                })),
            cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok(message);
    }

    private async Task<bool> AdministersAsync(Guid userId, Guid appId, CancellationToken cancellationToken)
        => await _db.AppAdmins.AsNoTracking()
            .AnyAsync(a => a.AppId == appId && a.UserId == userId && a.RevokedAt == null && a.App!.Status == AppStatus.Active && !a.App.IsPlatform, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Every live role of the application that needs a decision: ending within 30 days, or (otherwise) held by someone who
    /// has not signed in to the application for 90 days. A role decided about within that window is left off.
    /// </summary>
    private async Task<List<Candidate>> CandidatesAsync(Guid appId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset endingBy = now + AccessReviewDecision.EndingWindow;
        DateTimeOffset unusedSince = now - AccessReviewDecision.UnusedWindow;
        DateTimeOffset endingDecidedSince = now - AccessReviewDecision.EndingWindow;

        var rows = await _db.OrgMemberships.AsNoTracking()
            .Where(m => m.AppId == appId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now) && m.User!.DeletedAt == null)
            .Select(m => new
            {
                m.Id,
                m.UserId,
                m.OrgId,
                m.RoleId,
                RoleCode = m.Role!.Code,
                RoleName = m.Role.DisplayName == string.Empty ? m.Role.Code : m.Role.DisplayName,
                m.AppliesToDescendants,
                m.GrantedAt,
                m.ExpiresAt,
                Person = (m.User!.FirstName + " " + m.User.LastName).Trim(),
                Email = m.User.Email ?? string.Empty,
                Organisation = m.Org!.Name,
                LastSignIn = _db.AppGrants.Where(g => g.AppId == appId && g.UserId == m.UserId && g.RevokedAt == null).Select(g => g.LastUsedAt).FirstOrDefault(),
                LastDecided = _db.AccessReviewDecisions.Where(d => d.MembershipId == m.Id).Max(d => (DateTimeOffset?)d.DecidedAt),
            })
            .Where(m => (m.ExpiresAt != null && m.ExpiresAt <= endingBy) || (m.GrantedAt < unusedSince && (m.LastSignIn == null || m.LastSignIn < unusedSince)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        List<Candidate> candidates = [];
        foreach (var m in rows)
        {
            bool ending = m.ExpiresAt is DateTimeOffset ends && ends <= endingBy;
            DateTimeOffset decidedSince = ending ? endingDecidedSince : unusedSince;
            if (m.LastDecided is DateTimeOffset decided && decided >= decidedSince)
            {
                continue;
            }

            candidates.Add(new Candidate(
                ending ? EndingList : UnusedList,
                m.UserId,
                m.OrgId,
                m.RoleId,
                m.RoleCode,
                m.AppliesToDescendants,
                m.ExpiresAt,
                new AccessReviewItem(m.Id, m.Person, m.Email, m.Organisation, m.RoleName, m.GrantedAt, m.ExpiresAt, m.LastSignIn)));
        }

        return candidates;
    }

    private sealed record Candidate(string List, Guid MemberUserId, Guid OrgId, Guid RoleId, string RoleCode, bool AppliesToDescendants, DateTimeOffset? ExpiresAt, AccessReviewItem Item);
}
