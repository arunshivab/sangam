using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Infrastructure.Partners;

/// <summary>Invitations by e-mail (PR-13): accepting the links already sent. See <see cref="IInvitationService"/>.</summary>
public sealed class EfInvitationService : IInvitationService
{
    /// <summary>How long an invitation works.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private readonly SangamDbContext _db;
    private readonly UserManager<SangamUser> _users;
    private readonly IManagementService _management;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="users">User manager.</param>
    /// <param name="management">Management service (grants the membership on acceptance).</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    public EfInvitationService(SangamDbContext db, UserManager<SangamUser> users, IManagementService management, IAuditWriter audit, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _management = management ?? throw new ArgumentNullException(nameof(management));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<InvitationView?> GetAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        Invitation? invitation = await FindAsync(token, cancellationToken).ConfigureAwait(false);
        if (invitation is null)
        {
            return null;
        }

        string appName = await _db.Apps.Where(a => a.Id == invitation.AppId).Select(a => a.DisplayName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        string orgName = await _db.Organisations.Where(o => o.Id == invitation.OrgId).Select(o => o.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        string roleName = await _db.Roles.Where(r => r.AppId == invitation.AppId && r.Code == invitation.RoleCode).Select(r => r.DisplayName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? invitation.RoleCode;
        return new InvitationView(appName, orgName, roleName, invitation.Email, await StateAsync(invitation, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<PartnerResult> AcceptAsync(string token, Guid userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        Invitation? invitation = await FindAsync(token, cancellationToken).ConfigureAwait(false);
        if (invitation is null)
        {
            return PartnerResult.Refused("This invitation link is not valid.");
        }

        InvitationState state = await StateAsync(invitation, cancellationToken).ConfigureAwait(false);
        if (state != InvitationState.Open)
        {
            return PartnerResult.Refused(state switch
            {
                InvitationState.Used => "This invitation has already been accepted.",
                InvitationState.Expired => "This invitation has expired. Ask for a new one.",
                _ => "This invitation is no longer valid.",
            });
        }

        SangamUser user = await _users.FindByIdAsync(userId.ToString("D")).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Unknown user.");
        if (!user.EmailConfirmed || !string.Equals(user.NormalizedEmail, invitation.NormalizedEmail, StringComparison.Ordinal))
        {
            return PartnerResult.Refused($"This invitation was sent to {LogRedaction.MaskEmail(invitation.Email)}. Sign in with that address to accept it.");
        }

        // rc.12: the role is given at that organisation only, whatever the old invitation said about below it.
        // Accepting is the person's consent (rc.5): an invitation the application sent through the API is granted as
        // the person's own act; one an administrator sent, as theirs.
        ManagementActor actor = invitation.InvitedByUserId is Guid inviter ? ManagementActor.AppAdmin(inviter) : ManagementActor.Person(userId);
        ManagementResult<MembershipDto> granted = await _management.UpsertMembershipAsync(invitation.AppId, invitation.OrgId, userId,
            new MembershipUpsert(invitation.RoleCode, false), actor, cancellationToken).ConfigureAwait(false);
        if (granted.Status != ManagementStatus.Ok)
        {
            return PartnerResult.Refused("This invitation is no longer valid.");
        }

        invitation.AcceptedAt = _clock.UtcNow;
        invitation.AcceptedByUserId = userId;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.AppInvitationAccept, AuditActorType.User, userId, invitation.AppId, "organisation", invitation.OrgId), cancellationToken).ConfigureAwait(false);
        string appName = await _db.Apps.Where(a => a.Id == invitation.AppId).Select(a => a.DisplayName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? "the application";
        return PartnerResult.Ok($"You have joined. Sign in to {appName} with Sangam to use your new role.");
    }

    private static string Hash(string token)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private Task<Invitation?> FindAsync(string token, CancellationToken cancellationToken)
    {
        string hash = Hash(token);
        return _db.Invitations.FirstOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);
    }

    private async Task<InvitationState> StateAsync(Invitation invitation, CancellationToken cancellationToken)
    {
        if (invitation.AcceptedAt is not null)
        {
            return InvitationState.Used;
        }

        bool inviterStillMayInvite = invitation.InvitedByUserId is Guid inviter
            ? await IsAdminAsync(inviter, invitation.AppId, cancellationToken).ConfigureAwait(false)
            : await _db.Apps.AnyAsync(a => a.Id == invitation.AppId && a.Status == AppStatus.Active, cancellationToken).ConfigureAwait(false);
        if (invitation.RevokedAt is not null || !inviterStillMayInvite)
        {
            return InvitationState.Withdrawn;
        }

        return invitation.ExpiresAt <= _clock.UtcNow ? InvitationState.Expired : InvitationState.Open;
    }

    private Task<bool> IsAdminAsync(Guid userId, Guid appId, CancellationToken cancellationToken)
    {
        return _db.AppAdmins.AnyAsync(a => a.AppId == appId && a.UserId == userId && a.RevokedAt == null, cancellationToken);
    }
}
