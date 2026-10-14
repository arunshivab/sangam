using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Tenancy;
using Sangam.Shared;

namespace Sangam.Identity.Infrastructure.Partners;

/// <summary>rc.11 (R3): ending a role on a date or now, with a reason; people counts for the organisation tree.</summary>
public sealed partial class EfPartnerService
{
    /// <summary>The longest reason kept with an ended role.</summary>
    private const int ReasonLength = 300;

    /// <inheritdoc />
    public async Task<PartnerResult> EndMembershipAsync(Guid userId, Guid appId, Guid orgId, Guid memberUserId, DateOnly? endsOn, string? reason, CancellationToken cancellationToken = default)
    {
        if (await CheckRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotYours;
        }

        string? why = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (why is { Length: > ReasonLength })
        {
            why = why[..ReasonLength];
        }

        ManagementActor actor = ManagementActor.AppAdmin(userId) with { Reason = why };
        if (endsOn is not DateOnly day)
        {
            ManagementResult<MembershipDto> removed = await _management.RevokeMembershipAsync(appId, orgId, memberUserId, actor, cancellationToken).ConfigureAwait(false);
            return From(removed, "Role ended.");
        }

        // rc.12: every role the person holds there ends that day (a person may hold several, or none in Sangam).
        List<string?> current = await _db.OrgMemberships.AsNoTracking()
            .Where(m => m.AppId == appId && m.OrgId == orgId && m.UserId == memberUserId && m.RevokedAt == null)
            .Select(m => m.Role == null ? null : m.Role.Code)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (current.Count == 0)
        {
            return PartnerResult.Refused("That person no longer holds a role here.");
        }

        // The role ends at the end of the chosen day, India time.
        DateTimeOffset until = new(day.ToDateTime(new TimeOnly(23, 59, 59)), IndiaTime.Offset);
        List<string> codes = [.. current.Where(c => c is not null).Select(c => c!)];
        ManagementResult<MembershipDto> shortened = await _management
            .UpsertMembershipAsync(appId, orgId, memberUserId, new MembershipUpsert(null, false, until, codes), actor, cancellationToken).ConfigureAwait(false);
        return From(shortened, "The role will end on that day.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> OrganisationsWithOwnRulesAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
    {
        if (await CheckRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return [];
        }

        return await _db.Organisations.AsNoTracking()
            .Where(o => o.RegisteredViaAppId == appId && o.DeletedAt == null
                && (o.SignInPolicy != null || o.MinPasswordLength != null || o.MfaRequirement != null || o.BreachedPasswordCheck == true || o.RequireCharacterTypes == true))
            .OrderBy(o => o.Name)
            .Select(o => o.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, int>> CountPeopleAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
    {
        if (await CheckRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return new Dictionary<Guid, int>();
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        return await _db.OrgMemberships.AsNoTracking()
            .Where(m => m.AppId == appId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now))
            .GroupBy(m => m.OrgId)
            .Select(g => new { g.Key, People = g.Select(m => m.UserId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.People, cancellationToken).ConfigureAwait(false);
    }
}
