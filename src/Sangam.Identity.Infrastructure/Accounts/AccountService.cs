using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Monitoring;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Sms;

namespace Sangam.Identity.Infrastructure.Accounts;

/// <summary>
/// <see cref="IAccountService"/> on ASP.NET Core Identity. The only place <see cref="SangamUser"/>
/// is read for account flows; everything leaves as <see cref="UserSummary"/> or a result.
/// </summary>
public sealed class AccountService : IAccountService
{
    /// <summary>rc.2: how many "your sign-in uses a password" reminders one account can receive in 24 hours.</summary>
    internal const int PasswordAccountRemindersPerDay = 3;

    private const string PasswordPolicyMessage = "Use at least 12 characters, and not a common password. A few words together make a good one.";

    private readonly UserManager<SangamUser> _users;
    private readonly SangamDbContext _db;
    private readonly OneTimeCodeService _codes;
    private readonly IEmailSender _email;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly OtpOptions _otp;
    private readonly RegistrationOptions _registration;
    private readonly IMessageTemplates _templates;
    private readonly CurrentApplication _current;
    private readonly SmsNoticeSender _smsNotices;
    private readonly UnknownAddressLockout _unknownLockout;
    private readonly SecurityNotices? _notices;
    private static string? _dummyHash;
    private static readonly Dictionary<string, string> NoValues = new(StringComparer.Ordinal);

    /// <summary>Initialises the service.</summary>
    /// <param name="users">Identity user manager.</param>
    /// <param name="db">Database.</param>
    /// <param name="codes">One-time code service.</param>
    /// <param name="email">Email sender.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="otp">Code policy.</param>
    /// <param name="registration">Registration settings (V-09).</param>
    /// <param name="templates">Message templates (PR-19).</param>
    /// <param name="current">The application this request acts for, if any (PR-19).</param>
    /// <param name="smsNotices">Texts notices that carry no code (D-L).</param>
    /// <param name="unknownLockout">Lockout for addresses with no account (D-L).</param>
    /// <param name="notices">Security notices to the person (R7); none when null.</param>
    public AccountService(
        UserManager<SangamUser> users,
        SangamDbContext db,
        OneTimeCodeService codes,
        IEmailSender email,
        IAuditWriter audit,
        IClock clock,
        OtpOptions otp,
        RegistrationOptions registration,
        IMessageTemplates templates,
        CurrentApplication current,
        SmsNoticeSender smsNotices,
        UnknownAddressLockout unknownLockout,
        SecurityNotices? notices = null)
    {
        _notices = notices;
        _unknownLockout = unknownLockout ?? throw new ArgumentNullException(nameof(unknownLockout));
        _smsNotices = smsNotices ?? throw new ArgumentNullException(nameof(smsNotices));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _codes = codes ?? throw new ArgumentNullException(nameof(codes));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _otp = otp ?? throw new ArgumentNullException(nameof(otp));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
    }

