using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Provisioning;

namespace Sangam.Identity.Infrastructure.Tenancy;

/// <summary>
/// PR-25: ends time-limited memberships (contractors, auditors). From the moment one expires it no longer counts — tokens,
/// consoles and policies ignore it — and within a minute this sweep revokes it for good, audits it as
/// <c>org.membership.expire</c>, and tells SCIM and webhooks (<c>membership.revoked</c>, and <c>user.deactivated</c> when it
/// was the person's last role in the application).
/// </summary>
public sealed class MembershipExpiry
{
    private readonly SangamDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    /// <summary>Initialises the sweep.</summary>
    /// <param name="db">Database.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    public MembershipExpiry(SangamDbContext db, IAuditWriter audit, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Revokes every membership whose time is up; returns how many.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        List<OrgMembership> due = await _db.OrgMemberships.Include(m => m.Role)
            .Where(m => m.RevokedAt == null && m.ExpiresAt != null && m.ExpiresAt <= now)
            .OrderBy(m => m.ExpiresAt)
            .Take(200)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (OrgMembership membership in due)
        {
            membership.RevokedAt = membership.ExpiresAt;
            await AppEventLog.AddAsync(_db, AppEventTypes.MembershipRevoked, membership.AppId, membership.UserId, membership.OrgId, new Dictionary<string, object?> { ["org_id"] = membership.OrgId, ["role"] = membership.Role!.Code, ["reason"] = "expired" }, now, cancellationToken).ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (OrgMembership membership in due)
        {
            if (!await _db.OrgMemberships.AnyAsync(m => m.AppId == membership.AppId && m.UserId == membership.UserId && m.RevokedAt == null, cancellationToken).ConfigureAwait(false))
            {
                await AppEventLog.RaiseAsync(_db, AppEventTypes.UserDeactivated, membership.AppId, membership.UserId, null, new Dictionary<string, object?> { ["reason"] = "last_role_expired" }, now, cancellationToken).ConfigureAwait(false);
            }

            await _audit.WriteAsync(new AuditEntry(AuditActions.OrgMembershipExpire, AuditActorType.System, ActorAppId: membership.AppId, TargetType: "user", TargetId: membership.UserId,
                Metadata: JsonSerializer.Serialize(new { org = membership.OrgId, role = membership.Role!.Code, expired_at = membership.ExpiresAt })), cancellationToken).ConfigureAwait(false);
        }

        return due.Count;
    }
}
