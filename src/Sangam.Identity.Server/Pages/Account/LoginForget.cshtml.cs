using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>rc.11: "Remove" next to a name on this device's list. Only this browser's list changes; nothing is sent anywhere.</summary>
public sealed class LoginForgetModel : AuthPageModel
{
    /// <summary>A visit without a form goes back to the sign-in screen.</summary>
    public IActionResult OnGet() => Redirect("/login");

    /// <summary>Removes the person and goes back to the sign-in screen.</summary>
    /// <param name="key">The entry's key.</param>
    /// <param name="returnUrl">Where the sign-in was going.</param>
    public IActionResult OnPost(string? key, string? returnUrl)
    {
        if (!string.IsNullOrEmpty(key))
        {
            DeviceAccounts.Forget(HttpContext, key);
        }

        return Redirect(WithReturn("/login", returnUrl is not null && Url.IsLocalUrl(returnUrl) ? returnUrl : null));
    }
}
