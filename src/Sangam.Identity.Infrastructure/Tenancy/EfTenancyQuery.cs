using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Tenancy;

/// <summary><see cref="ITenancyQuery"/> over memberships, organisations and roles.</summary>
public sealed class EfTenancyQuery : ITenancyQuery
{
    private readonly SangamDbContext _db;

    /// <summary>Initialises the query.</summary>
    /// <param name="db">Database.</param>
    public EfTenancyQuery(SangamDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrgClaim>> GetOrgClaimsAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.OrgMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.AppId == appId && m.RevokedAt == null)
            .Where(m => m.Org!.Status == OrganisationStatus.Active)
            .OrderBy(m => m.Org!.Path)
            .Select(m => new
            {
                m.OrgId,
                m.Org!.Name,
                m.Org.OrgTypeCode,
                m.Org.Path,
                RoleCode = m.Role!.Code,
                m.Role.Permissions,
                m.AppliesToDescendants,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => new OrgClaim(r.OrgId, r.Name, r.OrgTypeCode, r.Path, r.RoleCode, ParsePermissions(r.Permissions), r.AppliesToDescendants))];
    }

    internal static IReadOnlyList<string> ParsePermissions(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
