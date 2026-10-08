using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Authorization;

/// <summary>
/// The checks every way into an application makes before it issues anything (PR-16, PR-17), for the flows that are not
/// /connect/authorize — the device page (PR-21) and SAML (PR-22): signed in; signed in the way the application and the
/// person's organisations require; a second factor where one is required; strongly and recently enough when the
/// application asks. Each failure is a page to send the browser to.
/// </summary>
public static class SignInGate
{
    /// <summary>The signed-in person and their session, or where to send the browser instead.</summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="accounts">Accounts.</param>
    /// <param name="policies">Sign-in policies.</param>
    /// <param name="appId">The application.</param>
    /// <param name="returnUrl">Where to come back to.</param>
    /// <param name="requiredLevel">The assurance level asked for (PR-17), 0 for none.</param>
    /// <param name="authenticatedAfter">When the authentication must be newer than (SAML ForceAuthn), if at all.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<(UserSummary? User, ClaimsPrincipal? Session, string? Redirect)> CheckAsync(
        HttpContext httpContext,
        IAccountService accounts,
        ISecurityPolicyService policies,
        Guid appId,
        string returnUrl,
        int requiredLevel = 0,
        DateTimeOffset? authenticatedAfter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(policies);
        string login = "/login?returnUrl=" + Uri.EscapeDataString(returnUrl);
        AuthenticateResult session = await httpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
        Guid? userId = session.Succeeded && session.Principal is not null ? SangamAuthentication.UserId(session.Principal) : null;
        UserSummary? user = userId is null ? null : await accounts.FindByIdAsync(userId.Value, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return (null, null, login);
        }

        ClaimsPrincipal principal = session.Principal!;
        PersonPolicy person = await policies.ForPersonAsync(user.Id, appId, cancellationToken).ConfigureAwait(false);
        SignInMode required = SignInModes.Resolve(person.Policy.SignIn, user.SignInPreference);
        SignInMode sessionMode = SignInModes.TryParse(principal.FindFirstValue(SangamAuthentication.SessionModeClaim), out SignInMode m) ? m : SignInMode.Password;
        AuthenticationProof proof = AuthenticationProof.FromSession(principal);
        bool tooWeak = !PartnerContext.Satisfies(required, sessionMode)
            || (person.RequiresSecondFactor && user.MfaEnrolled && !SangamAuthentication.HasSecondFactor(principal))
            || AuthenticationAssurance.Level(proof.Acr) < requiredLevel
            || (authenticatedAfter is DateTimeOffset after && (proof.AuthenticatedAt is not DateTimeOffset at || at < after));
        if (tooWeak)
        {
            await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
            return (null, null, login);
        }

        if (person.RequiresSecondFactor && !SangamAuthentication.HasSecondFactor(principal))
        {
            return (null, null, "/login/two-step-required?returnUrl=" + Uri.EscapeDataString(returnUrl));
        }

        return (user, principal, null);
    }
}
