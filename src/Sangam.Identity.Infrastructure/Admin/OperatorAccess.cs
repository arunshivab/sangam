using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Admin;

/// <summary>
/// rc.16 (ASVS V1.4.4, V1.4.5): the one way an operator's rights are checked. Every service method that acts for an
/// operator asks here, by named permission (<see cref="OperatorRight"/>), and a test fails the build when a public
/// method taking an operator's id does not (OperatorAccessTests). A refusal is written to the audit log as
/// <c>access.denied</c>, naming the operation, the permission and the rank it needs.
/// </summary>
public sealed class OperatorAccess
{
    private readonly SangamDbContext _db;
    private readonly IAuditWriter _audit;

    /// <summary>Initialises the checker.</summary>
    /// <param name="db">Database.</param>
    /// <param name="audit">Audit writer, for refusals.</param>
    public OperatorAccess(SangamDbContext db, IAuditWriter audit)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>The operator's rank, or null for someone who is not an operator (or whose access was revoked).</summary>
    /// <param name="operatorUserId">The person.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task<PlatformRole?> RankAsync(Guid operatorUserId, CancellationToken cancellationToken = default)
        => RankAsync(_db, operatorUserId, cancellationToken);

    /// <summary>The rank, read with a context the caller owns (services that make their own contexts).</summary>
    /// <param name="db">Database.</param>
    /// <param name="operatorUserId">The person.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static Task<PlatformRole?> RankAsync(SangamDbContext db, Guid operatorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.PlatformOperators.AsNoTracking()
            .Where(o => o.UserId == operatorUserId && o.RevokedAt == null)
            .Select(o => (PlatformRole?)o.Role)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Whether the operator has the permission, without writing a refusal (for what a page merely shows).</summary>
    /// <param name="operatorUserId">The person.</param>
    /// <param name="permission">The permission.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<bool> HasAsync(Guid operatorUserId, OperatorRight permission, CancellationToken cancellationToken = default)
        => OperatorRights.Allows(await RankAsync(operatorUserId, cancellationToken).ConfigureAwait(false), permission);

    /// <summary>A refusal when the operator lacks the permission (written to the audit log), else null.</summary>
    /// <param name="operatorUserId">The person.</param>
    /// <param name="permission">The permission the operation needs.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="operation">The operation, for the audit log (the calling method's name).</param>
    public async Task<AdminResult?> RequireAsync(Guid operatorUserId, OperatorRight permission, CancellationToken cancellationToken = default, [CallerMemberName] string operation = "")
    {
        PlatformRole? role = await RankAsync(operatorUserId, cancellationToken).ConfigureAwait(false);
        if (OperatorRights.Allows(role, permission))
        {
            return null;
        }

        PlatformRole needed = OperatorRights.LowestRank(permission);
        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AccessDenied,
                AuditActorType.Admin,
                operatorUserId,
                TargetType: "console",
                Metadata: JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["console"] = "admin",
                    ["operation"] = operation,
                    ["permission"] = permission.ToString(),
                    ["reason"] = role is null ? "not_operator" : "rank_too_low",
                    ["needed"] = needed.ToString(),
                    ["held"] = role?.ToString(),
                })),
            cancellationToken).ConfigureAwait(false);
        return role is null
            ? AdminResult.Refused("You do not have console access.")
            : AdminResult.Refused($"That needs {PlatformRanks.Label(needed)} access.");
    }
}
