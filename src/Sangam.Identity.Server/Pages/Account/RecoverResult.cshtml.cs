using Microsoft.AspNetCore.Mvc;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>rc.6 (SGM-914 section 5): what happened to a recovery with DigiLocker. Says nothing about the account itself.</summary>
public sealed class RecoverResultModel : AuthPageModel
{
    /// <summary>The outcome code.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Outcome { get; set; }

    /// <summary>The heading.</summary>
    public string Heading { get; private set; } = string.Empty;

    /// <summary>The explanation.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Explains the outcome.</summary>
    public void OnGet()
    {
        (Heading, Text) = Outcome switch
        {
            "started" => (L["Recovery started"].Value, L["Your account will be recovered in 24 hours (72 for administrators) unless the request is cancelled. We have written to your e-mail; when it is done, sign in and set up your authenticator again."].Value),
            "review" => (L["Recovery waiting for review"].Value, L["Your DigiLocker record did not match your account exactly, so a Sangam operator will compare the name, date of birth and gender. You will hear by e-mail."].Value),
            "pending" => (L["A recovery is already waiting"].Value, L["A recovery of your account is already under way. Watch your e-mail."].Value),
            "taken" => (L["Recovery not possible"].Value, L["That DigiLocker account already verifies another Sangam account, so it cannot recover this one."].Value),
            "none" => (L["Nothing to recover"].Value, L["This account has no authenticator to remove. Sign in again."].Value),
            "denied" => (L["Recovery not started"].Value, L["DigiLocker was not allowed to share your details. Sign in again to try once more."].Value),
            "failed" => (L["Recovery not started"].Value, L["DigiLocker did not answer. Sign in again to try once more."].Value),
            _ => (L["Recovery not started"].Value, L["That took too long. Sign in again to start once more."].Value),
        };
    }
}
