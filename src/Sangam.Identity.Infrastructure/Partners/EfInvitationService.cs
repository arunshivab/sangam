using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Infrastructure.Partners;

/// <summary>Invitations by e-mail (PR-13). See <see cref="IInvitationService"/>.</summary>
public sealed class EfInvitationService : IInvitationService
{
    /// <summary>How long an invitation works.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private static readonly EmailAddressAttribute EmailFormat = new();
    private readonly SangamDbContext _db;
    private readonly UserManager<SangamUser> _users;
    private readonly IManagementService _management;
    private readonly IEmailSender _email;
    private readonly IMessageTemplates _templates;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly string _origin;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="users">User manager.</param>
    /// <param name="management">Management service (grants the membership on acceptance).</param>
    /// <param name="email">E-mail sender.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="configuration">Configuration (<c>Sangam:Issuer</c> for the link).</param>
    /// <param name="templates">Message templates (PR-19).</param>
    public EfInvitationService(SangamDbContext db, UserManager<SangamUser> users, IManagementService management, IEmailSender email, IAuditWriter audit, IClock clock, IConfiguration configuration, IMessageTemplates templates)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _management = management ?? throw new ArgumentNullException(nameof(management));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        string? issuer = configuration["Sangam:Issuer"];
        _origin = string.IsNullOrWhiteSpace(issuer) ? "https://id.sangamid.in" : issuer.TrimEnd('/');
    }

    /// <inheritdoc />
    public async Task<PartnerResult> CreateAsync(Guid inviterUserId, Guid appId, Guid orgId, string email, string roleCode, bool appliesToDescendants, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(roleCode);
        if (!await IsAdminAsync(inviterUserId, appId, cancellationToken).ConfigureAwait(false))
        {
            return PartnerResult.Refused("You do not administer this application.");
        }

        string address = email.Trim();
        if (address.Length == 0 || address.Length > 256 || !EmailFormat.IsValid(address))
        {
            return PartnerResult.Refused("Enter a valid email address.");
        }

        Organisation? org = await _db.Organisations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orgId && o.RegisteredViaAppId == appId && o.DeletedAt == null, cancellationToken).ConfigureAwait(false);
        Role? role = await _db.Roles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.AppId == appId && r.Code == roleCode && r.RetiredAt == null && (r.OrgId == null || r.OrgId == orgId), cancellationToken).ConfigureAwait(false);
        App? app = await _db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, cancellationToken).ConfigureAwait(false);
        if (org is null || role is null || app is null)
        {
            return PartnerResult.Refused("That organisation or role does not exist in this application.");
        }

        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        DateTimeOffset now = _clock.UtcNow;
        _db.Invitations.Add(new Invitation
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            OrgId = orgId,
            RoleCode = role.Code,
            AppliesToDescendants = appliesToDescendants,
            Email = address,
            NormalizedEmail = _users.NormalizeEmail(address),
            TokenHash = Hash(token),
            InvitedByUserId = inviterUserId,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["application"] = app.DisplayName,
            ["organisation"] = org.Name,
            ["role"] = role.DisplayName,
            ["link"] = $"{_origin}/invite/{token}",
            ["days"] = ((int)Lifetime.TotalDays).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        // In the inviter's language (the invitee may not have an account yet), with the organisation's and application's own wording if set.
        EmailMessage invitation = await _templates.EmailAsync(MessageTemplateKinds.Invitation, System.Globalization.CultureInfo.CurrentUICulture.Name, appId, orgId, values, address, address, cancellationToken).ConfigureAwait(false);
        await _email.SendAsync(invitation, cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.AppInvitationCreate, AuditActorType.Admin, inviterUserId, appId, "organisation", orgId,
            Metadata: JsonSerializer.Serialize(new Dictionary<string, string> { ["email"] = LogRedaction.MaskEmail(address), ["role"] = role.Code })), cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok($"Invitation sent to {address}. It works for {(int)Lifetime.TotalDays} days.");
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

        ManagementResult<MembershipDto> granted = await _management.UpsertMembershipAsync(invitation.AppId, invitation.OrgId, userId,
            new MembershipUpsert(invitation.RoleCode, invitation.AppliesToDescendants), ManagementActor.AppAdmin(invitation.InvitedByUserId), cancellationToken).ConfigureAwait(false);
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

        if (invitation.RevokedAt is not null || !await IsAdminAsync(invitation.InvitedByUserId, invitation.AppId, cancellationToken).ConfigureAwait(false))
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
