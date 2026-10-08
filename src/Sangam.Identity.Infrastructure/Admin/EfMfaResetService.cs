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
/// and is applied only after <c>Sangam:Recovery:CoolingOffHours</c> (24) or, for privileged accounts,
/// <c>Sangam:Recovery:PrivilegedCoolingOffHours</c> (72, never below 24), and only if nobody cancelled it.
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
    public EfMfaResetService(SangamDbContext db, UserManager<SangamUser> users, IPortalService portal, IEmailSender email, IMessageTemplates templates, SmsNoticeSender sms, IAuditWriter audit, IClock clock, IConfiguration configuration)
    {
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

    /// <summary>The cooling-off period for an account.</summary>
    /// <param name="privileged">Whether the account is privileged.</param>
    public TimeSpan CoolingOff(bool privileged)
        => privileged
            ? TimeSpan.FromHours(Math.Max(24, _configuration.GetValue("Sangam:Recovery:PrivilegedCoolingOffHours", 72)))
            : TimeSpan.FromHours(Math.Max(1, _configuration.GetValue("Sangam:Recovery:CoolingOffHours", 24)));

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
            .Where(r => r.CancelledAt == null && r.AppliedAt == null && r.EffectiveAt <= now)
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

    /// <summary>Creates a request, audits it and alerts the owner on every channel.</summary>
    internal async Task<MfaResetRequest> CreateAsync(SangamUser user, Guid operatorUserId, string method, string reference, bool privileged, string? ipAddress, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        TimeSpan period = CoolingOff(privileged);
        MfaResetRequest request = new()
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RequestedByUserId = operatorUserId,
            VerificationMethod = method,
            Reference = reference.Trim(),
            Privileged = privileged,
            RequestedAt = now,
            EffectiveAt = now + period,
            CancelTokenHash = Hash(token),
        };
        _db.MfaResetRequests.Add(request);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.Entry(request).State = EntityState.Detached;

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AdminUserMfaResetRequest, AuditActorType.Admin, operatorUserId, TargetType: "user", TargetId: user.Id,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["request_id"] = request.Id,
                    ["verified_by"] = method,
                    ["reference"] = request.Reference,
                    ["privileged"] = privileged,
                    ["effective_at"] = request.EffectiveAt.ToString("O", CultureInfo.InvariantCulture),
                }),
                IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);

        // Every channel: e-mail with the one-click cancel link, SMS when it is on, and the notice at next sign-in.
        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["name"] = user.DisplayName,
            ["hours"] = ((int)period.TotalHours).ToString(CultureInfo.InvariantCulture),
            ["effective"] = Ist(request.EffectiveAt),
            ["link"] = CancelLink(token),
        };
        if (!string.IsNullOrEmpty(user.Email))
        {
            await _email.SendAsync(await _templates.EmailAsync(MessageTemplateKinds.TwoStepResetRequested, user.Locale, null, null, values, user.Email, user.DisplayName, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        await _sms.TrySendResetNoticeAsync(user, cancellationToken).ConfigureAwait(false);
        return request;
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
                s => s.SetProperty(r => r.AppliedAt, now).SetProperty(r => r.UrgentByUserId, urgentBy).SetProperty(r => r.UrgentReason, reason),
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
        if (!string.IsNullOrEmpty(user.Email))
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal) { ["name"] = user.DisplayName };
            await _email.SendAsync(await _templates.EmailAsync(MessageTemplateKinds.TwoStepResetNotice, user.Locale, null, null, values, user.Email, user.DisplayName, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

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
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CancelledAt, now).SetProperty(r => r.CancelledBy, by), cancellationToken)
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

    private static PendingTwoStepReset View(MfaResetRequest r) => new(r.Id, r.RequestedAt, r.EffectiveAt, r.Privileged, r.VerificationMethod);

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Ist(DateTimeOffset at)
        => at.ToOffset(TimeSpan.FromHours(5.5)).ToString("d MMM yyyy, HH:mm 'IST'", CultureInfo.InvariantCulture);
}
