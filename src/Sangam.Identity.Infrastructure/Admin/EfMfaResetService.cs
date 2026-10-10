using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Sms;

namespace Sangam.Identity.Infrastructure.Admin;

/// <summary>
/// Support resets of two-step sign-in with a cooling-off period (D-K). A request alerts the owner on every channel —
/// e-mail with a one-click "this wasn't me, cancel" link, SMS when SMS is on, and a notice at their next sign-in —
/// and is applied only after <c>Sangam:Recovery:CoolingOffHours</c> (24, never below 24) or, for privileged accounts,
/// <c>Sangam:Recovery:PrivilegedCoolingOffHours</c> (72, never below the ordinary period), and only if nobody cancelled it.
/// </summary>
public sealed class EfMfaResetService : IMfaResetService
{
    private readonly SangamDbContext _db;
    private readonly UserManager<SangamUser> _users;
    private readonly IPortalService _portal;
    private readonly IEmailSender _email;
    private readonly IMessageTemplates _templates;
    private readonly SmsNoticeSender _sms;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly IConfiguration _configuration;
    private readonly Verification.EfIdentityVerificationService _verification;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="users">Identity user manager.</param>
    /// <param name="portal">Portal service, to end every session when a reset is applied.</param>
    /// <param name="email">E-mail.</param>
    /// <param name="templates">Message templates.</param>
    /// <param name="sms">SMS notices.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="configuration">Configuration (<c>Sangam:Recovery</c>, and the identity server's address for the link).</param>
    /// <param name="verification">Identity verification, to store a recovered person's DigiLocker identity (rc.6).</param>
    public EfMfaResetService(SangamDbContext db, UserManager<SangamUser> users, IPortalService portal, IEmailSender email, IMessageTemplates templates, SmsNoticeSender sms, IAuditWriter audit, IClock clock, IConfiguration configuration, Verification.EfIdentityVerificationService verification)
    {
        _verification = verification ?? throw new ArgumentNullException(nameof(verification));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _portal = portal ?? throw new ArgumentNullException(nameof(portal));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _sms = sms ?? throw new ArgumentNullException(nameof(sms));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>The shortest cooling-off D-K allows, for any account (V-13): a setting below it is raised to it.</summary>
    public const int MinimumCoolingOffHours = 24;

    /// <summary>
    /// The cooling-off period for an account: never below <see cref="MinimumCoolingOffHours"/>, and a privileged
    /// account's never shorter than an ordinary one's.
    /// </summary>
    /// <param name="privileged">Whether the account is privileged.</param>
    public TimeSpan CoolingOff(bool privileged) => CoolingOff(_configuration, privileged);

    /// <summary>The cooling-off period the settings give an account (see the instance method).</summary>
    /// <param name="configuration">Configuration (<c>Sangam:Recovery</c>).</param>
    /// <param name="privileged">Whether the account is privileged.</param>
    public static TimeSpan CoolingOff(IConfiguration configuration, bool privileged)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        int ordinary = Math.Max(MinimumCoolingOffHours, configuration.GetValue("Sangam:Recovery:CoolingOffHours", 24));
        return TimeSpan.FromHours(privileged
            ? Math.Max(ordinary, configuration.GetValue("Sangam:Recovery:PrivilegedCoolingOffHours", 72))
            : ordinary);
    }

