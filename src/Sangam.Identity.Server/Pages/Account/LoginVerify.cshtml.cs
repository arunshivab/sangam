using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Sms;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Accounts;
using Sangam.Identity.Infrastructure.Sms;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Authorization;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// The code step of a sign-in, for both <c>password_and_otp</c> and <c>otp_only</c>. The code is e-mailed
/// first; where SMS is on, the person may have it texted to their verified mobile instead (PR-15).
/// </summary>
public sealed class LoginVerifyModel : AuthPageModel
{
    private readonly IAccountService _accounts;
    private readonly IAppDirectory _apps;
    private readonly OneTimeCodeService _codes;
    private readonly ISmsCodeService _sms;
    private readonly IClock _clock;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Account service.</param>
    /// <param name="apps">App directory.</param>
    /// <param name="codes">Code service, for the resend countdown.</param>
    /// <param name="sms">SMS codes.</param>
    /// <param name="clock">Clock.</param>
    public LoginVerifyModel(IAccountService accounts, IAppDirectory apps, OneTimeCodeService codes, ISmsCodeService sms, IClock clock)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _codes = codes ?? throw new ArgumentNullException(nameof(codes));
        _sms = sms ?? throw new ArgumentNullException(nameof(sms));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>The typed code.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter the 6-digit code.")]
    public string Code { get; set; } = string.Empty;

    /// <summary>Where to go after sign-in.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>Page-level error.</summary>
    public string? Error { get; private set; }

    /// <summary>A note left by the previous step (for example, why the code could not be texted).</summary>
    [TempData]
    public string? Notice { get; set; }

    /// <summary>Whether the current code was texted.</summary>
    public bool ViaSms { get; private set; }

    /// <summary>Whether the person may switch to (or back from) a texted code.</summary>
    public bool SmsOffered => _sms.Enabled;

    /// <summary>
    /// The masked mobile the code was texted to. Shown only once the password has been proven, so the page
    /// never tells a stranger who typed an address whether it has a mobile, or which one.
    /// </summary>
    public string? MaskedMobile { get; private set; }

    /// <summary>Seconds until a new code may be requested; 0 when allowed now.</summary>
    public int ResendIn { get; private set; }

    /// <summary>Countdown as m:ss.</summary>
    public string ResendLabel => TimeSpan.FromSeconds(ResendIn).ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>Meta-refresh interval so the disabled button re-enables without JavaScript.</summary>
    public int RefreshSeconds => ResendIn > 0 ? ResendIn : 0;

    /// <summary>Renders the code form, or sends the user back to sign-in when there is no pending flow.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        PendingFlow? pending = await SangamAuthentication.ReadPendingAsync(HttpContext, SangamAuthentication.Pending.SignInOtp);
        if (pending is null)
        {
            return RedirectToPage("/Account/Login", new { returnUrl = ReturnUrl });
        }

