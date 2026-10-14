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
            .Where(m => m.UserId == userId && m.AppId == appId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > DateTimeOffset.UtcNow))
            .Where(m => m.Org!.Status == OrganisationStatus.Active)
            .OrderBy(m => m.Org!.Path)
            .Select(m => new
            {
                m.OrgId,
                m.Org!.Name,
                m.Org.OrgTypeCode,
                m.Org.Path,
                RoleCode = m.Role == null ? null : m.Role.Code,
                Permissions = m.Role == null ? null : m.Role.Permissions,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // rc.12: one element per organisation, carrying every role held there (several allowed, or none).
        return [.. rows
            .GroupBy(r => r.OrgId)
            .Select(g =>
            {
                var first = g.First();
                List<string> roles = [.. g.Where(r => r.RoleCode is not null).Select(r => r.RoleCode!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
                List<string> permissions = [.. g.Where(r => r.Permissions is not null).SelectMany(r => ParsePermissions(r.Permissions!)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
                return new OrgClaim(g.Key, first.Name, first.OrgTypeCode, first.Path, roles.Count > 0 ? roles[0] : null, permissions, false, roles);
            })];
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