    /// <inheritdoc />
    public async Task<PendingTwoStepReset?> PendingAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        MfaResetRequest? request = await PendingRowAsync(userId, cancellationToken).ConfigureAwait(false);
        return request is null ? null : View(request);
    }

    /// <inheritdoc />
    public async Task<PendingTwoStepReset?> FindByTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        string hash = Hash(token);
        MfaResetRequest? request = await _db.MfaResetRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.CancelTokenHash == hash && r.CancelledAt == null && r.AppliedAt == null, cancellationToken).ConfigureAwait(false);
        return request is null ? null : View(request);
    }

    /// <inheritdoc />
    public async Task<bool> CancelByTokenAsync(string token, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        string hash = Hash(token);
        MfaResetRequest? request = await _db.MfaResetRequests.AsNoTracking().FirstOrDefaultAsync(r => r.CancelTokenHash == hash, cancellationToken).ConfigureAwait(false);
        return request is not null && await CancelAsync(request, "owner", ipAddress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> CancelByOwnerAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        MfaResetRequest? request = await PendingRowAsync(userId, cancellationToken).ConfigureAwait(false);
        return request is not null && await CancelAsync(request, "owner", ipAddress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> NoticeDueAsync(Guid userId, CancellationToken cancellationToken = default)
        => _db.MfaResetRequests.AnyAsync(r => r.UserId == userId && r.CancelledAt == null && r.AppliedAt == null && r.NoticeSeenAt == null, cancellationToken);

    /// <inheritdoc />
    public async Task AcknowledgeNoticeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        await _db.MfaResetRequests
            .Where(r => r.UserId == userId && r.CancelledAt == null && r.AppliedAt == null && r.NoticeSeenAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.NoticeSeenAt, now), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> ApplyDueAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        List<MfaResetRequest> due = await _db.MfaResetRequests.AsNoTracking()
            .Where(r => r.CancelledAt == null && r.AppliedAt == null && r.EffectiveAt <= now && (r.ReviewStatus == null || r.ReviewStatus == MfaResetReview.Approved))
            .OrderBy(r => r.EffectiveAt)
            .Take(100)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        int applied = 0;
        foreach (MfaResetRequest request in due)
        {
            if (await ApplyAsync(request, urgentBy: null, reason: null, ipAddress: null, cancellationToken).ConfigureAwait(false))
            {
                applied++;
            }
        }

        return applied;
    }

    /// <summary>The pending request on an account, tracked, if any.</summary>
    internal Task<MfaResetRequest?> PendingRowAsync(Guid userId, CancellationToken cancellationToken)
        => _db.MfaResetRequests.AsNoTracking()
            .Where(r => r.UserId == userId && r.CancelledAt == null && r.AppliedAt == null)
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Whether an account is privileged for a reset (D-K): an operator, an application administrator or an organisation
    /// administrator. Privileged accounts wait 72 hours instead of 24, and only an Owner reviews their recovery.
    /// </summary>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task<bool> IsPrivilegedAsync(Guid userId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        return await _db.PlatformOperators.AnyAsync(o => o.UserId == userId && o.RevokedAt == null, cancellationToken).ConfigureAwait(false)
            || await _db.AppAdmins.AnyAsync(a => a.UserId == userId && a.RevokedAt == null, cancellationToken).ConfigureAwait(false)
            || await _db.OrgMemberships.AnyAsync(m => m.UserId == userId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now) && m.Role!.Code == "org_admin", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// rc.6 (SGM-914): creates the request for a person's own recovery with DigiLocker, audits it, and alerts the owner
    /// on every channel with the cancel link. <paramref name="match"/> says how the record matched (<c>strong</c>,
    /// <c>rule</c>, or <c>review</c>); a <c>review</c> request waits for an operator before its cooling-off starts.
    /// </summary>
    internal async Task<MfaResetRequest> CreateRecoveryAsync(SangamUser user, bool privileged, string match, string record, string? ipAddress, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        TimeSpan period = CoolingOff(privileged);
        bool review = match == "review";
        MfaResetRequest request = new()
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RequestedByUserId = user.Id,
            VerificationMethod = "digilocker",
            Reference = match,
            Privileged = privileged,
            RequestedAt = now,
            EffectiveAt = now + period,
            CancelTokenHash = Hash(token),
            ReviewStatus = review ? MfaResetReview.Waiting : null,
            Record = record,
        };
        _db.MfaResetRequests.Add(request);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.Entry(request).State = EntityState.Detached;

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.UserMfaRecoveryRequest, AuditActorType.User, user.Id, TargetType: "user", TargetId: user.Id,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["request_id"] = request.Id,
                    ["match"] = match,
                    ["privileged"] = privileged,
                    ["effective_at"] = review ? "after_review" : request.EffectiveAt.ToString("O", CultureInfo.InvariantCulture),
                }),
                IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);

        if (review)
        {
            await SendAsync(user, MessageTemplateKinds.RecoveryReviewWaiting, new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = user.DisplayName, ["link"] = CancelLink(token) }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SendRequestedAsync(user, request.RequestedAt, request.EffectiveAt, token, cancellationToken).ConfigureAwait(false);
        }

        await _sms.TrySendResetNoticeAsync(user, cancellationToken).ConfigureAwait(false);
        return request;
    }

    /// <summary>
    /// rc.6: an operator approves a recovery after review. Its cooling-off period starts now, with a fresh cancel link
    /// sent to the owner (the first one stops working). Returns false when it was no longer waiting.
    /// </summary>
    internal async Task<bool> ApproveAsync(MfaResetRequest request, Guid operatorUserId, string reason, string? ipAddress, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        DateTimeOffset effective = now + CoolingOff(request.Privileged);
        int approved = await _db.MfaResetRequests
            .Where(r => r.Id == request.Id && r.ReviewStatus == MfaResetReview.Waiting && r.CancelledAt == null && r.AppliedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.ReviewStatus, MfaResetReview.Approved)
                    .SetProperty(r => r.ReviewedByUserId, operatorUserId)
                    .SetProperty(r => r.ReviewedAt, now)
                    .SetProperty(r => r.ReviewReason, reason)
                    .SetProperty(r => r.EffectiveAt, effective)
                    .SetProperty(r => r.CancelTokenHash, Hash(token)),
                cancellationToken)
            .ConfigureAwait(false);
        if (approved == 0)
        {
            return false;
        }

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AdminUserMfaRecoveryApprove, AuditActorType.Admin, operatorUserId, TargetType: "user", TargetId: request.UserId,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object> { ["request_id"] = request.Id, ["reason"] = reason, ["effective_at"] = effective.ToString("O", CultureInfo.InvariantCulture) }),
                IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        SangamUser? user = await _users.FindByIdAsync(request.UserId.ToString("D")).ConfigureAwait(false);
        if (user is not null)
        {
            await SendRequestedAsync(user, now, effective, token, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>rc.6: an operator refuses a recovery after review; the record is cleared and the person told. False when it was no longer waiting.</summary>
    internal async Task<bool> RefuseAsync(MfaResetRequest request, Guid operatorUserId, string reason, string? ipAddress, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        int refused = await _db.MfaResetRequests
            .Where(r => r.Id == request.Id && r.ReviewStatus == MfaResetReview.Waiting && r.CancelledAt == null && r.AppliedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.ReviewStatus, MfaResetReview.Refused)
                    .SetProperty(r => r.ReviewedByUserId, operatorUserId)
                    .SetProperty(r => r.ReviewedAt, now)
                    .SetProperty(r => r.ReviewReason, reason)
                    .SetProperty(r => r.CancelledAt, now)
                    .SetProperty(r => r.CancelledBy, "review")
                    .SetProperty(r => r.Record, (string?)null),
                cancellationToken)
            .ConfigureAwait(false);
        if (refused == 0)
        {
            return false;
        }

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AdminUserMfaRecoveryRefuse, AuditActorType.Admin, operatorUserId, TargetType: "user", TargetId: request.UserId,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object> { ["request_id"] = request.Id, ["reason"] = reason }),
                IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        SangamUser? user = await _users.FindByIdAsync(request.UserId.ToString("D")).ConfigureAwait(false);
        if (user is not null)
        {
            await SendAsync(user, MessageTemplateKinds.RecoveryRefused, new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = user.DisplayName }, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task SendRequestedAsync(SangamUser user, DateTimeOffset from, DateTimeOffset effective, string token, CancellationToken cancellationToken)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["name"] = user.DisplayName,
            ["hours"] = ((int)Math.Round((effective - from).TotalHours)).ToString(CultureInfo.InvariantCulture),
            ["effective"] = Ist(effective),
            ["link"] = CancelLink(token),
        };
        await SendAsync(user, MessageTemplateKinds.TwoStepResetRequested, values, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAsync(SangamUser user, string kind, Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(user.Email))
        {
            await _email.SendAsync(await _templates.EmailAsync(kind, user.Locale, null, null, values, user.Email, user.DisplayName, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies a request once — whichever host or caller claims it first: removes the authenticator, ends every
    /// session, tells the owner, and audits. Returns false when it was already applied or cancelled.
    /// </summary>
    internal async Task<bool> ApplyAsync(MfaResetRequest request, Guid? urgentBy, string? reason, string? ipAddress, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        int claimed = await _db.MfaResetRequests
            .Where(r => r.Id == request.Id && r.CancelledAt == null && r.AppliedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.AppliedAt, now).SetProperty(r => r.UrgentByUserId, urgentBy).SetProperty(r => r.UrgentReason, reason).SetProperty(r => r.Record, (string?)null),
                cancellationToken)
            .ConfigureAwait(false);
        if (claimed == 0)
        {
            return false;
        }

        SangamUser? user = await _users.FindByIdAsync(request.UserId.ToString("D")).ConfigureAwait(false);
        if (user is null)
        {
            return false;
        }

        await _users.SetTwoFactorEnabledAsync(user, false).ConfigureAwait(false);
        await _users.ResetAuthenticatorKeyAsync(user).ConfigureAwait(false);
        await _users.UpdateSecurityStampAsync(user).ConfigureAwait(false);
        await _portal.RevokeAllSessionsAsync(user.Id, ipAddress, cancellationToken).ConfigureAwait(false);

        // rc.6 (SGM-914 section 5): a person recovered with DigiLocker who was not verified is verified now, so their next
        // recovery uses the strong match. Their name, date of birth and gender take the record's values, as they agreed.
        if (RecoveryRecord.Parse(request.Record) is RecoveryRecord record && user.IdentityVerifiedAt is null)
        {
            await _verification.ApplyHashedAsync(user.Id, new Application.Verification.VerifiedIdentity(record.Method, record.SubjectHash, record.Name, record.DateOfBirth, record.Gender), ipAddress, cancellationToken).ConfigureAwait(false);
        }

        await SendAsync(user, MessageTemplateKinds.TwoStepResetNotice, new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = user.DisplayName }, cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AdminUserMfaReset, urgentBy is null ? AuditActorType.System : AuditActorType.Admin, urgentBy ?? request.RequestedByUserId, TargetType: "user", TargetId: user.Id,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["request_id"] = request.Id,
                    ["method"] = request.VerificationMethod,
                    ["reference"] = request.Reference,
                    ["applied"] = urgentBy is null ? "after_cooling_off" : "urgent_override",
                }),
                IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Cancels a pending request; returns false when it was no longer pending.</summary>
    internal async Task<bool> CancelAsync(MfaResetRequest request, string by, string? ipAddress, CancellationToken cancellationToken, Guid? operatorUserId = null)
    {
        DateTimeOffset now = _clock.UtcNow;
        int cancelled = await _db.MfaResetRequests
            .Where(r => r.Id == request.Id && r.CancelledAt == null && r.AppliedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CancelledAt, now).SetProperty(r => r.CancelledBy, by).SetProperty(r => r.Record, (string?)null), cancellationToken)
            .ConfigureAwait(false);
        if (cancelled == 0)
        {
            return false;
        }

        await _audit.WriteAsync(
            operatorUserId is Guid op
                ? new AuditEntry(AuditActions.AdminUserMfaResetWithdraw, AuditActorType.Admin, op, TargetType: "user", TargetId: request.UserId,
                    Metadata: JsonSerializer.Serialize(new Dictionary<string, object> { ["request_id"] = request.Id }), IpAddress: ipAddress)
                : new AuditEntry(AuditActions.UserMfaResetCancel, AuditActorType.User, request.UserId, TargetType: "user", TargetId: request.UserId,
                    Metadata: JsonSerializer.Serialize(new Dictionary<string, object> { ["request_id"] = request.Id }), IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private string CancelLink(string token)
    {
        string origin = (_configuration["Sangam:Issuer"] ?? _configuration["Sangam:Authority"] ?? "https://id.sangamid.in").TrimEnd('/');
        return origin + "/account/reset/cancel/" + token;
    }

    private static PendingTwoStepReset View(MfaResetRequest r) => new(r.Id, r.RequestedAt, r.EffectiveAt, r.Privileged, r.VerificationMethod, r.ReviewStatus == MfaResetReview.Waiting);

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Ist(DateTimeOffset at)
        => at.ToOffset(Sangam.Shared.IndiaTime.Offset).ToString("d MMM yyyy, HH:mm 'IST'", CultureInfo.InvariantCulture);
}
