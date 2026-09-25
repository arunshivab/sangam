using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Accounts;

/// <summary>
/// <see cref="IAccountService"/> on ASP.NET Core Identity. The only place <see cref="SangamUser"/>
/// is read for account flows; everything leaves as <see cref="UserSummary"/> or a result.
/// </summary>
public sealed class AccountService : IAccountService
{
    private const string PasswordPolicyMessage = "Use at least 8 characters with an uppercase letter, a lowercase letter, a number and a symbol, and not a common password.";

    private readonly UserManager<SangamUser> _users;
    private readonly SangamDbContext _db;
    private readonly OneTimeCodeService _codes;
    private readonly IEmailSender _email;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly OtpOptions _otp;

    /// <summary>Initialises the service.</summary>
    /// <param name="users">Identity user manager.</param>
    /// <param name="db">Database.</param>
    /// <param name="codes">One-time code service.</param>
    /// <param name="email">Email sender.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="otp">Code policy.</param>
    public AccountService(
        UserManager<SangamUser> users,
        SangamDbContext db,
        OneTimeCodeService codes,
        IEmailSender email,
        IAuditWriter audit,
        IClock clock,
        OtpOptions otp)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _codes = codes ?? throw new ArgumentNullException(nameof(codes));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _otp = otp ?? throw new ArgumentNullException(nameof(otp));
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
            errors.Add(new AccountError("Password", PasswordPolicyMessage));
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
        bool emailTaken = await _users.FindByEmailAsync(email).ConfigureAwait(false) is not null;
        bool mobileTaken = await _db.Users.AnyAsync(u => u.PhoneNumber == mobile && u.Status != UserStatus.DeletedHard, cancellationToken).ConfigureAwait(false);
        if (emailTaken || mobileTaken)
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

        await _email.SendAsync(AccountEmails.ForCode(user.Email!, user.DisplayName, purpose, code, _otp.Lifetime), cancellationToken).ConfigureAwait(false);
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
            await _audit.WriteAsync(
                new AuditEntry(AuditActions.UserLoginFail, AuditActorType.Anonymous, Metadata: "{\"reason\":\"unknown_email\"}", IpAddress: ipAddress, UserAgent: userAgent),
                cancellationToken).ConfigureAwait(false);
            return new SignInCheck(SignInStatus.InvalidCredentials, null, SignInMode.Password);
        }

        SignInMode mode = SignInModes.Resolve(appPolicy, user.SignInPreference);

        if (await _users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            await LogLoginFailAsync(user.Id, "locked_out", ipAddress, userAgent, cancellationToken).ConfigureAwait(false);
            return new SignInCheck(SignInStatus.LockedOut, null, mode);
        }

        if (!await _users.CheckPasswordAsync(user, password).ConfigureAwait(false))
        {
            await _users.AccessFailedAsync(user).ConfigureAwait(false);
            await LogLoginFailAsync(user.Id, "wrong_password", ipAddress, userAgent, cancellationToken).ConfigureAwait(false);
            bool nowLocked = await _users.IsLockedOutAsync(user).ConfigureAwait(false);
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
            return new SignInCheck(SignInStatus.InvalidCredentials, null, mode);
        }

        await IssueCodeAsync(user.Id, OneTimeCodePurpose.SignIn, cancellationToken).ConfigureAwait(false);
        return new SignInCheck(SignInStatus.RequiresOtp, ToSummary(user), mode);
    }

    /// <inheritdoc />
    public Task RecordSignInAsync(Guid userId, SignInMode mode, string? ipAddress, string? userAgent, CancellationToken cancellationToken = default)
        => _audit.WriteAsync(
            new AuditEntry(AuditActions.UserLoginSuccess, AuditActorType.User, userId, TargetType: "user", TargetId: userId,
                Metadata: $"{{\"mode\":\"{SignInModes.ToCode(mode)}\"}}", IpAddress: ipAddress, UserAgent: userAgent),
            cancellationToken);

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
    public async Task<AccountResult> ResetPasswordAsync(string email, string code, string newPassword, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(newPassword);

        PasswordStrengthResult strength = PasswordStrength.Evaluate(newPassword);
        if (!strength.MeetsPolicy)
        {
            return AccountResult.Failed(new AccountError("NewPassword", PasswordPolicyMessage));
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
        return AccountResult.Success;
    }

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
        u.DateOfBirth, u.Gender, u.Locale, u.SignInPreference, u.CreatedAt, u.UpdatedAt, u.SecurityStamp ?? string.Empty);

    private static string PurposeJson(OneTimeCodePurpose purpose, string? reason = null)
        => reason is null
            ? $"{{\"purpose\":\"{purpose.ToString().ToLowerInvariant()}\"}}"
            : $"{{\"purpose\":\"{purpose.ToString().ToLowerInvariant()}\",\"reason\":\"{reason}\"}}";

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
}