    /// <inheritdoc />
    public async Task<RegistrationOutcome> RegisterAsync(RegisterUserCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        List<AccountError> errors = [];
        string email = command.Email.Trim();
        string mobile = NormaliseMobile(command.Mobile);

        if (string.IsNullOrWhiteSpace(command.FirstName))
        {
            errors.Add(new AccountError("FirstName", "Enter your first name."));
        }

        if (string.IsNullOrWhiteSpace(command.LastName))
        {
            errors.Add(new AccountError("LastName", "Enter your last name."));
        }

        if (!LooksLikeEmail(email))
        {
            errors.Add(new AccountError("Email", "Enter a valid email address."));
        }

        if (!LooksLikeE164(mobile))
        {
            errors.Add(new AccountError("Mobile", "Enter your mobile number with country code, for example +91 98765 43210."));
        }

        DateOnly today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
        if (!AgePolicy.IsPlausible(command.DateOfBirth, today))
        {
            errors.Add(new AccountError("DateOfBirth", "Enter a valid date of birth."));
        }
        else if (!AgePolicy.MayRegister(command.DateOfBirth, today))
        {
            errors.Add(new AccountError("DateOfBirth", $"You need to be {AgePolicy.MinimumRegistrationAge} or older to open a Sangam account. If an adult manages an account for you, write to help@sangamid.in."));
        }

        PasswordStrengthResult strength = PasswordStrength.Evaluate(command.Password);
        if (!strength.MeetsPolicy)
        {
            errors.Add(new AccountError("Password", PolicyMessage(strength)));
        }

        if (string.IsNullOrWhiteSpace(command.TermsVersion))
        {
            errors.Add(new AccountError("AcceptTerms", "You need to accept the terms to create an account."));
        }

        if (errors.Count > 0)
        {
            return new RegistrationOutcome(AccountResult.Failed([.. errors]), null);
        }

        // Existing email or mobile: same generic message for both, so the form cannot be used to enumerate accounts.
        SangamUser? existing = await _users.FindByEmailAsync(email).ConfigureAwait(false);

        SangamUser? mobileOwner = existing is not null && existing.PhoneNumber == mobile
            ? existing
            : await _db.Users.FirstOrDefaultAsync(u => u.PhoneNumber == mobile && u.Status != UserStatus.DeletedHard, cancellationToken).ConfigureAwait(false);
        if ((existing is not null || mobileOwner is not null) && _registration.ConcealExistingAccounts)
        {
            // D-L: reveal nothing. The page goes on exactly as for a real registration, nothing is created, and
            // the real owner is told: by e-mail for an address, by SMS (or e-mail when it cannot be texted) for a
            // mobile. The password is hashed all the same so the response takes as long as a real registration.
            _ = _users.PasswordHasher.HashPassword(new SangamUser(), command.Password);
            if (existing is not null && existing.Status != UserStatus.DeletedHard)
            {
                await NotifyAttemptAsync(existing, byMobile: false, command, cancellationToken).ConfigureAwait(false);
            }

            if (mobileOwner is not null && mobileOwner.Id != existing?.Id)
            {
                await NotifyAttemptAsync(mobileOwner, byMobile: true, command, cancellationToken).ConfigureAwait(false);
            }

            return new RegistrationOutcome(AccountResult.Success, null, Concealed: true);
        }

        if (existing is not null || mobileOwner is not null)
        {
            return new RegistrationOutcome(
                AccountResult.Failed(new AccountError(null, "An account already exists with this email or mobile. Sign in, or use 'Forgot password?' to recover it.")),
                null);
        }

        DateTimeOffset now = _clock.UtcNow;
        SangamUser user = new()
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            PhoneNumber = mobile,
            FirstName = command.FirstName.Trim(),
            LastName = command.LastName.Trim(),
            DateOfBirth = command.DateOfBirth,
            Gender = command.Gender,
            // PR-18/19: the language the person registered in becomes their profile language (e-mails use it).
            Locale = WebHosting.SupportedCultures.FirstOrDefault(c => string.Equals(c, CultureInfo.CurrentUICulture.Name, StringComparison.OrdinalIgnoreCase)) ?? WebHosting.DefaultCulture,
            CreatedAt = now,
            UpdatedAt = now,
            LastPasswordChangeAt = now,
        };

