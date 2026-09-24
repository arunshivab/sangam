using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>Sign-out. Screen 8 with the partner return path arrives in PR-04; this is the session-ending core.</summary>
public sealed class LogoutModel : AuthPageModel
{
    private readonly IAuditWriter _audit;

    /// <summary>Initialises the page.</summary>
    /// <param name="audit">Audit writer.</param>
    public LogoutModel(IAuditWriter audit)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>Renders the confirmation; anonymous visitors go to sign-in.</summary>
    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? Page() : RedirectToPage("/Account/Login");

    /// <summary>Ends the session.</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Guid? id = SangamAuthentication.UserId(User);
        await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        await SangamAuthentication.ClearPendingAsync(HttpContext);
        if (id is not null)
        {
            await _audit.WriteAsync(new AuditEntry(AuditActions.UserLogout, AuditActorType.User, id, TargetType: "user", TargetId: id, IpAddress: ClientIp, UserAgent: ClientUserAgent), cancellationToken);
        }

        return RedirectToPage("/Account/Login", new { signedout = true });
    }
}
