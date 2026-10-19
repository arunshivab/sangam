using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Infrastructure.Tenancy;

/// <summary><see cref="IStaffService"/> over the identity database (rc.12, sections 2 to 5 of the design).</summary>
public sealed partial class EfStaffService : IStaffService
{
    /// <summary>At most this many checks per person per admin per day (4.3).</summary>
    public const int ChecksPerPersonPerDay = 5;

    /// <summary>Wrong activation codes before the link stops working.</summary>
    public const int WrongCodesAllowed = 5;

    /// <summary>Wrong join codes an admin may enter in an hour.</summary>
    public const int WrongJoinCodesPerHour = 10;

    /// <summary>
    /// How long after its first use the same admin may use a join code again, so one code adds the person to every
    /// organisation the admin ticked (in several applications, from the Sangam console).
    /// </summary>
    public static readonly TimeSpan JoinCodeBatch = TimeSpan.FromMinutes(15);

    private const string FirstNameField = "first_name";
    private const string LastNameField = "last_name";
    private const string DateOfBirthField = "date_of_birth";
    private const string EmailField = "email";
    private const string MobileField = "mobile";
    private const string BadCodeOutcome = "bad_code";

    private static readonly EmailAddressAttribute EmailFormat = new();
    private static readonly PasswordHasher<StaffInvite> CodeHasher = new();
    private readonly SangamDbContext _db;
    private readonly StaffRights _rights;
    private readonly IManagementService _management;
    private readonly IMessageTemplates _templates;
    private readonly IEmailSender _email;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly string _origin;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="rights">Who may manage staff where.</param>
    /// <param name="management">The tenancy rules, through which memberships are given.</param>
    /// <param name="templates">Message templates.</param>
    /// <param name="email">E-mail sender.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="configuration">Configuration (<c>Sangam:Issuer</c> for the link).</param>
    public EfStaffService(SangamDbContext db, StaffRights rights, IManagementService management, IMessageTemplates templates, IEmailSender email, IAuditWriter audit, IClock clock, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _rights = rights ?? throw new ArgumentNullException(nameof(rights));
        _management = management ?? throw new ArgumentNullException(nameof(management));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        string? issuer = configuration["Sangam:Issuer"];
        _origin = string.IsNullOrWhiteSpace(issuer) ? "https://id.sangamid.in" : issuer.TrimEnd('/');
    }

    // ------------------------------------------------------------------ the check (4.3)

