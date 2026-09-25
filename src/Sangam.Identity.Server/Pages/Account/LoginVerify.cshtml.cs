using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Accounts;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Authorization;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>The code step of a sign-in, for both <c>password_and_otp</c> and <c>otp_only</c>.</summary>
public sealed class LoginVerifyModel : AuthPageModel
{
    private readonly IAccountService _accounts;
    private readonly IAppDirectory _apps;
    private readonly OneTimeCodeService _codes;
    private readonly IClock _clock;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Account service.</param>
    /// <param name="apps">App directory.</param>
    /// <param name="codes">Code service, for the resend countdown.</param>
    /// <param name="clock">Clock.</param>
    public LoginVerifyModel(IAccountService accounts, IAppDirectory apps, OneTimeCodeService codes, IClock clock)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _codes = codes ?? throw new ArgumentNullException(nameof(codes));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>The typed code.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter the 6-digit code from the email.")]
    public string Code { get; set; } = string.Empty;

    /// <summary>Where to go after sign-in.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>Page-level error.</summary>
    public string? Error { get; private set; }

    /// <summary>Seconds until a new code may be requested; 0 when allowed now.</summary>
    public int ResendIn { get; private set; }

    /// <summary>Countdown as m:ss.</summary>
    public string ResendLabel => TimeSpan.FromSeconds(ResendIn).ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>Meta-refresh interval so the disabled button re-enables without JavaScript.</summary>
    public int RefreshSeconds => ResendIn > 0 ? ResendIn : 0;

    /// <summary>Renders the code form, or sends the user back to sign-in when there is no pending flow.</summary>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        PendingFlow? pending = await SangamAuthentication.ReadPendingAsync(HttpContext, SangamAuthentication.Pending.SignInOtp);
        if (pending is null)
        {
            return RedirectToPage("/Account/Login", new { returnUrl = ReturnUrl });
        }

        await ComputeResendAsync(pending, cancellationToken);
        return Page();
    }

    /// <summary>Checks the code and establishes the session.</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        PendingFlow? pending = await SangamAuthentication.ReadPendingAsync(HttpContext, SangamAuthentication.Pending.SignInOtp);
        if (pending is null)
        {
            return RedirectToPage("/Account/Login", new { returnUrl = ReturnUrl });
        }

        await ComputeResendAsync(pending, cancellationToken);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (pending.UserId is null || pending.UserId == Guid.Empty)
        {
            Error = "That code is not valid. Request a new one.";
            return Page();
        }

        OtpVerifyStatus status = await _accounts.VerifyCodeAsync(pending.UserId.Value, OneTimeCodePurpose.SignIn, Code, ClientIp, cancellationToken);
        if (status != OtpVerifyStatus.Valid)
        {
            Error = status == OtpVerifyStatus.Invalid
                ? "That code is not correct. Check the email and try again."
                : "That code has expired. Request a new one.";
            return Page();
        }

        UserSummary? user = await _accounts.FindByIdAsync(pending.UserId.Value, cancellationToken);
        if (user is null)
        {
            return RedirectToPage("/Account/Login");
        }

        SignInMode mode = pending.Mode ?? SignInMode.PasswordAndOtp;
        await SangamAuthentication.SignInSessionAsync(HttpContext, user, mode, Partner?.Id, PartnerContext.DeviceLabelFromReturnUrl(ReturnUrl));
        await _accounts.RecordSignInAsync(user.Id, mode, ClientIp, ClientUserAgent, cancellationToken);
        return LocalRedirect(SafeReturnUrl(ReturnUrl));
    }

    /// <summary>Sends a fresh code (subject to the cooldown).</summary>
    public async Task<IActionResult> OnPostResendAsync(CancellationToken cancellationToken)
    {
        PendingFlow? pending = await SangamAuthentication.ReadPendingAsync(HttpContext, SangamAuthentication.Pending.SignInOtp);
        if (pending is null)
        {
            return RedirectToPage("/Account/Login", new { returnUrl = ReturnUrl });
        }

        if (pending.UserId is not null && pending.UserId != Guid.Empty)
        {
            await _accounts.IssueCodeAsync(pending.UserId.Value, OneTimeCodePurpose.SignIn, cancellationToken);
        }

        return RedirectToPage("/Account/LoginVerify", new { returnUrl = ReturnUrl });
    }

    private async Task ComputeResendAsync(PendingFlow pending, CancellationToken cancellationToken)
    {
        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        if (pending.UserId is null || pending.UserId == Guid.Empty)
        {
            ResendIn = 60;
            return;
        }

        DateTimeOffset? next = await _codes.NextIssueAllowedAtAsync(pending.UserId.Value, OneTimeCodePurpose.SignIn, cancellationToken);
        ResendIn = next is null ? 0 : (int)Math.Ceiling((next.Value - _clock.UtcNow).TotalSeconds);
    }
}