        IdentityResult created = await _users.CreateAsync(user, command.Password).ConfigureAwait(false);
        if (!created.Succeeded)
        {
            return new RegistrationOutcome(
                AccountResult.Failed([.. created.Errors.Select(e => new AccountError(MapIdentityField(e.Code), e.Description))]),
                null);
        }

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.UserRegister, AuditActorType.User, user.Id, TargetType: "user", TargetId: user.Id,
                Metadata: $"{{\"terms\":\"{command.TermsVersion}\"}}", IpAddress: command.IpAddress, UserAgent: command.UserAgent),
            cancellationToken).ConfigureAwait(false);

        await IssueCodeAsync(user.Id, OneTimeCodePurpose.EmailVerification, cancellationToken).ConfigureAwait(false);
        return new RegistrationOutcome(AccountResult.Success, user.Id);
    }

    /// <summary>
    /// Tells an account's owner that someone tried to register with its address or mobile (D-L), at most
    /// <see cref="RegistrationOptions.AttemptNoticesPerDay"/> times in 24 hours; every attempt is audited.
    /// </summary>
    private async Task NotifyAttemptAsync(SangamUser owner, bool byMobile, RegisterUserCommand command, CancellationToken cancellationToken)
    {
        DateTimeOffset since = _clock.UtcNow.AddHours(-24);
        int recent = await _db.AuditEvents.CountAsync(
            e => e.Action == AuditActions.UserRegisterDuplicate && e.TargetId == owner.Id && e.OccurredAt > since, cancellationToken).ConfigureAwait(false);
        string channel = "none";
        if (recent < _registration.AttemptNoticesPerDay)
        {
            if (byMobile && await _smsNotices.TrySendRegistrationNoticeAsync(owner, command.IpAddress, cancellationToken).ConfigureAwait(false))
            {
                channel = "sms";
            }
            else if (!string.IsNullOrEmpty(owner.Email))
            {
                string kind = byMobile ? MessageTemplateKinds.MobileAttemptNotice : MessageTemplateKinds.RegistrationAttemptNotice;
                EmailMessage notice = await _templates.EmailAsync(kind, owner.Locale, null, null, NoValues, owner.Email, owner.DisplayName, cancellationToken).ConfigureAwait(false);
                await _email.SendAsync(notice, cancellationToken).ConfigureAwait(false);
                channel = "email";
            }
        }

        string reason = byMobile ? "mobile_taken" : "email_taken";
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.UserRegisterDuplicate, AuditActorType.Anonymous, TargetType: "user", TargetId: owner.Id,
                Metadata: $"{{\"reason\":\"{reason}\",\"notified\":\"{channel}\"}}", IpAddress: command.IpAddress, UserAgent: command.UserAgent),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<UserSummary?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        SangamUser? user = await _users.FindByEmailAsync(email.Trim()).ConfigureAwait(false);
        return user is null || user.Status == UserStatus.DeletedHard ? null : ToSummary(user);
    }

    /// <inheritdoc />
    public async Task<UserSummary?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        SangamUser? user = await _users.FindByIdAsync(userId.ToString("D")).ConfigureAwait(false);
        return user is null || user.Status == UserStatus.DeletedHard ? null : ToSummary(user);
    }

    /// <inheritdoc />
    public async Task<OtpIssueResult> IssueCodeAsync(Guid userId, OneTimeCodePurpose purpose, CancellationToken cancellationToken = default)
    {
        SangamUser user = await _users.FindByIdAsync(userId.ToString("D")).ConfigureAwait(false)
            ?? throw new InvalidOperationException("User not found.");

        (OtpIssueResult result, string? code) = await _codes.IssueAsync(userId, purpose, cancellationToken).ConfigureAwait(false);
        if (result.Status != OtpIssueStatus.Sent || code is null)
        {
            return result;
        }

        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["name"] = user.DisplayName,
            ["code"] = code,
            ["minutes"] = ((int)Math.Round(_otp.Lifetime.TotalMinutes)).ToString(CultureInfo.InvariantCulture),
        };
        EmailMessage message = await _templates.EmailAsync(CodeKind(purpose), user.Locale, _current.AppId, _current.OrgId, values, user.Email!, user.DisplayName, cancellationToken).ConfigureAwait(false);
        await _email.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.UserOtpIssue, AuditActorType.User, userId, TargetType: "user", TargetId: userId, Metadata: PurposeJson(purpose)),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async Task<OtpVerifyStatus> VerifyCodeAsync(Guid userId, OneTimeCodePurpose purpose, string code, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        OtpVerifyStatus status = await _codes.VerifyAsync(userId, purpose, code, cancellationToken).ConfigureAwait(false);

        if (status != OtpVerifyStatus.Valid)
        {
            await _audit.WriteAsync(
                new AuditEntry(AuditActions.UserOtpFail, AuditActorType.User, userId, TargetType: "user", TargetId: userId,
                    Metadata: PurposeJson(purpose, status.ToString().ToLowerInvariant()), IpAddress: ipAddress),
                cancellationToken).ConfigureAwait(false);
            return status;
        }

        if (purpose == OneTimeCodePurpose.EmailVerification)
        {
            SangamUser user = await _users.FindByIdAsync(userId.ToString("D")).ConfigureAwait(false)
                ?? throw new InvalidOperationException("User not found.");
            if (!user.EmailConfirmed)
            {
                user.EmailConfirmed = true;
                user.UpdatedAt = _clock.UtcNow;
                await _users.UpdateAsync(user).ConfigureAwait(false);
                await _audit.WriteAsync(
                    new AuditEntry(AuditActions.UserEmailVerify, AuditActorType.User, userId, TargetType: "user", TargetId: userId, IpAddress: ipAddress),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return status;
    }

    /// <inheritdoc />
    public async Task<SignInCheck> CheckPasswordAsync(string email, string password, SignInPolicy? appPolicy, string? ipAddress, string? userAgent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);

        SangamUser? user = await _users.FindByEmailAsync(email.Trim()).ConfigureAwait(false);
        if (user is null || user.Status == UserStatus.DeletedHard)
        {
            // D-L: an address with no account behaves like one with a wrong password — the same hashing time, and
            // the same lockout after the same number of tries — so neither reveals whether an account exists.
            bool locked = await _unknownLockout.IsLockedOutAsync(email, cancellationToken).ConfigureAwait(false);
            if (!locked)
            {
                _dummyHash ??= _users.PasswordHasher.HashPassword(new SangamUser(), "Sangam-timing-equaliser-1!");
                _ = _users.PasswordHasher.VerifyHashedPassword(new SangamUser(), _dummyHash, password);
                locked = await _unknownLockout.RecordFailureAsync(email, cancellationToken).ConfigureAwait(false);
                if (locked)
                {
                    SangamMetrics.LockoutCount.Add(1);
                }
            }

            SangamMetrics.SignInFailureCount.Add(1);

            await _audit.WriteAsync(
                new AuditEntry(AuditActions.UserLoginFail, AuditActorType.Anonymous, Metadata: locked ? "{\"reason\":\"unknown_email\",\"locked\":true}" : "{\"reason\":\"unknown_email\"}", IpAddress: ipAddress, UserAgent: userAgent),
                cancellationToken).ConfigureAwait(false);
            return new SignInCheck(locked ? SignInStatus.LockedOut : SignInStatus.InvalidCredentials, null, SignInMode.Password);
        }

        SignInMode mode = SignInModes.Resolve(appPolicy, user.SignInPreference);

        if (await _users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            SangamMetrics.SignInFailureCount.Add(1);
            await LogLoginFailAsync(user.Id, "locked_out", ipAddress, userAgent, cancellationToken).ConfigureAwait(false);
            return new SignInCheck(SignInStatus.LockedOut, null, mode);
        }

        if (!await _users.CheckPasswordAsync(user, password).ConfigureAwait(false))
        {
            await _users.AccessFailedAsync(user).ConfigureAwait(false);
            await LogLoginFailAsync(user.Id, "wrong_password", ipAddress, userAgent, cancellationToken).ConfigureAwait(false);
            bool nowLocked = await _users.IsLockedOutAsync(user).ConfigureAwait(false);
            SangamMetrics.SignInFailureCount.Add(1);
            if (nowLocked)
            {
                SangamMetrics.LockoutCount.Add(1);
            }

            return new SignInCheck(nowLocked ? SignInStatus.LockedOut : SignInStatus.InvalidCredentials, null, mode);
        }

        await _users.ResetAccessFailedCountAsync(user).ConfigureAwait(false);

        if (user.Status != UserStatus.Active)
        {
            await LogLoginFailAsync(user.Id, "status_" + user.Status.ToString().ToLowerInvariant(), ipAddress, userAgent, cancellationToken).ConfigureAwait(false);
            return new SignInCheck(SignInStatus.NotAllowed, null, mode);
        }

        UserSummary summary = ToSummary(user);
        if (!user.EmailConfirmed)
        {
            return new SignInCheck(SignInStatus.EmailNotVerified, summary, mode);
        }

        if (mode == SignInMode.PasswordAndOtp)
        {
            await IssueCodeAsync(user.Id, OneTimeCodePurpose.SignIn, cancellationToken).ConfigureAwait(false);
            return new SignInCheck(SignInStatus.RequiresOtp, summary, mode);
        }

        return new SignInCheck(SignInStatus.Succeeded, summary, mode);
    }

    /// <inheritdoc />
    public async Task<SignInCheck> BeginOtpSignInAsync(string email, SignInPolicy? appPolicy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        SangamUser? user = await _users.FindByEmailAsync(email.Trim()).ConfigureAwait(false);
        if (user is null || user.Status != UserStatus.Active || !user.EmailConfirmed)
        {
            return new SignInCheck(SignInStatus.InvalidCredentials, null, SignInMode.OtpOnly);
        }

        SignInMode mode = SignInModes.Resolve(appPolicy, user.SignInPreference);
        if (mode != SignInMode.OtpOnly)
        {
            // rc.2: no code for a password sign-in, but the owner is told so by e-mail; the screen stays the same for every address.
            await RemindPasswordAccountAsync(user, cancellationToken).ConfigureAwait(false);
            return new SignInCheck(SignInStatus.InvalidCredentials, null, mode);
        }

        await IssueCodeAsync(user.Id, OneTimeCodePurpose.SignIn, cancellationToken).ConfigureAwait(false);
        return new SignInCheck(SignInStatus.RequiresOtp, ToSummary(user), mode);
    }

    /// <summary>
    /// rc.2: someone asked for a sign-in code for an account whose sign-in uses a password. The owner is reminded by
    /// e-mail, at most <see cref="PasswordAccountRemindersPerDay"/> times in 24 hours so the button cannot be used to
    /// flood a mailbox; every request is audited, with whether a reminder went out.
    /// </summary>
    private async Task RemindPasswordAccountAsync(SangamUser owner, CancellationToken cancellationToken)
    {
        DateTimeOffset since = _clock.UtcNow.AddHours(-24);
        int recent = await _db.AuditEvents.CountAsync(
            e => e.Action == AuditActions.UserOtpPasswordAccount && e.TargetId == owner.Id && e.OccurredAt > since, cancellationToken).ConfigureAwait(false);
        string channel = "none";
        if (recent < PasswordAccountRemindersPerDay && !string.IsNullOrEmpty(owner.Email))
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal) { ["name"] = owner.DisplayName };
            EmailMessage reminder = await _templates.EmailAsync(
                MessageTemplateKinds.PasswordAccountCodeNotice, owner.Locale, null, null, values, owner.Email, owner.DisplayName, cancellationToken).ConfigureAwait(false);
            await _email.SendAsync(reminder, cancellationToken).ConfigureAwait(false);
            channel = "email";
        }

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.UserOtpPasswordAccount, AuditActorType.Anonymous, TargetType: "user", TargetId: owner.Id,
                Metadata: $"{{\"notified\":\"{channel}\"}}"),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task RecordSignInAsync(Guid userId, SignInMode mode, string? ipAddress, string? userAgent, CancellationToken cancellationToken = default)
        => RecordSignInAsync(userId, mode, codeBySms: false, ipAddress, userAgent, cancellationToken);

    /// <inheritdoc />
    public Task RecordSignInAsync(Guid userId, SignInMode mode, bool codeBySms, string? ipAddress, string? userAgent, CancellationToken cancellationToken = default)
    {
        SangamMetrics.SignInCount.Add(1);
        return _audit.WriteAsync(
            new AuditEntry(AuditActions.UserLoginSuccess, AuditActorType.User, userId, TargetType: "user", TargetId: userId,
                Metadata: codeBySms
                    ? $"{{\"mode\":\"{SignInModes.ToCode(mode)}\",\"channel\":\"sms\"}}"
                    : $"{{\"mode\":\"{SignInModes.ToCode(mode)}\"}}",
                IpAddress: ipAddress, UserAgent: userAgent),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Guid?> RequestPasswordResetAsync(string email, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        SangamUser? user = await _users.FindByEmailAsync(email.Trim()).ConfigureAwait(false);
        if (user is null || user.Status == UserStatus.DeletedHard)
        {
            await _audit.WriteAsync(
                new AuditEntry(AuditActions.UserPasswordResetRequest, AuditActorType.Anonymous, Metadata: "{\"reason\":\"unknown_email\"}", IpAddress: ipAddress),
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.UserPasswordResetRequest, AuditActorType.User, user.Id, TargetType: "user", TargetId: user.Id, IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        await IssueCodeAsync(user.Id, OneTimeCodePurpose.PasswordReset, cancellationToken).ConfigureAwait(false);
        return user.Id;
    }

    /// <inheritdoc />
    public async Task<AccountResult> ReplacePasswordAsync(Guid userId, string newPassword, string reason, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newPassword);
        ArgumentNullException.ThrowIfNull(reason);
        PasswordStrengthResult replacement = PasswordStrength.Evaluate(newPassword);
        if (!replacement.MeetsPolicy)
        {
            return AccountResult.Failed(new AccountError("NewPassword", PolicyMessage(replacement)));
        }

        SangamUser? user = await _users.FindByIdAsync(userId.ToString("D")).ConfigureAwait(false);
        if (user is null || user.Status != UserStatus.Active)
        {
            return AccountResult.Failed(new AccountError("NewPassword", "This account cannot sign in."));
        }

        if (await _users.CheckPasswordAsync(user, newPassword).ConfigureAwait(false))
        {
            return AccountResult.Failed(new AccountError("NewPassword", "Choose a password different from the one you have now."));
        }

        string token = await _users.GeneratePasswordResetTokenAsync(user).ConfigureAwait(false);
        IdentityResult reset = await _users.ResetPasswordAsync(user, token, newPassword).ConfigureAwait(false);
        if (!reset.Succeeded)
        {
            return AccountResult.Failed([.. reset.Errors.Select(e => new AccountError("NewPassword", e.Description))]);
        }

        user.LastPasswordChangeAt = _clock.UtcNow;
        user.UpdatedAt = user.LastPasswordChangeAt;
        await _users.UpdateAsync(user).ConfigureAwait(false);
        await _users.UpdateSecurityStampAsync(user).ConfigureAwait(false);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.UserPasswordChange, AuditActorType.User, user.Id, TargetType: "user", TargetId: user.Id,
                Metadata: reason is "too_short" or "breached" or "changed" ? $"{{\"reason\":\"{reason}\"}}" : "{}", IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        await NoticeAsync(MessageTemplateKinds.PasswordChangedNotice, user, cancellationToken).ConfigureAwait(false);
        return AccountResult.Success;
    }

    /// <inheritdoc />
    public async Task<AccountResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);
        SangamUser? user = await _users.FindByIdAsync(userId.ToString("D")).ConfigureAwait(false);
        if (user is null || user.Status != UserStatus.Active)
        {
            return AccountResult.Failed(new AccountError("CurrentPassword", "Your password could not be changed. Sign in again and retry."));
        }

        if (await _users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            return AccountResult.Failed(new AccountError("CurrentPassword", "Too many failed attempts. Try again in 15 minutes, or reset your password."));
        }

        if (!await _users.CheckPasswordAsync(user, currentPassword).ConfigureAwait(false))
        {
            await _users.AccessFailedAsync(user).ConfigureAwait(false);
            await _audit.WriteAsync(
                new AuditEntry(AuditActions.UserPasswordChange, AuditActorType.User, user.Id, TargetType: "user", TargetId: user.Id,
                    Metadata: "{\"reason\":\"changed\",\"outcome\":\"wrong_current_password\"}", IpAddress: ipAddress),
                cancellationToken).ConfigureAwait(false);
            return AccountResult.Failed(new AccountError("CurrentPassword", "That is not your current password."));
        }

        await _users.ResetAccessFailedCountAsync(user).ConfigureAwait(false);
        return await ReplacePasswordAsync(userId, newPassword, "changed", ipAddress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AccountResult> ResetPasswordAsync(string email, string code, string newPassword, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(newPassword);

        PasswordStrengthResult strength = PasswordStrength.Evaluate(newPassword);
        if (!strength.MeetsPolicy)
        {
            return AccountResult.Failed(new AccountError("NewPassword", PolicyMessage(strength)));
        }

        SangamUser? user = await _users.FindByEmailAsync(email.Trim()).ConfigureAwait(false);
        if (user is null || user.Status == UserStatus.DeletedHard)
        {
            // Same message as a wrong code: no enumeration through the reset form either.
            return AccountResult.Failed(new AccountError("Code", "That code is not valid. Request a new one."));
        }

        OtpVerifyStatus status = await VerifyCodeAsync(user.Id, OneTimeCodePurpose.PasswordReset, code, ipAddress, cancellationToken).ConfigureAwait(false);
        if (status != OtpVerifyStatus.Valid)
        {
            return AccountResult.Failed(new AccountError("Code", status == OtpVerifyStatus.Invalid
                ? "That code is not correct. Check the email and try again."
                : "That code has expired. Request a new one."));
        }

        string token = await _users.GeneratePasswordResetTokenAsync(user).ConfigureAwait(false);
        IdentityResult reset = await _users.ResetPasswordAsync(user, token, newPassword).ConfigureAwait(false);
        if (!reset.Succeeded)
        {
            return AccountResult.Failed([.. reset.Errors.Select(e => new AccountError("NewPassword", e.Description))]);
        }

        user.LastPasswordChangeAt = _clock.UtcNow;
        user.UpdatedAt = user.LastPasswordChangeAt;
        await _users.UpdateAsync(user).ConfigureAwait(false);
        await _users.UpdateSecurityStampAsync(user).ConfigureAwait(false);
        await _users.ResetAccessFailedCountAsync(user).ConfigureAwait(false);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.UserPasswordResetComplete, AuditActorType.User, user.Id, TargetType: "user", TargetId: user.Id, IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        await NoticeAsync(MessageTemplateKinds.PasswordChangedNotice, user, cancellationToken).ConfigureAwait(false);
        return AccountResult.Success;
    }

    private static string PolicyMessage(PasswordStrengthResult strength)
        => strength.IsNotTooLong ? PasswordPolicyMessage : "Use at most 128 characters.";

    private Task NoticeAsync(string kind, SangamUser user, CancellationToken cancellationToken)
        => _notices?.SendAsync(kind, user, cancellationToken) ?? Task.CompletedTask;

    /// <inheritdoc />
    public async Task SetSignInPreferenceAsync(Guid userId, SignInMode preference, CancellationToken cancellationToken = default)
    {
        SangamUser user = await _users.FindByIdAsync(userId.ToString("D")).ConfigureAwait(false)
            ?? throw new InvalidOperationException("User not found.");
        if (user.SignInPreference == preference)
        {
            return;
        }

        user.SignInPreference = preference;
        user.UpdatedAt = _clock.UtcNow;
        await _users.UpdateAsync(user).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.UserSignInPreferenceChange, AuditActorType.User, userId, TargetType: "user", TargetId: userId,
                Metadata: $"{{\"preference\":\"{SignInModes.ToCode(preference)}\"}}"),
            cancellationToken).ConfigureAwait(false);
    }

    private Task LogLoginFailAsync(Guid userId, string reason, string? ipAddress, string? userAgent, CancellationToken cancellationToken)
        => _audit.WriteAsync(
            new AuditEntry(AuditActions.UserLoginFail, AuditActorType.User, userId, TargetType: "user", TargetId: userId,
                Metadata: $"{{\"reason\":\"{reason}\"}}", IpAddress: ipAddress, UserAgent: userAgent),
            cancellationToken);

    private static UserSummary ToSummary(SangamUser u) => new(
        u.Id, u.FirstName, u.LastName, u.Email ?? string.Empty, u.EmailConfirmed, u.PhoneNumber, u.PhoneNumberConfirmed,
        u.DateOfBirth, u.Gender, u.Locale, u.SignInPreference, u.CreatedAt, u.TwoFactorEnabled, u.UpdatedAt, u.SecurityStamp ?? string.Empty, u.IdentityVerifiedAt, u.Status);

    private static string PurposeJson(OneTimeCodePurpose purpose, string? reason = null)
        => reason is null
            ? $"{{\"purpose\":\"{SnakeCase(purpose)}\"}}"
            : $"{{\"purpose\":\"{SnakeCase(purpose)}\",\"reason\":\"{reason}\"}}";

    // "EmailVerification" → "email_verification", the form the activity history reads.
    private static string SnakeCase(OneTimeCodePurpose purpose)
        => string.Concat(purpose.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static string? MapIdentityField(string code) => code switch
    {
        _ when code.StartsWith("Password", StringComparison.Ordinal) => "Password",
        _ when code.StartsWith("Duplicate", StringComparison.Ordinal) || code.StartsWith("InvalidEmail", StringComparison.Ordinal) => "Email",
        _ => null,
    };

    private static bool LooksLikeEmail(string email)
        => email.Length is >= 5 and <= 256
        && email.IndexOf('@', StringComparison.Ordinal) is > 0 and var at
        && at < email.Length - 3
        && email.LastIndexOf('.') > at
        && !email.Contains(' ', StringComparison.Ordinal);

    private static bool LooksLikeE164(string mobile)
        => mobile.Length is >= 8 and <= 16 && mobile[0] == '+' && mobile.Skip(1).All(char.IsDigit) && mobile[1] != '0';

    /// <summary>Strips spaces, dashes and parentheses; an Indian ten-digit number without a code gets +91.</summary>
    /// <param name="raw">What the user typed.</param>
    /// <returns>E.164 candidate.</returns>
    public static string NormaliseMobile(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        string digits = new([.. raw.Where(c => char.IsDigit(c) || c == '+')]);
        if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            digits = "+" + digits[2..];
        }

        if (!digits.StartsWith('+'))
        {
            digits = digits.Length == 10 ? "+91" + digits : "+" + digits;
        }

        return digits;
    }

    private static string CodeKind(OneTimeCodePurpose purpose) => purpose switch
    {
        OneTimeCodePurpose.EmailVerification => MessageTemplateKinds.EmailVerification,
        OneTimeCodePurpose.EmailChange => MessageTemplateKinds.EmailChangeCode,
        OneTimeCodePurpose.PasswordReset => MessageTemplateKinds.PasswordReset,
        _ => MessageTemplateKinds.SignInCode,
    };
}