    /// <inheritdoc />
    public async Task<StaffCheckResult?> CheckAsync(StaffActor actor, Guid orgId, StaffDetails details, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(details);
        Organisation? org = await _rights.OrganisationAsync(actor.AppId, orgId, cancellationToken).ConfigureAwait(false);
        if (org is null || !await _rights.ManagesAsync(actor.AppId, org, actor.AdminUserId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (Invalid(details) is string problem)
        {
            return new StaffCheckResult(StaffCheckOutcome.None, [], [], problem, await ChecksLeftAsync(actor.AdminUserId, details, cancellationToken).ConfigureAwait(false));
        }

        return (await RunCheckAsync(actor, org, details, cancellationToken).ConfigureAwait(false)).Result;
    }

    // ------------------------------------------------------------------ adding

    /// <inheritdoc />
    public async Task<StaffAddResult> AddAsync(StaffActor actor, Guid orgId, StaffAddition addition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(addition);
        Organisation? org = await _rights.OrganisationAsync(actor.AppId, orgId, cancellationToken).ConfigureAwait(false);
        if (org is null || !await _rights.ManagesAsync(actor.AppId, org, actor.AdminUserId, cancellationToken).ConfigureAwait(false))
        {
            return new StaffAddResult(StaffAddStatus.NotFound, "The organisation does not exist, or you are not an admin there.");
        }

        if (org.Status != OrganisationStatus.Active)
        {
            return new StaffAddResult(StaffAddStatus.Refused, "The organisation is not active.");
        }

        (bool may, bool beforeDigiLocker) = await _rights.MayAddAsync(actor.AppId, actor.AdminUserId, cancellationToken).ConfigureAwait(false);
        if (!may)
        {
            return new StaffAddResult(StaffAddStatus.Refused, "Only an admin verified with DigiLocker can add people. Verify your account, or ask a verified admin above you to add this person.");
        }

        int ways = (addition.Details is null ? 0 : 1) + (string.IsNullOrWhiteSpace(addition.JoinCode) ? 0 : 1) + (addition.StaffRef is null ? 0 : 1) + (addition.UserId is null ? 0 : 1);
        if (ways != 1)
        {
            return new StaffAddResult(StaffAddStatus.Refused, "Say who to add in one way: their details, their join code, the reference from an earlier addition, or their Sangam id.");
        }

        List<string> roles = [.. (addition.Roles ?? []).Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal)];
        if (await RolesProblemAsync(actor.AppId, org, roles, cancellationToken).ConfigureAwait(false) is string rolesProblem)
        {
            return new StaffAddResult(StaffAddStatus.Refused, rolesProblem);
        }

        DateTimeOffset now = _clock.UtcNow;
        DateOnly today = StaffCodes.IndiaToday(now);
        if (addition.StartsOn is DateOnly starts && (starts < today || starts > today.AddYears(1)))
        {
            return new StaffAddResult(StaffAddStatus.Refused, "A start date must be today or later, and within a year.");
        }

        if (addition.ExpiresAt is DateTimeOffset ends && (ends <= now || ends > now.AddYears(5)))
        {
            return new StaffAddResult(StaffAddStatus.Refused, "A time-limited role must end in the future, and at most five years from now.");
        }

        DateOnly? startsOn = addition.StartsOn is DateOnly later && later > today ? later : null;
        Placement placement = new(org, roles, startsOn, addition.ExpiresAt?.ToUniversalTime(), beforeDigiLocker);

        if (addition.JoinCode is { } joinCode && !string.IsNullOrWhiteSpace(joinCode))
        {
            return await AddByJoinCodeAsync(actor, placement, joinCode, cancellationToken).ConfigureAwait(false);
        }

        if (addition.UserId is Guid userId)
        {
            return await AddKnownAsync(actor, placement, userId, cancellationToken).ConfigureAwait(false);
        }

        if (addition.StaffRef is Guid staffRef)
        {
            return await AddToInviteAsync(actor, placement, staffRef, cancellationToken).ConfigureAwait(false);
        }

        return await AddByDetailsAsync(actor, placement, addition.Details!, addition.Confirmed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Pull (4.1): the person's own join code links exactly their account.</summary>
    private async Task<StaffAddResult> AddByJoinCodeAsync(StaffActor actor, Placement placement, string typed, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        int wrong = await _db.StaffChecks.CountAsync(c => c.AdminUserId == actor.AdminUserId && c.Outcome == BadCodeOutcome && c.CheckedAt > now.AddHours(-1), cancellationToken).ConfigureAwait(false);
        if (wrong >= WrongJoinCodesPerHour)
        {
            return new StaffAddResult(StaffAddStatus.TooMany, "Too many wrong join codes. Try again in an hour.");
        }

        string normalised = StaffCodes.Normalise(typed);
        string hash = normalised.Length == 0 ? string.Empty : StaffCodes.Hash(normalised);
        DateTimeOffset batch = now - JoinCodeBatch;
        JoinCode? code = hash.Length == 0 ? null : await _db.JoinCodes
            .Where(j => j.CodeHash == hash && j.ExpiresAt > now && (j.UsedAt == null || (j.UsedByUserId == actor.AdminUserId && j.UsedAt > batch)))
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        bool personActive = code is not null && await _db.Users.AnyAsync(u => u.Id == code.UserId && u.Status == UserStatus.Active, cancellationToken).ConfigureAwait(false);
        if (code is null || !personActive)
        {
            _db.StaffChecks.Add(new StaffCheck { Id = Guid.NewGuid(), AdminUserId = actor.AdminUserId, SubjectHash = hash.Length == 0 ? StaffCodes.Hash(string.Empty) : hash, Outcome = BadCodeOutcome, CheckedAt = now });
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new StaffAddResult(StaffAddStatus.NotFound, "That join code is not right, or it has expired. Ask the person to make a new one in their Sangam portal.");
        }

        code.UsedAt ??= now;
        code.UsedByUserId ??= actor.AdminUserId;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await PlaceAsync(actor, placement, code.UserId, "join_code", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Someone already in this customer, in this application (another branch, say): no new consent is needed.</summary>
    private async Task<StaffAddResult> AddKnownAsync(StaffActor actor, Placement placement, Guid userId, CancellationToken cancellationToken)
    {
        string root = OrganisationPath.ForRoot(OrganisationPath.CustomerId(placement.Org.Path));
        DateTimeOffset now = _clock.UtcNow;
        bool inCustomer = await _db.OrgMemberships.AnyAsync(
            m => m.AppId == actor.AppId && m.UserId == userId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now) && m.Org!.Path.StartsWith(root),
            cancellationToken).ConfigureAwait(false);
        if (!inCustomer)
        {
            return new StaffAddResult(StaffAddStatus.NotFound, "This person is not in your organisation yet. Ask them for a join code, or enter their details.");
        }

        return await PlaceAsync(actor, placement, userId, "member", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Another organisation for a person added earlier: no second check, code or e-mail.</summary>
    private async Task<StaffAddResult> AddToInviteAsync(StaffActor actor, Placement placement, Guid staffRef, CancellationToken cancellationToken)
    {
        StaffInvite? invite = await _db.StaffInvites.AsNoTracking().FirstOrDefaultAsync(i => i.Id == staffRef, cancellationToken).ConfigureAwait(false);
        if (invite is null || (invite.CreatedByUserId != actor.AdminUserId && invite.AppId != actor.AppId) || invite.CancelledAt is not null)
        {
            return new StaffAddResult(StaffAddStatus.NotFound, "That reference is not one of your additions.");
        }

        if (invite.AcceptedByUserId is Guid joined)
        {
            return await PlaceAsync(actor, placement, joined, "reference", invite.Id, cancellationToken).ConfigureAwait(false);
        }

        if (invite.ExpiresAt <= _clock.UtcNow)
        {
            return new StaffAddResult(StaffAddStatus.Refused, "That activation has expired. Add the person again.");
        }

        await WaitAsync(actor, placement, invite, cancellationToken).ConfigureAwait(false);
        return new StaffAddResult(StaffAddStatus.Waiting, "Added. It starts when the person activates with your code.", invite.Id, AddedBeforeDigiLocker: placement.BeforeDigiLocker);
    }

    /// <summary>Push (4.2): checked first, then an activation code for the admin to hand over and a link by e-mail.</summary>
    private async Task<StaffAddResult> AddByDetailsAsync(StaffActor actor, Placement placement, StaffDetails details, bool confirmed, CancellationToken cancellationToken)
    {
        if (Invalid(details) is string problem)
        {
            return new StaffAddResult(StaffAddStatus.Refused, problem);
        }

        (StaffCheckResult? check, SangamUser? found) = await RunCheckAsync(actor, placement.Org, details, cancellationToken).ConfigureAwait(false);
        if (check is null)
        {
            return new StaffAddResult(StaffAddStatus.TooMany, $"You have checked this person {ChecksPerPersonPerDay} times today. Try again tomorrow, or ask them for a join code.");
        }

        if (!confirmed && check.Outcome is StaffCheckOutcome.Differs or StaffCheckOutcome.Similar)
        {
            return new StaffAddResult(StaffAddStatus.NeedsConfirmation, check.Message, Check: check);
        }

        string normalizedEmail = details.Email.Trim().ToUpperInvariant();
        DateTimeOffset now = _clock.UtcNow;
        StaffInvite? open = await _db.StaffInvites
            .Where(i => i.NormalizedEmail == normalizedEmail && i.AcceptedAt == null && i.CancelledAt == null && i.ExpiresAt > now)
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (open is not null)
        {
            // One code covers every organisation added for the person, in every application.
            await WaitAsync(actor, placement, open, cancellationToken).ConfigureAwait(false);
            string told = open.CreatedByUserId == actor.AdminUserId
                ? "Added. This person already has your activation code; it covers this organisation too."
                : "Added. Another admin has already given this person an activation code; this starts when they activate with it.";
            return new StaffAddResult(StaffAddStatus.Waiting, told, open.Id, Check: check, AddedBeforeDigiLocker: placement.BeforeDigiLocker);
        }

        string code = StaffCodes.New();
        string token = StaffCodes.NewToken();
        StaffInvite invite = new()
        {
            Id = Guid.NewGuid(),
            AppId = actor.AppId,
            FirstName = details.FirstName.Trim(),
            LastName = details.LastName.Trim(),
            Email = details.Email.Trim(),
            NormalizedEmail = normalizedEmail,
            Mobile = string.IsNullOrWhiteSpace(details.Mobile) ? null : details.Mobile.Trim(),
            DateOfBirth = details.DateOfBirth,
            ExistingUserId = check.Outcome is StaffCheckOutcome.Match or StaffCheckOutcome.Differs ? found?.Id : null,
            AcceptedDifferences = JsonSerializer.Serialize(check.Outcome == StaffCheckOutcome.Differs ? check.Differing : []),
            TokenHash = StaffCodes.Hash(token),
            CreatedByUserId = actor.AdminUserId,
            AddedBeforeDigiLocker = placement.BeforeDigiLocker,
            CreatedAt = now,
            ExpiresAt = now + StaffInvite.Lifetime,
        };
        invite.CodeHash = CodeHasher.HashPassword(invite, StaffCodes.Normalise(code));
        _db.StaffInvites.Add(invite);
        await WaitAsync(actor, placement, invite, cancellationToken).ConfigureAwait(false);

        string adminName = await NameAsync(actor.AdminUserId, cancellationToken).ConfigureAwait(false);
        string application = await _db.Apps.Where(a => a.Id == actor.AppId).Select(a => a.DisplayName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        EmailMessage message = await _templates.EmailAsync(
            MessageTemplateKinds.StaffActivation,
            CultureInfo.CurrentUICulture.Name,
            actor.AppId,
            placement.Org.Id,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = invite.FirstName,
                ["application"] = application,
                ["organisations"] = placement.Org.Name,
                ["admin"] = adminName,
                ["link"] = $"{_origin}/staff/{token}",
                ["days"] = ((int)StaffInvite.Lifetime.TotalDays).ToString(CultureInfo.InvariantCulture),
            },
            invite.Email,
            (invite.FirstName + " " + invite.LastName).Trim(),
            cancellationToken).ConfigureAwait(false);
        await _email.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return new StaffAddResult(
            StaffAddStatus.Waiting,
            $"Give this activation code to {invite.FirstName} in person: {code}. It is shown only now. We have e-mailed them a link that works with it for {(int)StaffInvite.Lifetime.TotalDays} days.",
            invite.Id,
            ActivationCode: code,
            Check: check,
            AddedBeforeDigiLocker: placement.BeforeDigiLocker);
    }

    /// <summary>A known person: given the organisation now, or on its start date.</summary>
    private async Task<StaffAddResult> PlaceAsync(StaffActor actor, Placement placement, Guid userId, string how, Guid? inviteId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;

        // rc.13: an organisation that takes only people verified with DigiLocker keeps the addition waiting until the person
        // verifies (the sweep then gives the place within a minute). Someone already there is not affected.
        bool alreadyThere = await HasLivePlaceAsync(actor.AppId, placement.Org.Id, userId, cancellationToken).ConfigureAwait(false);
        if (!alreadyThere && !await VerifiedStaff.MayJoinAsync(_db, placement.Org.Path, userId, cancellationToken).ConfigureAwait(false))
        {
            _db.PendingMemberships.Add(Pending(actor, placement, inviteId, userId, now));
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await AuditAddAsync(actor, placement, userId, how, cancellationToken, inviteId, waitsForDigiLocker: true).ConfigureAwait(false);
            await AskToVerifyAsync(actor, placement.Org, userId, cancellationToken).ConfigureAwait(false);
            return new StaffAddResult(
                StaffAddStatus.Waiting,
                "Added. This organisation takes only people verified with DigiLocker, so it starts when they verify. We have e-mailed them how.",
                UserId: userId,
                AddedBeforeDigiLocker: placement.BeforeDigiLocker,
                WaitsForDigiLocker: true);
        }

        if (placement.StartsOn is not null)
        {
            _db.PendingMemberships.Add(Pending(actor, placement, inviteId, userId, now));
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await AuditAddAsync(actor, placement, userId, how, cancellationToken).ConfigureAwait(false);
            return new StaffAddResult(StaffAddStatus.Waiting, $"Added. Access starts on {placement.StartsOn:d MMM yyyy}.", UserId: userId, AddedBeforeDigiLocker: placement.BeforeDigiLocker);
        }

        ManagementResult<MembershipDto> given = await GiveAsync(actor.AppId, placement.Org.Id, userId, placement.Roles, placement.ExpiresAt, actor.AdminUserId, cancellationToken).ConfigureAwait(false);
        if (given.Status != ManagementStatus.Ok)
        {
            return new StaffAddResult(given.Status == ManagementStatus.NotFound ? StaffAddStatus.NotFound : StaffAddStatus.Refused, given.Message ?? "The person could not be added.");
        }

        await AuditAddAsync(actor, placement, userId, how, cancellationToken).ConfigureAwait(false);
        return new StaffAddResult(StaffAddStatus.Added, "Added.", UserId: userId, AddedBeforeDigiLocker: placement.BeforeDigiLocker);
    }

    /// <summary>Records one organisation waiting on an invite.</summary>
    private async Task WaitAsync(StaffActor actor, Placement placement, StaffInvite invite, CancellationToken cancellationToken)
    {
        _db.PendingMemberships.Add(Pending(actor, placement, invite.Id, null, _clock.UtcNow));
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAddAsync(actor, placement, invite.ExistingUserId, "activation_code", cancellationToken, invite.Id).ConfigureAwait(false);
    }

    private static PendingMembership Pending(StaffActor actor, Placement placement, Guid? inviteId, Guid? userId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        InviteId = inviteId,
        UserId = userId,
        AppId = actor.AppId,
        OrgId = placement.Org.Id,
        Roles = JsonSerializer.Serialize(placement.Roles),
        StartsOn = placement.StartsOn,
        ExpiresAt = placement.ExpiresAt,
        CreatedByUserId = actor.AdminUserId,
        CreatedAt = now,
    };

    private Task AuditAddAsync(StaffActor actor, Placement placement, Guid? userId, string how, CancellationToken cancellationToken, Guid? inviteId = null, bool waitsForDigiLocker = false)
        => _audit.WriteAsync(
            new AuditEntry(AuditActions.StaffAdd, AuditActorType.Admin, actor.AdminUserId, actor.AppId, userId is null ? "staff_invite" : "user", userId ?? inviteId,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["org"] = placement.Org.Id,
                    ["how"] = how,
                    ["roles"] = placement.Roles,
                    ["starts_on"] = placement.StartsOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["expires_at"] = placement.ExpiresAt,
                    ["before_digilocker"] = placement.BeforeDigiLocker,
                    ["via"] = actor.ViaApi ? "api" : "console",
                    ["invite"] = inviteId,
                    ["waits_for_digilocker"] = waitsForDigiLocker,
                })),
            cancellationToken);

    /// <summary>rc.13: tells a known person that the organisation takes only people verified with DigiLocker, and how to verify.</summary>
    private async Task AskToVerifyAsync(StaffActor actor, Organisation org, Guid userId, CancellationToken cancellationToken)
    {
        SangamUser? person = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        if (person?.Email is not { Length: > 0 } address)
        {
            return;
        }

        string adminName = await NameAsync(actor.AdminUserId, cancellationToken).ConfigureAwait(false);
        string application = await _db.Apps.Where(a => a.Id == actor.AppId).Select(a => a.DisplayName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        EmailMessage message = await _templates.EmailAsync(
            MessageTemplateKinds.StaffVerifyRequired,
            CultureInfo.CurrentUICulture.Name,
            actor.AppId,
            org.Id,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = person.FirstName,
                ["application"] = application,
                ["organisations"] = org.Name,
                ["admin"] = adminName,
                ["link"] = $"{_origin}/identity/required?join=true&returnUrl=%2Faccount",
            },
            address,
            (person.FirstName + " " + person.LastName).Trim(),
            cancellationToken).ConfigureAwait(false);
        await _email.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ waiting additions

    /// <inheritdoc />
    public async Task<IReadOnlyList<StaffWaiting>?> WaitingAsync(StaffActor actor, Guid orgId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        Organisation? org = await _rights.OrganisationAsync(actor.AppId, orgId, cancellationToken).ConfigureAwait(false);
        if (org is null || !await _rights.ManagesAsync(actor.AppId, org, actor.AdminUserId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        DateTimeOffset now = _clock.UtcNow;
        bool verifiedOnly = await VerifiedStaff.OnAtAsync(_db, org.Path, cancellationToken).ConfigureAwait(false);
        var rows = await (
            from p in _db.PendingMemberships.AsNoTracking()
            where p.AppId == actor.AppId && p.OrgId == orgId && p.AppliedAt == null && p.CancelledAt == null
            join i in _db.StaffInvites.AsNoTracking() on p.InviteId equals i.Id into invites
            from i in invites.DefaultIfEmpty()
            join u in _db.Users.AsNoTracking() on p.UserId equals u.Id into users
            from u in users.DefaultIfEmpty()
            where i == null || (i.CancelledAt == null && (i.AcceptedAt != null || i.ExpiresAt > now))
            orderby p.CreatedAt descending
            select new
            {
                p.Id,
                p.InviteId,
                p.Roles,
                p.StartsOn,
                p.UserId,
                InviteName = i == null ? null : i.FirstName + " " + i.LastName,
                UserName = u == null ? null : u.FirstName + " " + u.LastName,
                Accepted = i != null && i.AcceptedAt != null,
                InviteExpires = i == null ? (DateTimeOffset?)null : i.ExpiresAt,
                Before = i != null && i.AddedBeforeDigiLocker,
                Verified = u != null && u.IdentityVerifiedAt != null,
            }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. rows.Select(r => new StaffWaiting(
                r.Id,
                r.UserId is null ? r.InviteId : null,
                (r.UserName ?? r.InviteName ?? string.Empty).Trim(),
                Codes(r.Roles),
                r.StartsOn,
                r.UserId is not null || r.Accepted,
                r.UserId is null ? r.InviteExpires : null,
                r.Before,
                verifiedOnly && r.UserId is not null && !r.Verified)),
        ];
    }

    /// <inheritdoc />
    public async Task<StaffActingAdmin> ActingAdminAsync(StaffActor actor, Guid orgId, Guid personId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        Organisation? org = await _rights.OrganisationAsync(actor.AppId, orgId, cancellationToken).ConfigureAwait(false);

        // rc.13 (for demos): a tester giving themselves a place where the application's administrators allow it.
        if (org is not null && actor.AdminUserId == personId && org.TestersMayJoinThemselves && org.Status == OrganisationStatus.Active)
        {
            return StaffActingAdmin.Themselves;
        }

        if (org is null || !await _rights.ManagesAsync(actor.AppId, org, actor.AdminUserId, cancellationToken).ConfigureAwait(false))
        {
            return StaffActingAdmin.NotAdmin;
        }

        bool newPlace = !await HasLivePlaceAsync(actor.AppId, orgId, personId, cancellationToken).ConfigureAwait(false);
        return newPlace && !(await _rights.MayAddAsync(actor.AppId, actor.AdminUserId, cancellationToken).ConfigureAwait(false)).May
            ? StaffActingAdmin.MustBeVerified
            : StaffActingAdmin.May;
    }

    /// <inheritdoc />
    public async Task<bool> CancelAsync(StaffActor actor, Guid waitingId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        PendingMembership? pending = await _db.PendingMemberships.FirstOrDefaultAsync(p => p.Id == waitingId && p.AppId == actor.AppId && p.AppliedAt == null && p.CancelledAt == null, cancellationToken).ConfigureAwait(false);
        Organisation? org = pending is null ? null : await _rights.OrganisationAsync(actor.AppId, pending.OrgId, cancellationToken).ConfigureAwait(false);
        if (pending is null || org is null || !await _rights.ManagesAsync(actor.AppId, org, actor.AdminUserId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        DateTimeOffset now = _clock.UtcNow;
        pending.CancelledAt = now;
        if (pending.InviteId is Guid inviteId
            && !await _db.PendingMemberships.AnyAsync(p => p.InviteId == inviteId && p.Id != pending.Id && p.AppliedAt == null && p.CancelledAt == null, cancellationToken).ConfigureAwait(false))
        {
            StaffInvite? invite = await _db.StaffInvites.FirstOrDefaultAsync(i => i.Id == inviteId && i.AcceptedAt == null, cancellationToken).ConfigureAwait(false);
            invite?.CancelledAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.StaffCancel, AuditActorType.Admin, actor.AdminUserId, actor.AppId, pending.UserId is null ? "staff_invite" : "user", pending.UserId ?? pending.InviteId,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object?> { ["org"] = pending.OrgId, ["waiting"] = pending.Id })),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    // ------------------------------------------------------------------ the person's side

    /// <inheritdoc />
    public async Task<StaffInviteView?> GetInviteAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        StaffInvite? invite = await FindAsync(token, cancellationToken).ConfigureAwait(false);
        if (invite is null)
        {
            return null;
        }

        var places = await (
            from p in _db.PendingMemberships.AsNoTracking()
            where p.InviteId == invite.Id && p.CancelledAt == null
            join o in _db.Organisations.AsNoTracking() on p.OrgId equals o.Id
            join a in _db.Apps.AsNoTracking() on p.AppId equals a.Id
            orderby a.DisplayName, o.Name
            select new { App = a.DisplayName, Org = o.Name, o.Path }).ToListAsync(cancellationToken).ConfigureAwait(false);
        bool verifiedOnly = false;
        foreach (string path in places.Select(p => p.Path).Distinct(StringComparer.Ordinal))
        {
            verifiedOnly |= await VerifiedStaff.OnAtAsync(_db, path, cancellationToken).ConfigureAwait(false);
        }

        return new StaffInviteView(
            State(invite),
            invite.FirstName,
            LogRedaction.MaskEmail(invite.Email),
            invite.ExistingUserId is not null,
            [.. places.GroupBy(p => p.App).Select(g => new StaffInvitePlace(g.Key, [.. g.Select(p => p.Org).Distinct()]))],
            await NameAsync(invite.CreatedByUserId, cancellationToken).ConfigureAwait(false),
            verifiedOnly);
    }

    /// <inheritdoc />
    public async Task<StaffCodeResult> CheckCodeAsync(string token, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        StaffInvite? invite = await FindAsync(token, cancellationToken).ConfigureAwait(false);
        if (invite is null || State(invite) != StaffInviteState.Open)
        {
            return new StaffCodeResult(StaffCodeStatus.NotOpen, 0);
        }

        string normalised = StaffCodes.Normalise(code);
        if (normalised.Length > 0 && CodeHasher.VerifyHashedPassword(invite, invite.CodeHash, normalised) != PasswordVerificationResult.Failed)
        {
            return new StaffCodeResult(StaffCodeStatus.Right, WrongCodesAllowed - invite.FailedCodes, invite.Id);
        }

        invite.FailedCodes++;
        if (invite.FailedCodes >= WrongCodesAllowed)
        {
            invite.CancelledAt = _clock.UtcNow;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await _audit.WriteAsync(new AuditEntry(AuditActions.StaffInviteLock, AuditActorType.Anonymous, TargetType: "staff_invite", TargetId: invite.Id), cancellationToken).ConfigureAwait(false);
            return new StaffCodeResult(StaffCodeStatus.Locked, 0);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new StaffCodeResult(StaffCodeStatus.Wrong, WrongCodesAllowed - invite.FailedCodes);
    }

    /// <inheritdoc />
    public async Task<StaffDetails?> InvitedDetailsAsync(Guid inviteId, CancellationToken cancellationToken = default)
    {
        StaffInvite? invite = await _db.StaffInvites.AsNoTracking().FirstOrDefaultAsync(i => i.Id == inviteId, cancellationToken).ConfigureAwait(false);
        return invite is null || State(invite) != StaffInviteState.Open
            ? null
            : new StaffDetails(invite.FirstName, invite.LastName, invite.Email, invite.Mobile, invite.DateOfBirth);
    }

    /// <inheritdoc />
    public async Task<StaffJoinResult> JoinAsync(Guid inviteId, Guid userId, CancellationToken cancellationToken = default)
    {
        StaffInvite? invite = await _db.StaffInvites.FirstOrDefaultAsync(i => i.Id == inviteId, cancellationToken).ConfigureAwait(false);
        if (invite is null || State(invite) != StaffInviteState.Open)
        {
            return new StaffJoinResult(StaffJoinStatus.Refused, "This activation link no longer works. Ask your admin to add you again.");
        }

        SangamUser? user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.Status == UserStatus.Active, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return new StaffJoinResult(StaffJoinStatus.Refused, "Sign in to join.");
        }

        if (invite.ExistingUserId is Guid expected)
        {
            if (expected != userId)
            {
                return new StaffJoinResult(StaffJoinStatus.Refused, "This activation is for a different Sangam account. Sign in with the account your admin added.");
            }

            // 4.4: the account's own name and date of birth must match what the admin entered, unless the admin confirmed
            // the difference when they checked.
            HashSet<string> accepted = [.. Codes(invite.AcceptedDifferences)];
            List<string> differ = [.. PersonDifferences(invite, user).Where(f => !accepted.Contains(f))];
            if (differ.Count > 0)
            {
                await StopAsync(invite, user, differ, cancellationToken).ConfigureAwait(false);
                return new StaffJoinResult(StaffJoinStatus.Stopped, "Your account's details are different from what your admin entered, so you have not joined yet. We have told your admin; they will check and add you again.");
            }
        }
        else if (!user.EmailConfirmed || !string.Equals(user.NormalizedEmail, invite.NormalizedEmail, StringComparison.Ordinal))
        {
            return new StaffJoinResult(StaffJoinStatus.Refused, $"This activation was sent to {LogRedaction.MaskEmail(invite.Email)}. Sign in with the account that has that address, or create one with it.");
        }

        List<PendingMembership> waiting = await _db.PendingMemberships
            .Where(p => p.InviteId == invite.Id && p.AppliedAt == null && p.CancelledAt == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // rc.13: the person joins now; a place at an organisation that takes only people verified with DigiLocker waits
        // (the sweep gives it within a minute of their verifying), the others start as usual.
        bool waitsForDigiLocker = false;
        if (user.IdentityVerifiedAt is null)
        {
            List<Guid> orgIds = [.. waiting.Select(p => p.OrgId).Distinct()];
            List<string> paths = await _db.Organisations.AsNoTracking().Where(o => orgIds.Contains(o.Id)).Select(o => o.Path).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (string path in paths)
            {
                waitsForDigiLocker |= await VerifiedStaff.OnAtAsync(_db, path, cancellationToken).ConfigureAwait(false);
            }
        }

        DateTimeOffset now = _clock.UtcNow;
        invite.AcceptedAt = now;
        invite.AcceptedByUserId = userId;
        foreach (PendingMembership p in waiting)
        {
            p.UserId = userId;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.StaffJoin, AuditActorType.User, userId, invite.AppId, "staff_invite", invite.Id,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object?> { ["organisations"] = waiting.Select(p => p.OrgId).Distinct().ToList(), ["existing_account"] = invite.ExistingUserId is not null })),
            cancellationToken).ConfigureAwait(false);

        // Joining is the person's own act, so each application is linked; what starts later waits for the sweep.
        await ApplyDueAsync(waiting, cancellationToken).ConfigureAwait(false);
        return waitsForDigiLocker
            ? new StaffJoinResult(StaffJoinStatus.NeedsVerification, "You have joined. This organisation wants you to verify with DigiLocker before your place there starts; it starts within a minute of verifying.")
            : new StaffJoinResult(StaffJoinStatus.Joined, "You have joined. Sign in to the application with Sangam to start.");
    }

    // ------------------------------------------------------------------ applying (also the sweep)

    /// <summary>
    /// Applies every waiting addition whose person is known and whose start date has come. Run by the background sweep
    /// every minute, and straight after a person joins.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many were applied.</returns>
    public async Task<int> ApplyDueAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        DateOnly today = StaffCodes.IndiaToday(now);

        // rc.13: an addition that ended before it could start is cancelled, waiting for DigiLocker or not.
        await _db.PendingMemberships
            .Where(p => p.AppliedAt == null && p.CancelledAt == null && p.ExpiresAt != null && p.ExpiresAt <= now)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.CancelledAt, now), cancellationToken).ConfigureAwait(false);

        // rc.13: additions that can only wait (the person has not verified with DigiLocker, the organisation or one above it
        // takes only verified people, and they have no live place there yet) are left out in the query itself, so however
        // many there are they never hold up the rest.
        List<PendingMembership> due = await (
            from p in _db.PendingMemberships
            where p.UserId != null && p.AppliedAt == null && p.CancelledAt == null && (p.StartsOn == null || p.StartsOn <= today)
            join o in _db.Organisations on p.OrgId equals o.Id
            where _db.Users.Any(u => u.Id == p.UserId && u.IdentityVerifiedAt != null)
                || !_db.Organisations.Any(v => v.VerifiedStaffOnly && v.DeletedAt == null && o.Path.StartsWith(v.Path))
                || _db.OrgMemberships.Any(m => m.AppId == p.AppId && m.OrgId == p.OrgId && m.UserId == p.UserId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now))
            orderby p.CreatedAt
            select p)
            .Take(500)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return await ApplyDueAsync(due, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ApplyDueAsync(List<PendingMembership> candidates, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        DateOnly today = StaffCodes.IndiaToday(now);
        int applied = 0;
        foreach (PendingMembership p in candidates.Where(p => p.UserId is not null && (p.StartsOn is null || p.StartsOn <= today)))
        {
            DateTimeOffset? expires = p.ExpiresAt is DateTimeOffset e && e <= now ? null : p.ExpiresAt;
            if (p.ExpiresAt is not null && expires is null)
            {
                // It ended before it could start.
                p.CancelledAt = now;
                continue;
            }

            // rc.13: it waits (it is not cancelled) while the organisation takes only verified people and they have not verified.
            string? path = await _db.Organisations.AsNoTracking().Where(o => o.Id == p.OrgId).Select(o => o.Path).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (path is not null && !await HasLivePlaceAsync(p.AppId, p.OrgId, p.UserId!.Value, cancellationToken).ConfigureAwait(false)
                && !await VerifiedStaff.MayJoinAsync(_db, path, p.UserId.Value, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            ManagementResult<MembershipDto> given = await GiveAsync(p.AppId, p.OrgId, p.UserId!.Value, Codes(p.Roles), expires, p.CreatedByUserId, cancellationToken).ConfigureAwait(false);
            if (given.Status == ManagementStatus.Ok)
            {
                p.AppliedAt = now;
                applied++;
            }
            else if (!string.Equals(given.Message, VerifiedStaff.Required, StringComparison.Ordinal))
            {
                // The organisation closed or a role was retired meanwhile: it is not retried for ever. (One that became
                // DigiLocker-only meanwhile keeps waiting instead.)
                p.CancelledAt = now;
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return applied;
    }

    /// <summary>rc.13: whether the person holds a live place (not ended, not expired) at the organisation.</summary>
    private Task<bool> HasLivePlaceAsync(Guid appId, Guid orgId, Guid userId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        return _db.OrgMemberships.AnyAsync(m => m.AppId == appId && m.OrgId == orgId && m.UserId == userId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now), cancellationToken);
    }

    /// <summary>
    /// Gives the roles, adding to any the person already holds there (adding never takes a role away); as the admin who
    /// added them, through the same rules (audit, application told) as every membership.
    /// </summary>
    private async Task<ManagementResult<MembershipDto>> GiveAsync(Guid appId, Guid orgId, Guid userId, IReadOnlyList<string> roles, DateTimeOffset? expiresAt, Guid adminUserId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        List<string> held = await _db.OrgMemberships.AsNoTracking()
            .Where(m => m.AppId == appId && m.OrgId == orgId && m.UserId == userId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now) && m.Role != null)
            .Select(m => m.Role!.Code)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<string> all = [.. held.Concat(roles).Distinct(StringComparer.Ordinal)];
        return await _management.UpsertMembershipAsync(appId, orgId, userId, new MembershipUpsert(ExpiresAt: expiresAt, Roles: all), ManagementActor.AppAdmin(adminUserId), cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The comparison itself; <see langword="null"/> result when the admin is over the day's limit for this person.</summary>
    private async Task<(StaffCheckResult? Result, SangamUser? Found)> RunCheckAsync(StaffActor actor, Organisation org, StaffDetails details, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        string subject = Subject(details);
        int used = await _db.StaffChecks.CountAsync(c => c.AdminUserId == actor.AdminUserId && c.SubjectHash == subject && c.CheckedAt > now.AddDays(-1), cancellationToken).ConfigureAwait(false);
        if (used >= ChecksPerPersonPerDay)
        {
            return (null, null);
        }

        string email = details.Email.Trim().ToUpperInvariant();
        string? mobile = string.IsNullOrWhiteSpace(details.Mobile) ? null : details.Mobile.Trim();
        IQueryable<SangamUser> live = _db.Users.AsNoTracking().Where(u => u.Status == UserStatus.Active || u.Status == UserStatus.Suspended);
        SangamUser? found = await live.FirstOrDefaultAsync(u => u.NormalizedEmail == email, cancellationToken).ConfigureAwait(false)
            ?? (mobile is null ? null : await live.FirstOrDefaultAsync(u => u.PhoneNumber == mobile, cancellationToken).ConfigureAwait(false));

        StaffCheckOutcome outcome;
        List<string> matching = [];
        List<string> differing = [];
        if (found is not null)
        {
            Compare(Same(details.FirstName, found.FirstName), FirstNameField, matching, differing);
            Compare(Same(details.LastName, found.LastName), LastNameField, matching, differing);
            Compare(details.DateOfBirth == found.DateOfBirth, DateOfBirthField, matching, differing);
            Compare(string.Equals(email, found.NormalizedEmail, StringComparison.Ordinal), EmailField, matching, differing);
            if (mobile is not null)
            {
                Compare(string.Equals(mobile, found.PhoneNumber, StringComparison.Ordinal), MobileField, matching, differing);
            }

            outcome = differing.Count == 0 ? StaffCheckOutcome.Match : StaffCheckOutcome.Differs;
        }
        else
        {
            string first = Simple(details.FirstName);
            string last = Simple(details.LastName);
            List<SangamUser> sameBirthday = await live.Where(u => u.DateOfBirth == details.DateOfBirth).Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
            bool similar = sameBirthday.Any(u => Simple(u.FirstName) == first && Simple(u.LastName) == last);
            outcome = similar ? StaffCheckOutcome.Similar : StaffCheckOutcome.None;
            if (similar)
            {
                matching.AddRange([FirstNameField, LastNameField, DateOfBirthField]);
            }
        }

        _db.StaffChecks.Add(new StaffCheck
        {
            Id = Guid.NewGuid(),
            AdminUserId = actor.AdminUserId,
            SubjectHash = subject,
            Outcome = outcome.ToString().ToLowerInvariant(),
            Differences = JsonSerializer.Serialize(differing),
            CheckedAt = now,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.StaffCheck, AuditActorType.Admin, actor.AdminUserId, actor.AppId, "organisation", org.Id,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["outcome"] = outcome.ToString().ToLowerInvariant(),
                    ["matching"] = matching,
                    ["differing"] = differing,
                    ["via"] = actor.ViaApi ? "api" : "console",
                })),
            cancellationToken).ConfigureAwait(false);
        return (new StaffCheckResult(outcome, matching, differing, Message(outcome, matching, differing), ChecksPerPersonPerDay - used - 1), found);
    }

    private async Task<int> ChecksLeftAsync(Guid adminUserId, StaffDetails details, CancellationToken cancellationToken)
    {
        DateTimeOffset since = _clock.UtcNow.AddDays(-1);
        string subject = Subject(details);
        int used = await _db.StaffChecks.CountAsync(c => c.AdminUserId == adminUserId && c.SubjectHash == subject && c.CheckedAt > since, cancellationToken).ConfigureAwait(false);
        return Math.Max(0, ChecksPerPersonPerDay - used);
    }

    /// <summary>The words the admin reads (4.3). Never what Sangam holds, only which details match.</summary>
    internal static string Message(StaffCheckOutcome outcome, IReadOnlyList<string> matching, IReadOnlyList<string> differing) => outcome switch
    {
        StaffCheckOutcome.Match => "This person already has a Sangam account. They will enter your code to join.",
        StaffCheckOutcome.Differs => $"A person with this {Fields(matching)} is already registered in Sangam, but the {Fields(differing)} {(differing.Count == 1 ? "is" : "are")} different. Are you sure you have checked the details against their records?",
        StaffCheckOutcome.Similar => "A person with this name and date of birth may already be registered in Sangam. Ask them for their join code.",
        _ => "No Sangam account matches. A new account will be made when the person activates.",
    };

    private static string Fields(IReadOnlyList<string> fields)
    {
        List<string> words = [.. fields.Select(f => f switch
        {
            FirstNameField => "first name",
            LastNameField => "last name",
            DateOfBirthField => "date of birth",
            EmailField => "email",
            MobileField => "mobile number",
            _ => f,
        })];
        return words.Count switch
        {
            0 => "detail",
            1 => words[0],
            _ => string.Join(", ", words.Take(words.Count - 1)) + " and " + words[^1],
        };
    }

    private static void Compare(bool same, string field, List<string> matching, List<string> differing) => (same ? matching : differing).Add(field);

    private static bool Same(string a, string b) => Simple(a) == Simple(b);

    /// <summary>A name as compared: trimmed, single spaces, case ignored.</summary>
    private static string Simple(string? name) => Spaces().Replace((name ?? string.Empty).Trim(), " ").ToUpperInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static string Subject(StaffDetails details) => StaffCodes.Hash(details.Email.Trim().ToUpperInvariant());

    private static string? Invalid(StaffDetails details)
    {
        if (string.IsNullOrWhiteSpace(details.FirstName) || details.FirstName.Trim().Length > 100 || (details.LastName ?? string.Empty).Trim().Length > 100)
        {
            return "Enter the person's first name (and last name, if they have one), up to 100 characters each.";
        }

        string email = (details.Email ?? string.Empty).Trim();
        if (email.Length == 0 || email.Length > 256 || !EmailFormat.IsValid(email))
        {
            return "Enter a valid email address; the activation link is sent there.";
        }

        if (!string.IsNullOrWhiteSpace(details.Mobile) && !Mobile().IsMatch(details.Mobile.Trim()))
        {
            return "Enter the mobile number with its country code, for example +919876543210.";
        }

        int year = details.DateOfBirth.Year;
        return year < 1900 || details.DateOfBirth > DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-14) ? "Enter the person's date of birth." : null;
    }

    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$")]
    private static partial Regex Mobile();

    /// <summary>Which of the person's own details differ from what the admin entered (name and date of birth only).</summary>
    private static IEnumerable<string> PersonDifferences(StaffInvite invite, SangamUser user)
    {
        if (!Same(invite.FirstName, user.FirstName))
        {
            yield return FirstNameField;
        }

        if (!Same(invite.LastName, user.LastName))
        {
            yield return LastNameField;
        }

        if (invite.DateOfBirth != user.DateOfBirth)
        {
            yield return DateOfBirthField;
        }
    }

    private async Task StopAsync(StaffInvite invite, SangamUser user, List<string> differ, CancellationToken cancellationToken)
    {
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.StaffJoinStop, AuditActorType.User, user.Id, invite.AppId, "staff_invite", invite.Id,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object?> { ["differing"] = differ })),
            cancellationToken).ConfigureAwait(false);
        SangamUser? admin = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == invite.CreatedByUserId, cancellationToken).ConfigureAwait(false);
        if (admin?.Email is null)
        {
            return;
        }

        List<string> organisations = await (
            from p in _db.PendingMemberships.AsNoTracking()
            where p.InviteId == invite.Id && p.CancelledAt == null
            join o in _db.Organisations.AsNoTracking() on p.OrgId equals o.Id
            select o.Name).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        EmailMessage message = await _templates.EmailAsync(
            MessageTemplateKinds.StaffJoinStopped,
            admin.Locale,
            invite.AppId,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = admin.FirstName,
                ["member"] = (invite.FirstName + " " + invite.LastName).Trim(),
                ["organisations"] = string.Join(", ", organisations),
                ["fields"] = Fields(differ),
            },
            admin.Email,
            admin.FirstName,
            cancellationToken).ConfigureAwait(false);
        await _email.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether the roles can be given at this organisation (the same rules the membership itself applies).</summary>
    private async Task<string?> RolesProblemAsync(Guid appId, Organisation org, List<string> roles, CancellationToken cancellationToken)
    {
        if (roles.Count == 0)
        {
            return null;
        }

        if (roles.Count > 10)
        {
            return "At most 10 roles in one organisation.";
        }

        Guid customerId = OrganisationPath.CustomerId(org.Path);
        bool? kept = customerId == org.Id ? org.RolesKeptInSangam : await _db.Organisations.AsNoTracking().Where(o => o.Id == customerId).Select(o => o.RolesKeptInSangam).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (kept == false)
        {
            return "This customer keeps its roles in the application, not in Sangam. Add the person without roles.";
        }

        int live = await _db.Roles.AsNoTracking()
            .Where(r => r.AppId == appId && roles.Contains(r.Code) && r.RetiredAt == null && (r.OrgId == null || r.OrgId == org.Id))
            .Select(r => r.Code).Distinct().CountAsync(cancellationToken).ConfigureAwait(false);
        return live == roles.Count ? null : "One of those roles does not exist in this application.";
    }

    private StaffInviteState State(StaffInvite invite)
    {
        if (invite.AcceptedAt is not null)
        {
            return StaffInviteState.Accepted;
        }

        if (invite.CancelledAt is not null)
        {
            return StaffInviteState.Cancelled;
        }

        return invite.ExpiresAt <= _clock.UtcNow ? StaffInviteState.Expired : StaffInviteState.Open;
    }

    private Task<StaffInvite?> FindAsync(string token, CancellationToken cancellationToken)
    {
        string hash = StaffCodes.Hash(token);
        return _db.StaffInvites.FirstOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);
    }

    private async Task<string> NameAsync(Guid userId, CancellationToken cancellationToken)
        => (await _db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.FirstName + " " + u.LastName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty).Trim();

    private static List<string> Codes(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Where and how a person is being added.</summary>
    private sealed record Placement(Organisation Org, List<string> Roles, DateOnly? StartsOn, DateTimeOffset? ExpiresAt, bool BeforeDigiLocker);
}
