using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Partners;

/// <summary>
/// <see cref="IPartnerService"/> over the identity database. Roles, organisations and memberships
/// go through the same <see cref="IManagementService"/> the HTTP API uses, so the rules are one set;
/// this layer adds who may call it, whom they may see, and the audit attribution.
/// </summary>
public sealed class EfPartnerService : IPartnerService
{
    private readonly SangamDbContext _db;
    private readonly IManagementService _management;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ISecurityPolicyService _policies;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="management">The shared tenancy rules.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="policies">Security policies (PR-16).</param>
    public EfPartnerService(SangamDbContext db, IManagementService management, IAuditWriter audit, IClock clock, ISecurityPolicyService policies)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _management = management ?? throw new ArgumentNullException(nameof(management));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PartnerAppRow>> GetMyAppsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.AppAdmins.AsNoTracking()
            .Where(a => a.UserId == userId && a.RevokedAt == null && !a.App!.IsPlatform)
            .OrderBy(a => a.App!.DisplayName)
            .Select(a => new { a.AppId, a.App!.ClientId, a.App.DisplayName, a.App.BrandColour, a.App.Glyph, a.Role, a.App.Status })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new PartnerAppRow(r.AppId, r.ClientId, r.DisplayName, r.BrandColour, r.Glyph, r.Role, r.Status))];
    }

    /// <inheritdoc />
    public async Task<AppAdminRole?> GetRoleAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
    {
        AppAdmin? admin = await _db.AppAdmins.AsNoTracking()
            .FirstOrDefaultAsync(a => a.UserId == userId && a.AppId == appId && a.RevokedAt == null && !a.App!.IsPlatform, cancellationToken).ConfigureAwait(false);

        // Sangam's own clients are never a partner's to manage, even if a row says otherwise:
        // every method here checks this first, so nothing below can reach one.
        return admin?.Role;
    }

    // ------------------------------------------------------------------------------ roles

    /// <inheritdoc />
    public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
        => await GetRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null
            ? []
            : await _management.ListRolesAsync(appId, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<PartnerResult> UpsertRoleAsync(Guid userId, Guid appId, string code, RoleUpsert input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(input);
        if (await GetRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotYours;
        }

        ManagementResult<RoleDto> result = await _management.UpsertRoleAsync(appId, code, input, ManagementActor.AppAdmin(userId), cancellationToken).ConfigureAwait(false);
        return From(result, $"Role {code} saved.");
    }

    /// <inheritdoc />
    public async Task<PartnerResult> RetireRoleAsync(Guid userId, Guid appId, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (await GetRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotYours;
        }

        ManagementResult<RoleDto> result = await _management.RetireRoleAsync(appId, code, ManagementActor.AppAdmin(userId), cancellationToken).ConfigureAwait(false);
        return From(result, $"Role {code} retired. People who held it keep their membership record, but it no longer appears in their tokens.");
    }

    // ------------------------------------------------------------------------------ organisations

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrganisationDto>> ListOrganisationsAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
    {
        if (await GetRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return [];
        }

        return await _db.Organisations.AsNoTracking()
            .Where(o => o.RegisteredViaAppId == appId && o.DeletedAt == null)
            .OrderBy(o => o.Path)
            .Select(o => new OrganisationDto(o.Id, o.Name, o.OrgTypeCode, o.ParentOrgId, o.Path, o.Depth, o.Status, o.Metadata))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrgTypeRow>> ListOrgTypesAsync(CancellationToken cancellationToken = default)
        => await _db.OrgTypes.AsNoTracking()
            .OrderBy(t => t.SortOrder)
            .Select(t => new OrgTypeRow(t.Code, t.DisplayName, t.CanBeRoot, t.CanHaveChildren))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<PartnerResult> UpsertOrganisationAsync(Guid userId, Guid appId, Guid orgId, OrganisationUpsert input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (await GetRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotYours;
        }

        ManagementResult<OrganisationDto> result = await _management.UpsertOrganisationAsync(appId, orgId, input, ManagementActor.AppAdmin(userId), cancellationToken).ConfigureAwait(false);
        return From(result, $"{input.Name} saved.");
    }

    // ------------------------------------------------------------------------------ people

    /// <inheritdoc />
    public async Task<IReadOnlyList<LinkedUserRow>> SearchLinkedUsersAsync(Guid userId, Guid appId, string? query, int take, CancellationToken cancellationToken = default)
    {
        if (await GetRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return [];
        }

        // Only people with a live grant to this application. This is the privacy boundary: the
        // partner console is never a window onto Sangam's wider directory.
        IQueryable<AppGrant> grants = _db.AppGrants.AsNoTracking()
            .Where(g => g.AppId == appId && g.RevokedAt == null && g.User!.Status == UserStatus.Active);

        if (!string.IsNullOrWhiteSpace(query))
        {
            string term = query.Trim();
            grants = grants.Where(g =>
                EF.Functions.ILike(g.User!.Email!, $"%{term}%")
                || EF.Functions.ILike(g.User!.FirstName + " " + g.User.LastName, $"%{term}%"));
        }

        var rows = await grants
            .OrderBy(g => g.User!.FirstName).ThenBy(g => g.User!.LastName)
            .Take(Math.Clamp(take, 1, 100))
            .Select(g => new { g.UserId, g.User!.FirstName, g.User.LastName, g.User.Email, g.GrantedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(r => new LinkedUserRow(r.UserId, (r.FirstName + " " + r.LastName).Trim(), r.Email ?? string.Empty, r.GrantedAt))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PartnerMemberRow>> ListMembersAsync(Guid userId, Guid appId, Guid orgId, CancellationToken cancellationToken = default)
    {
        if (await GetRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return [];
        }

        var rows = await _db.OrgMemberships.AsNoTracking()
            .Where(m => m.AppId == appId && m.OrgId == orgId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > DateTimeOffset.UtcNow))
            .OrderBy(m => m.User!.FirstName)
            .Select(m => new { m.UserId, m.User!.FirstName, m.User.LastName, m.User.Email, Role = m.Role!.Code, m.AppliesToDescendants, m.GrantedAt, m.ExpiresAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(r => new PartnerMemberRow(r.UserId, (r.FirstName + " " + r.LastName).Trim(), r.Email ?? string.Empty, r.Role, r.AppliesToDescendants, r.GrantedAt, r.ExpiresAt))];
    }

    /// <inheritdoc />
    public async Task<PartnerResult> GrantMembershipAsync(Guid userId, Guid appId, Guid orgId, Guid memberUserId, MembershipUpsert input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (await GetRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotYours;
        }

        // The management API may create the link itself, because the application is acting for a
        // person it already knows. A partner's staff member may not: a person joins an application
        // by consenting to it, never by being added to it by someone else.
        if (!await IsLinkedAsync(appId, memberUserId, cancellationToken).ConfigureAwait(false))
        {
            return PartnerResult.Refused("That person has not linked this application. They must sign in to it once and allow access first.");
        }

        ManagementResult<MembershipDto> result = await _management
            .UpsertMembershipAsync(appId, orgId, memberUserId, input, ManagementActor.AppAdmin(userId), cancellationToken).ConfigureAwait(false);
        return From(result, "Role granted.");
    }

    /// <inheritdoc />
    public async Task<PartnerResult> RevokeMembershipAsync(Guid userId, Guid appId, Guid orgId, Guid memberUserId, CancellationToken cancellationToken = default)
    {
        if (await GetRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotYours;
        }

        ManagementResult<MembershipDto> result = await _management
            .RevokeMembershipAsync(appId, orgId, memberUserId, ManagementActor.AppAdmin(userId), cancellationToken).ConfigureAwait(false);
        return From(result, "Role removed.");
    }

    // ------------------------------------------------------------------------------ administrators

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppAdminRow>> ListAdminsAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
    {
        if (await GetRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is not AppAdminRole.Owner)
        {
            return [];
        }

        var rows = await _db.AppAdmins.AsNoTracking()
            .Where(a => a.AppId == appId && a.RevokedAt == null)
            .OrderByDescending(a => a.Role).ThenBy(a => a.GrantedAt)
            .Select(a => new { a.UserId, a.User!.FirstName, a.User.LastName, a.User.Email, a.Role, a.User.TwoFactorEnabled, a.GrantedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(r => new AppAdminRow(r.UserId, (r.FirstName + " " + r.LastName).Trim(), r.Email ?? string.Empty, r.Role, r.TwoFactorEnabled, r.GrantedAt))];
    }

    /// <inheritdoc />
    public async Task<PartnerResult> GrantAdminAsync(Guid userId, Guid appId, string email, AppAdminRole role, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        if (await GetRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is not AppAdminRole.Owner)
        {
            return PartnerResult.Refused("Only an owner of this application can change its administrators.");
        }

        string normalised = email.Trim().ToUpperInvariant();
        SangamUser? user = await _db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalised, cancellationToken).ConfigureAwait(false);

        // One answer for "no such account", "unverified" and "never linked this application", so the
        // form cannot be used to test whether an address is registered with Sangam.
        const string NotEligible = "Only someone who has linked this application, with a verified account, can be made an administrator.";
        if (user is null || user.Status != UserStatus.Active || !user.EmailConfirmed || !await IsLinkedAsync(appId, user.Id, cancellationToken).ConfigureAwait(false))
        {
            return PartnerResult.Refused(NotEligible);
        }

        AppAdmin? existing = await _db.AppAdmins.FirstOrDefaultAsync(a => a.AppId == appId && a.UserId == user.Id && a.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Role == AppAdminRole.Owner && role != AppAdminRole.Owner && await OwnerCountAsync(appId, cancellationToken).ConfigureAwait(false) <= 1)
            {
                return PartnerResult.Refused("That is the last owner. Make someone else an owner first.");
            }

            existing.Role = role;
        }
        else
        {
            _db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = appId, UserId = user.Id, Role = role, GrantedByUserId = userId, GrantedAt = _clock.UtcNow });
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AppAdminGrant, AuditActorType.Admin, userId, appId, "user", user.Id,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, string> { ["role"] = role.ToString().ToLowerInvariant() })),
            cancellationToken).ConfigureAwait(false);

        return PartnerResult.Ok(user.TwoFactorEnabled
            ? $"{user.FirstName} is now an {(role == AppAdminRole.Owner ? "owner" : "administrator")}."
            : $"{user.FirstName} is now an {(role == AppAdminRole.Owner ? "owner" : "administrator")}, but must set up an authenticator app before the partner console will let them in.");
    }

    /// <inheritdoc />
    public async Task<PartnerResult> RevokeAdminAsync(Guid userId, Guid appId, Guid adminUserId, CancellationToken cancellationToken = default)
    {
        if (await GetRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is not AppAdminRole.Owner)
        {
            return PartnerResult.Refused("Only an owner of this application can change its administrators.");
        }

        AppAdmin? admin = await _db.AppAdmins.FirstOrDefaultAsync(a => a.AppId == appId && a.UserId == adminUserId && a.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (admin is null)
        {
            return PartnerResult.Refused("That person is not an administrator of this application.");
        }

        if (admin.Role == AppAdminRole.Owner && await OwnerCountAsync(appId, cancellationToken).ConfigureAwait(false) <= 1)
        {
            return PartnerResult.Refused("That is the last owner. Make someone else an owner first, or nobody could manage this application's administrators.");
        }

        admin.RevokedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.AppAdminRevoke, AuditActorType.Admin, userId, appId, "user", adminUserId), cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok("Administrator removed.");
    }

    // ------------------------------------------------------------------------------ settings

    /// <inheritdoc />
    public async Task<PartnerAppSettings?> GetSettingsAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
    {
        if (await GetRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        App? app = await _db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, cancellationToken).ConfigureAwait(false);
        return app is null ? null : new PartnerAppSettings(app.DisplayName, app.Description, app.BrandColour, app.Glyph, app.SignInPolicy);
    }

    /// <inheritdoc />
    public async Task<PartnerResult> UpdateSettingsAsync(Guid userId, Guid appId, string? description, string brandColour, string glyph, SignInPolicy signInPolicy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brandColour);
        ArgumentNullException.ThrowIfNull(glyph);
        if (await GetRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotYours;
        }

        App? app = await _db.Apps.FirstOrDefaultAsync(a => a.Id == appId, cancellationToken).ConfigureAwait(false);
        if (app is null)
        {
            return NotYours;
        }

        if (!IsHexColour(brandColour))
        {
            return PartnerResult.Refused("The brand colour must be a hex colour such as #1D4E89.");
        }

        int glyphLength = new StringInfo(glyph.Trim()).LengthInTextElements;
        if (glyphLength is < 1 or > 2)
        {
            return PartnerResult.Refused("The tile letter must be one or two characters.");
        }

        if (!MayMoveTo(app.SignInPolicy, signInPolicy))
        {
            return PartnerResult.Refused(
                "Partners can require two-step sign-in, or leave the choice to each user. Password-only and email-code-only are set by Sangam, because they would weaken sign-in for people who chose two-step.");
        }

        SignInPolicy before = app.SignInPolicy;
        app.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        app.BrandColour = brandColour.Trim().ToUpperInvariant();
        app.Glyph = glyph.Trim();
        app.SignInPolicy = signInPolicy;
        app.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AppSettingsUpdate, AuditActorType.Admin, userId, appId, "app", appId,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["sign_in_policy_before"] = before.ToString(),
                    ["sign_in_policy_after"] = signInPolicy.ToString(),
                })),
            cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok("Settings saved.");
    }

    /// <summary>
    /// Whether a partner may move the policy from <paramref name="from"/> to <paramref name="to"/>.
    /// Sangam's floor is <see cref="SignInPolicy.Default"/> — each person's own choice. A partner may
    /// require two-step, or return to the floor; never below it. <see cref="SignInPolicy.Password"/>
    /// would override people who chose two-step, and <see cref="SignInPolicy.OtpOnly"/> makes the
    /// mailbox the whole account; both are the platform operators' decision. A platform-set value of either can
    /// only be tightened from here: to two-step, or to passkey only (PR-16).
    /// </summary>
    internal static bool MayMoveTo(SignInPolicy from, SignInPolicy to)
        => from == to
        || to is SignInPolicy.PasswordAndOtp or SignInPolicy.PasskeyOnly
        || (to == SignInPolicy.Default && from is SignInPolicy.Default or SignInPolicy.PasswordAndOtp or SignInPolicy.PasskeyOnly);

    // ------------------------------------------------------------------------------ security policy (PR-16)

    /// <inheritdoc />
    public async Task<PolicyView?> GetAppPolicyAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
    {
        if (await GetRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        App? app = await _db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, cancellationToken).ConfigureAwait(false);
        return app is null
            ? null
            : new PolicyView(
                _policies.Platform,
                app.SignInPolicy,
                app.MinPasswordLength,
                app.MfaRequirement == MfaRequirement.Optional ? null : app.MfaRequirement,
                app.BreachedPasswordCheck ? true : null,
                _policies.BreachCheckAvailable,
                app.RequireCharacterTypes ? true : null);
    }

    /// <inheritdoc />
    public async Task<PartnerResult> UpdateAppPolicyAsync(Guid userId, Guid appId, PolicyInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (await GetRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotYours;
        }

        App? app = await _db.Apps.FirstOrDefaultAsync(a => a.Id == appId, cancellationToken).ConfigureAwait(false);
        if (app is null)
        {
            return NotYours;
        }

        string? weaker = _policies.Platform.WhyWeaker(null, input.MinPasswordLength, input.Mfa, input.BreachedPasswordCheck, input.RequireCharacterTypes);
        if (weaker is not null)
        {
            return PartnerResult.Refused(weaker);
        }

        if (input.BreachedPasswordCheck == true && !_policies.BreachCheckAvailable)
        {
            return PartnerResult.Refused("The breached-password check is not switched on for Sangam yet, so it cannot be required.");
        }

        string before = PolicyJson(null, app.MinPasswordLength, app.MfaRequirement, app.BreachedPasswordCheck, app.RequireCharacterTypes);
        app.MinPasswordLength = input.MinPasswordLength;
        app.MfaRequirement = input.Mfa ?? MfaRequirement.Optional;
        app.BreachedPasswordCheck = input.BreachedPasswordCheck == true;
        app.RequireCharacterTypes = input.RequireCharacterTypes == true;
        app.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AppPolicyUpdate, AuditActorType.Admin, userId, appId, "app", appId,
                Metadata: $"{{\"before\":{before},\"after\":{PolicyJson(null, app.MinPasswordLength, app.MfaRequirement, app.BreachedPasswordCheck, app.RequireCharacterTypes)}}}"),
            cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok("Security policy saved. It applies from each person's next sign-in.");
    }

    /// <inheritdoc />
    public async Task<PolicyView?> GetOrganisationPolicyAsync(Guid userId, Guid appId, Guid orgId, CancellationToken cancellationToken = default)
    {
        if (await GetRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        Organisation? org = await FindOrganisationAsync(appId, orgId, tracked: false, cancellationToken).ConfigureAwait(false);
        return org is null
            ? null
            : new PolicyView(
                await InheritedAsync(appId, org, cancellationToken).ConfigureAwait(false),
                org.SignInPolicy,
                org.MinPasswordLength,
                org.MfaRequirement,
                org.BreachedPasswordCheck,
                _policies.BreachCheckAvailable,
                org.RequireCharacterTypes);
    }

    /// <inheritdoc />
    public async Task<PartnerResult> UpdateOrganisationPolicyAsync(Guid userId, Guid appId, Guid orgId, PolicyInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (await GetRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotYours;
        }

        Organisation? org = await FindOrganisationAsync(appId, orgId, tracked: true, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return PartnerResult.Refused("That organisation is not in this application.");
        }

        if (input.SignIn is not (null or SignInPolicy.PasswordAndOtp or SignInPolicy.PasskeyOnly))
        {
            return PartnerResult.Refused("An organisation can require two-step sign-in or a passkey, or follow the application's rule.");
        }

        SecurityPolicy inherited = await InheritedAsync(appId, org, cancellationToken).ConfigureAwait(false);
        string? weaker = inherited.WhyWeaker(input.SignIn, input.MinPasswordLength, input.Mfa, input.BreachedPasswordCheck, input.RequireCharacterTypes);
        if (weaker is not null)
        {
            return PartnerResult.Refused(weaker);
        }

        if (input.BreachedPasswordCheck == true && !_policies.BreachCheckAvailable)
        {
            return PartnerResult.Refused("The breached-password check is not switched on for Sangam yet, so it cannot be required.");
        }

        string before = PolicyJson(org.SignInPolicy, org.MinPasswordLength, org.MfaRequirement, org.BreachedPasswordCheck, org.RequireCharacterTypes);
        org.SignInPolicy = input.SignIn;
        org.MinPasswordLength = input.MinPasswordLength;
        org.MfaRequirement = input.Mfa;
        org.BreachedPasswordCheck = input.BreachedPasswordCheck == true ? true : null;
        org.RequireCharacterTypes = input.RequireCharacterTypes == true ? true : null;
        org.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.OrgPolicyUpdate, AuditActorType.Admin, userId, appId, "organisation", orgId,
                Metadata: $"{{\"before\":{before},\"after\":{PolicyJson(org.SignInPolicy, org.MinPasswordLength, org.MfaRequirement, org.BreachedPasswordCheck, org.RequireCharacterTypes)}}}"),
            cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok($"Security policy for {org.Name} saved. It applies to everyone in it and below it from their next sign-in.");
    }

    private Task<Organisation?> FindOrganisationAsync(Guid appId, Guid orgId, bool tracked, CancellationToken cancellationToken)
    {
        IQueryable<Organisation> orgs = tracked ? _db.Organisations : _db.Organisations.AsNoTracking();
        return orgs.FirstOrDefaultAsync(o => o.Id == orgId && o.RegisteredViaAppId == appId && o.DeletedAt == null, cancellationToken);
    }

    /// <summary>The application's policy tightened by every ancestor of <paramref name="org"/> (not the organisation itself).</summary>
    private async Task<SecurityPolicy> InheritedAsync(Guid appId, Organisation org, CancellationToken cancellationToken)
    {
        SecurityPolicy policy = await _policies.ForAppAsync(appId, cancellationToken).ConfigureAwait(false);
        List<Guid> ancestors = [.. OrganisationPath.Ids(org.Path).Where(id => id != org.Id)];
        if (ancestors.Count == 0)
        {
            return policy;
        }

        Dictionary<Guid, Organisation> rows = await _db.Organisations.AsNoTracking()
            .Where(o => ancestors.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, cancellationToken)
            .ConfigureAwait(false);
        foreach (Guid id in ancestors)
        {
            if (rows.TryGetValue(id, out Organisation? parent))
            {
                policy = policy.Tighten(parent.SignInPolicy, parent.MinPasswordLength, parent.MfaRequirement, parent.BreachedPasswordCheck, parent.RequireCharacterTypes);
            }
        }

        return policy;
    }

    private static string PolicyJson(SignInPolicy? signIn, int? minLength, MfaRequirement? mfa, bool? breach, bool? characterTypes)
        => JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["sign_in"] = signIn?.ToString(),
            ["min_password_length"] = minLength?.ToString(CultureInfo.InvariantCulture),
            ["mfa"] = mfa?.ToString(),
            ["breach_check"] = breach?.ToString(),
            ["character_types"] = characterTypes?.ToString(),
        });

    private static PartnerResult NotYours { get; } = PartnerResult.Refused("You do not administer this application.");

    private async Task<bool> IsLinkedAsync(Guid appId, Guid userId, CancellationToken cancellationToken)
        => await _db.AppGrants.AnyAsync(g => g.AppId == appId && g.UserId == userId && g.RevokedAt == null, cancellationToken).ConfigureAwait(false);

    private Task<int> OwnerCountAsync(Guid appId, CancellationToken cancellationToken)
        => _db.AppAdmins.CountAsync(a => a.AppId == appId && a.Role == AppAdminRole.Owner && a.RevokedAt == null, cancellationToken);

    private static bool IsHexColour(string value)
    {
        string v = value.Trim();
        return v.Length == 7 && v[0] == '#' && v.Skip(1).All(char.IsAsciiHexDigit);
    }

    private static PartnerResult From<T>(ManagementResult<T> result, string success)
        => result.Status == ManagementStatus.Ok ? PartnerResult.Ok(success) : PartnerResult.Refused(result.Message ?? "That could not be saved.");
}
