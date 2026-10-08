using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Sms;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Sms;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// Verifying the mobile number with a texted code (PR-15, SGM-206 §6). Until then the number is "not yet
/// verified" (D-048) and is not used for sign-in codes.
/// </summary>
[Authorize]
public sealed class MobileModel : AuthPageModel
{
    private readonly IAccountService _accounts;
    private readonly ISmsCodeService _sms;
    private readonly IClock _clock;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Accounts.</param>
    /// <param name="sms">SMS codes.</param>
    /// <param name="clock">Clock.</param>
    public MobileModel(IAccountService accounts, ISmsCodeService sms, IClock clock)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _sms = sms ?? throw new ArgumentNullException(nameof(sms));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>The typed code.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter the 6-digit code from the text message.")]
    public string Code { get; set; } = string.Empty;

    /// <summary>The number, masked to its last four digits.</summary>
    public string MaskedMobile { get; private set; } = string.Empty;

    /// <summary>Whether the number is already verified.</summary>
    public bool Verified { get; private set; }

    /// <summary>Why the number cannot be verified by SMS, when it cannot.</summary>
    public string? Unavailable { get; private set; }

    /// <summary>Seconds until another code may be texted.</summary>
    public int ResendIn { get; private set; }

    /// <summary>Countdown as m:ss.</summary>
    public string ResendLabel => TimeSpan.FromSeconds(ResendIn).ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>A confirmation or explanation carried across a redirect.</summary>
    [TempData]
    public string? Notice { get; set; }

    /// <summary>Page-level error.</summary>
    public string? Error { get; private set; }

    /// <summary>Shows the number and its state.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!_sms.Enabled)
        {
            return NotFound();
        }

        return await LoadAsync(cancellationToken) ? Page() : RedirectToPage("/Account/Login");
    }

    /// <summary>Texts a verification code.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<IActionResult> OnPostSendAsync(CancellationToken cancellationToken)
    {
        if (!_sms.Enabled)
        {
            return NotFound();
        }

        SmsIssueResult result = await _sms.SendMobileVerificationAsync(CurrentUserId(), ClientIp, cancellationToken);
        Notice = result.Status switch
        {
            SmsIssueStatus.Sent => L["We texted a 6-digit code to your mobile. It is valid for 10 minutes."].Value,
            SmsIssueStatus.TooSoon => L["A code was texted less than a minute ago. Use that one, or wait to ask again."].Value,
            SmsIssueStatus.RateLimited => L["Too many codes were asked for in the last hour. Try again later."].Value,
            SmsIssueStatus.ProviderFailed => L["The code could not be texted just now. Try again in a few minutes."].Value,
            _ => null,
        };
        return RedirectToPage();
    }

    /// <summary>Checks the code.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<IActionResult> OnPostVerifyAsync(CancellationToken cancellationToken)
    {
        if (!_sms.Enabled)
        {
            return NotFound();
        }

        if (!await LoadAsync(cancellationToken))
        {
            return RedirectToPage("/Account/Login");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        OtpVerifyStatus status = await _sms.VerifyMobileAsync(CurrentUserId(), Code, ClientIp, cancellationToken);
        if (status == OtpVerifyStatus.Valid)
        {
            Notice = L["Your mobile number is verified. You can now have sign-in codes texted to it."];
            return RedirectToPage();
        }

        Error = status == OtpVerifyStatus.Invalid
            ? L["That code is not correct. Check the text message and try again."]
            : L["That code has expired or was for another number. Ask for a new one."];
        return Page();
    }

    private Guid CurrentUserId() => SangamAuthentication.UserId(base.User) ?? Guid.Empty;

    private async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        UserSummary? user = await _accounts.FindByIdAsync(CurrentUserId(), cancellationToken);
        if (user is null)
        {
            return false;
        }

        MaskedMobile = SmsNumbers.Mask(user.Mobile, 4);
        Verified = user.MobileVerified;
        if (string.IsNullOrEmpty(user.Mobile))
        {
            Unavailable = L["Your account has no mobile number. Add one in your account portal first."];
        }
        else if (!_sms.IsCountryAllowed(user.Mobile))
        {
            Unavailable = L["Codes cannot be texted to numbers in your country yet."];
        }

        DateTimeOffset? next = await _sms.NextSendAllowedAtAsync(user.Id, OneTimeCodePurpose.MobileVerification, cancellationToken);
        ResendIn = next is null ? 0 : (int)Math.Ceiling((next.Value - _clock.UtcNow).TotalSeconds);
        return true;
    }
}
