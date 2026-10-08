using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Accounts;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Infrastructure.Portal;

/// <summary>Change of e-mail address (OI-022). See <see cref="IEmailChangeService"/>.</summary>
public sealed class EfEmailChangeService : IEmailChangeService
{
    private static readonly EmailAddressAttribute EmailFormat = new();
    private readonly SangamDbContext _db;
    private readonly UserManager<SangamUser> _users;
    private readonly OneTimeCodeService _codes;
    private readonly IEmailSender _email;
    private readonly IMessageTemplates _templates;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly OtpOptions _otp;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="users">User manager (password checks and lockout).</param>
    /// <param name="codes">One-time codes.</param>
    /// <param name="email">E-mail sender.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="otp">Code settings.</param>
    /// <param name="templates">Message templates (PR-19).</param>
    public EfEmailChangeService(SangamDbContext db, UserManager<SangamUser> users, OneTimeCodeService codes, IEmailSender email, IAuditWriter audit, IClock clock, OtpOptions otp, IMessageTemplates templates)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _codes = codes ?? throw new ArgumentNullException(nameof(codes));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _otp = otp ?? throw new ArgumentNullException(nameof(otp));
    }

    /// <inheritdoc />
    public async Task<PendingEmailChange?> GetPendingAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        EmailChangeRequest? pending = await PendingAsync(userId, cancellationToken).ConfigureAwait(false);
        return pending is null ? null : new PendingEmailChange(pending.NewEmail, pending.CreatedAt);
    }

    /// <inheritdoc />
    public async Task<EmailChangeResult> RequestAsync(Guid userId, string newEmail, string currentPassword, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newEmail);
        ArgumentNullException.ThrowIfNull(currentPassword);
        SangamUser user = await _users.FindByIdAsync(userId.ToString("D")).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Unknown user.");
        string email = newEmail.Trim();
        if (email.Length == 0 || email.Length > 256 || !EmailFormat.IsValid(email))
        {
            return new EmailChangeResult(EmailChangeStatus.InvalidEmail, "Enter a valid email address.");
        }

        string normalized = _users.NormalizeEmail(email);
        if (string.Equals(normalized, user.NormalizedEmail, StringComparison.Ordinal))
        {
            return new EmailChangeResult(EmailChangeStatus.SameAsCurrent, "That is already your email address.");
        }

        if (await _users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            return new EmailChangeResult(EmailChangeStatus.LockedOut, "Too many wrong passwords. Try again later.");
        }

        if (!await _users.CheckPasswordAsync(user, currentPassword).ConfigureAwait(false))
        {
            await _users.AccessFailedAsync(user).ConfigureAwait(false);
            return new EmailChangeResult(EmailChangeStatus.WrongPassword, "Your current password is not right.");
        }

        if (await _users.FindByEmailAsync(email).ConfigureAwait(false) is not null)
        {
            return new EmailChangeResult(EmailChangeStatus.Unavailable, "That address cannot be used for your account.");
        }

        (OtpIssueResult issued, string? code) = await _codes.IssueAsync(user.Id, OneTimeCodePurpose.EmailChange, cancellationToken).ConfigureAwait(false);
        if (issued.Status != OtpIssueStatus.Sent || code is null)
        {
            return new EmailChangeResult(EmailChangeStatus.RateLimited, "Please wait a moment before asking for another code.");
        }

        DateTimeOffset now = _clock.UtcNow;
        await CancelPendingAsync(user.Id, now, cancellationToken).ConfigureAwait(false);
        _db.EmailChangeRequests.Add(new EmailChangeRequest { Id = Guid.NewGuid(), UserId = user.Id, NewEmail = email, NormalizedNewEmail = normalized, CreatedAt = now });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["name"] = user.FirstName,
            ["code"] = code,
            ["minutes"] = ((int)Math.Round(_otp.Lifetime.TotalMinutes)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        await _email.SendAsync(await _templates.EmailAsync(MessageTemplateKinds.EmailChangeCode, user.Locale, null, null, values, email, user.FirstName, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.UserEmailChangeRequest, AuditActorType.User, user.Id, TargetType: "user", TargetId: user.Id, Metadata: Masked("new", email)), cancellationToken).ConfigureAwait(false);
        return new EmailChangeResult(EmailChangeStatus.CodeSent, $"We sent a code to {email}. Enter it here to finish.");
    }

    /// <inheritdoc />
    public async Task<EmailChangeResult> ConfirmAsync(Guid userId, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        EmailChangeRequest? pending = await PendingAsync(userId, cancellationToken).ConfigureAwait(false);
        if (pending is null)
        {
            return new EmailChangeResult(EmailChangeStatus.NothingPending, "There is no change waiting to be confirmed.");
        }

        if (await _codes.VerifyAsync(userId, OneTimeCodePurpose.EmailChange, code, cancellationToken).ConfigureAwait(false) != OtpVerifyStatus.Valid)
        {
            return new EmailChangeResult(EmailChangeStatus.WrongCode, "That code is not right or has expired.");
        }

        SangamUser user = await _users.FindByIdAsync(userId.ToString("D")).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Unknown user.");
        if (await _users.FindByEmailAsync(pending.NewEmail).ConfigureAwait(false) is not null)
        {
            return new EmailChangeResult(EmailChangeStatus.Unavailable, "That address cannot be used for your account.");
        }

        string oldEmail = user.Email ?? string.Empty;
        DateTimeOffset now = _clock.UtcNow;
        user.Email = pending.NewEmail;
        user.NormalizedEmail = pending.NormalizedNewEmail;
        user.UserName = pending.NewEmail;
        user.NormalizedUserName = _users.NormalizeName(pending.NewEmail);
        user.EmailConfirmed = true;
        user.UpdatedAt = now;
        pending.CompletedAt = now;
        IdentityResult updated = await _users.UpdateAsync(user).ConfigureAwait(false);
        if (!updated.Succeeded)
        {
            return new EmailChangeResult(EmailChangeStatus.Unavailable, "That address cannot be used for your account.");
        }

        await _users.UpdateSecurityStampAsync(user).ConfigureAwait(false);
        await Provisioning.AppEventLog.AddAsync(_db, AppEventTypes.UserUpdated, null, user.Id, null, null, now, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (oldEmail.Length > 0)
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal) { ["name"] = user.FirstName, ["new_email"] = LogRedaction.MaskEmail(pending.NewEmail) };
            await _email.SendAsync(await _templates.EmailAsync(MessageTemplateKinds.EmailChangedNotice, user.Locale, null, null, values, oldEmail, user.FirstName, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        await _audit.WriteAsync(new AuditEntry(AuditActions.UserEmailChange, AuditActorType.User, user.Id, TargetType: "user", TargetId: user.Id, Metadata: JsonSerializer.Serialize(new Dictionary<string, string> { ["old"] = LogRedaction.MaskEmail(oldEmail), ["new"] = LogRedaction.MaskEmail(pending.NewEmail) })), cancellationToken).ConfigureAwait(false);
        return new EmailChangeResult(EmailChangeStatus.Changed, $"Your email address is now {pending.NewEmail}. Use it the next time you sign in.");
    }

    /// <inheritdoc />
    public async Task CancelAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await CancelPendingAsync(userId, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Masked(string key, string email)
    {
        return JsonSerializer.Serialize(new Dictionary<string, string> { [key] = LogRedaction.MaskEmail(email) });
    }

    private Task<EmailChangeRequest?> PendingAsync(Guid userId, CancellationToken cancellationToken)
    {
        return _db.EmailChangeRequests
            .Where(r => r.UserId == userId && r.CompletedAt == null && r.CancelledAt == null)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task CancelPendingAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<EmailChangeRequest> open = await _db.EmailChangeRequests
            .Where(r => r.UserId == userId && r.CompletedAt == null && r.CancelledAt == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (EmailChangeRequest request in open)
        {
            request.CancelledAt = now;
        }
    }
}
