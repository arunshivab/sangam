using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Accounts;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>Screen 3 — email verification by code, with a no-JS resend countdown (meta refresh).</summary>
public sealed class VerifyModel : AuthPageModel
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
    public VerifyModel(IAccountService accounts, IAppDirectory apps, OneTimeCodeService codes, IClock clock)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _codes = codes ?? throw new ArgumentNullException(nameof(codes));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Where to continue after verification.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>The typed code.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter the 6-digit code from the email.")]
    public string Code { get; set; } = string.Empty;

    /// <summary>Address the code went to.</summary>
    public string Email { get; private set; } = string.Empty;

    /// <summary>Page-level error.</summary>
    public string? Error { get; private set; }

    /// <summary>Seconds until resend is allowed; 0 when allowed now.</summary>
    public int ResendIn { get; private set; }

    /// <summary>Countdown as m:ss.</summary>
    public string ResendLabel => TimeSpan.FromSeconds(ResendIn).ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>Meta-refresh interval.</summary>
    public int RefreshSeconds => ResendIn > 0 ? ResendIn : 0;

    /// <summary>Renders the code form.</summary>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        UserSummary? user = await LoadPendingUserAsync(cancellationToken);
        if (user is null)
        {
            return RedirectToPage("/Account/Register");
        }

        if (user.EmailVerified)
        {
            return RedirectToPage("/Account/Verified", new { returnUrl = ReturnUrl });
        }

        await PrepareAsync(user, cancellationToken);
        return Page();
    }

    /// <summary>Checks the code; on success the address is verified and a session is opened.</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        UserSummary? user = await LoadPendingUserAsync(cancellationToken);
        if (user is null)
        {
            return RedirectToPage("/Account/Register");
        }

        await PrepareAsync(user, cancellationToken);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        OtpVerifyStatus status = await _accounts.VerifyCodeAsync(user.Id, OneTimeCodePurpose.EmailVerification, Code, ClientIp, cancellationToken);
        if (status != OtpVerifyStatus.Valid)
        {
            Error = status == OtpVerifyStatus.Invalid
                ? "That code is not correct. Check the email and try again."
                : "That code has expired. Request a new one.";
            return Page();
        }

        UserSummary verified = (await _accounts.FindByIdAsync(user.Id, cancellationToken))!;
        await SangamAuthentication.SignInSessionAsync(HttpContext, verified, SignInMode.Password);
        await _accounts.RecordSignInAsync(verified.Id, SignInMode.Password, ClientIp, ClientUserAgent, cancellationToken);
        return RedirectToPage("/Account/Verified", new { returnUrl = ReturnUrl });
    }

    /// <summary>Sends a fresh code (subject to the cooldown).</summary>
    public async Task<IActionResult> OnPostResendAsync(CancellationToken cancellationToken)
    {
        UserSummary? user = await LoadPendingUserAsync(cancellationToken);
        if (user is null)
        {
            return RedirectToPage("/Account/Register");
        }

        await _accounts.IssueCodeAsync(user.Id, OneTimeCodePurpose.EmailVerification, cancellationToken);
        return RedirectToPage("/Account/Verify", new { returnUrl = ReturnUrl });
    }

    private async Task<UserSummary?> LoadPendingUserAsync(CancellationToken cancellationToken)
    {
        PendingFlow? pending = await SangamAuthentication.ReadPendingAsync(HttpContext, SangamAuthentication.Pending.EmailVerification);
        return pending?.UserId is Guid id ? await _accounts.FindByIdAsync(id, cancellationToken) : null;
    }

    private async Task PrepareAsync(UserSummary user, CancellationToken cancellationToken)
    {
        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        Email = user.Email;
        DateTimeOffset? next = await _codes.NextIssueAllowedAtAsync(user.Id, OneTimeCodePurpose.EmailVerification, cancellationToken);
        ResendIn = next is null ? 0 : (int)Math.Ceiling((next.Value - _clock.UtcNow).TotalSeconds);
    }
}