        await PrepareAsync(pending, cancellationToken);
        return Page();
    }

    /// <summary>Checks the code and establishes the session.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        PendingFlow? pending = await SangamAuthentication.ReadPendingAsync(HttpContext, SangamAuthentication.Pending.SignInOtp);
        if (pending is null)
        {
            return RedirectToPage("/Account/Login", new { returnUrl = ReturnUrl });
        }

        await PrepareAsync(pending, cancellationToken);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (pending.UserId is null || pending.UserId == Guid.Empty)
        {
            Error = "That code is not valid. Request a new one.";
            return Page();
        }

        OneTimeCodePurpose purpose = pending.ViaSms ? OneTimeCodePurpose.SmsSignIn : OneTimeCodePurpose.SignIn;
        OtpVerifyStatus status = await _accounts.VerifyCodeAsync(pending.UserId.Value, purpose, Code, ClientIp, cancellationToken);
        if (status != OtpVerifyStatus.Valid)
        {
            Error = status == OtpVerifyStatus.Invalid
                ? (pending.ViaSms ? "That code is not correct. Check the text message and try again." : "That code is not correct. Check the email and try again.")
                : "That code has expired. Request a new one.";
            return Page();
        }

        UserSummary? user = await _accounts.FindByIdAsync(pending.UserId.Value, cancellationToken);
        if (user is null)
        {
            return RedirectToPage("/Account/Login");
        }

        SignInMode mode = pending.Mode ?? SignInMode.PasswordAndOtp;
        if (user.MfaEnrolled)
        {
            await SangamAuthentication.StorePendingAsync(HttpContext, SangamAuthentication.Pending.Authenticator, user.Id, mode);
            return RedirectToPage("/Account/LoginMfa", new { returnUrl = ReturnUrl });
        }

        await SangamAuthentication.SignInSessionAsync(HttpContext, user, mode, Partner?.Id, PartnerContext.DeviceLabelFromReturnUrl(ReturnUrl));
        await _accounts.RecordSignInAsync(user.Id, mode, pending.ViaSms, ClientIp, ClientUserAgent, cancellationToken);
        return LocalRedirect(SafeReturnUrl(ReturnUrl));
    }

    /// <summary>Sends a fresh code by the current channel (subject to the cooldown).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostResendAsync(CancellationToken cancellationToken)
    {
        PendingFlow? pending = await SangamAuthentication.ReadPendingAsync(HttpContext, SangamAuthentication.Pending.SignInOtp);
        if (pending is null)
        {
            return RedirectToPage("/Account/Login", new { returnUrl = ReturnUrl });
        }

        if (pending.UserId is Guid userId && userId != Guid.Empty)
        {
            if (pending.ViaSms)
            {
                await _sms.SendSignInCodeAsync(userId, ClientIp, cancellationToken);
            }
            else
            {
                await _accounts.IssueCodeAsync(userId, OneTimeCodePurpose.SignIn, cancellationToken);
            }
        }

        return RedirectToPage("/Account/LoginVerify", new { returnUrl = ReturnUrl });
    }

    /// <summary>Texts a code to the verified mobile instead of e-mailing it.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostSmsAsync(CancellationToken cancellationToken)
    {
        PendingFlow? pending = await SangamAuthentication.ReadPendingAsync(HttpContext, SangamAuthentication.Pending.SignInOtp);
        if (pending is null || !_sms.Enabled)
        {
            return RedirectToPage("/Account/Login", new { returnUrl = ReturnUrl });
        }

        Guid userId = pending.UserId ?? Guid.Empty;
        SmsIssueResult result = userId == Guid.Empty
            ? new SmsIssueResult(SmsIssueStatus.NoAccount)
            : await _sms.SendSignInCodeAsync(userId, ClientIp, cancellationToken);

        bool passwordProven = pending.Mode == SignInMode.PasswordAndOtp;
        if (passwordProven && result.Status is not (SmsIssueStatus.Sent or SmsIssueStatus.TooSoon))
        {
            // The person has proven their password, so the page may say plainly why no text is coming.
            Notice = result.Status switch
            {
                SmsIssueStatus.NoMobile or SmsIssueStatus.MobileNotVerified => "Your account has no verified mobile number yet, so the code stays in your email. You can verify your mobile from your account page.",
                SmsIssueStatus.CountryNotAllowed => "Codes cannot be texted to your mobile's country yet, so the code stays in your email.",
                SmsIssueStatus.RateLimited => "Too many texts were asked for in the last hour. Use the code in your email.",
                _ => "The code could not be texted just now. Use the code in your email.",
            };
            return RedirectToPage("/Account/LoginVerify", new { returnUrl = ReturnUrl });
        }

        // Before the password is proven, every outcome looks the same, so the page reveals nothing about the account.
        await SangamAuthentication.StorePendingAsync(HttpContext, SangamAuthentication.Pending.SignInOtp, userId, pending.Mode, viaSms: true);
        return RedirectToPage("/Account/LoginVerify", new { returnUrl = ReturnUrl });
    }

    /// <summary>Goes back to an e-mailed code.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostEmailAsync(CancellationToken cancellationToken)
    {
        PendingFlow? pending = await SangamAuthentication.ReadPendingAsync(HttpContext, SangamAuthentication.Pending.SignInOtp);
        if (pending is null)
        {
            return RedirectToPage("/Account/Login", new { returnUrl = ReturnUrl });
        }

        Guid userId = pending.UserId ?? Guid.Empty;
        if (userId != Guid.Empty)
        {
            await _accounts.IssueCodeAsync(userId, OneTimeCodePurpose.SignIn, cancellationToken);
        }

        await SangamAuthentication.StorePendingAsync(HttpContext, SangamAuthentication.Pending.SignInOtp, userId, pending.Mode, viaSms: false);
        return RedirectToPage("/Account/LoginVerify", new { returnUrl = ReturnUrl });
    }

    private async Task PrepareAsync(PendingFlow pending, CancellationToken cancellationToken)
    {
        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        ViaSms = pending.ViaSms && _sms.Enabled;
        if (pending.UserId is not Guid userId || userId == Guid.Empty)
        {
            ResendIn = 60;
            return;
        }

        if (ViaSms && pending.Mode == SignInMode.PasswordAndOtp)
        {
            UserSummary? user = await _accounts.FindByIdAsync(userId, cancellationToken);
            MaskedMobile = user?.Mobile is null ? null : SmsNumbers.Mask(user.Mobile);
        }

        DateTimeOffset? next = ViaSms
            ? await _sms.NextSendAllowedAtAsync(userId, OneTimeCodePurpose.SmsSignIn, cancellationToken)
            : await _codes.NextIssueAllowedAtAsync(userId, OneTimeCodePurpose.SignIn, cancellationToken);
        ResendIn = next is null ? 0 : (int)Math.Ceiling((next.Value - _clock.UtcNow).TotalSeconds);
    }
}
