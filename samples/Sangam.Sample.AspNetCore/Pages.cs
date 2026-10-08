using System.Net;
using System.Security.Claims;
using Sangam.Client;
using Sangam.Shared.Constants;

/// <summary>The sample's one page, written by hand to keep the sample small.</summary>
internal static class Pages
{
    /// <summary>The home page, signed in or not.</summary>
    /// <param name="page">The page template.</param>
    /// <param name="principal">The person.</param>
    public static string Home(string page, ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.GetSangamUser() is not SangamUser user)
        {
            const string SignIn = "<section><h1>Sign in with Sangam</h1><p class=\"muted\">This sample uses Sangam.Client: the sign-in is completed on the server and the browser holds an encrypted session cookie.</p><a class=\"button\" href=\"/auth/login?returnTo=/\">Sign in with Sangam</a></section>";
            return page.Replace("{{user}}", string.Empty, StringComparison.Ordinal).Replace("{{body}}", SignIn, StringComparison.Ordinal);
        }

        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        string memberships = string.Concat(user.Memberships.Select(m => $"<li><b>{E(m.Role)}</b> at {E(m.OrganisationName)} <code>{E(m.Path)}</code>{(m.AppliesToDescendants ? " (and below)" : string.Empty)}: {E(string.Join(", ", m.Permissions))}</li>"));
        string options = string.Concat(user.Memberships.Select(m => $"<option value=\"{E(m.Path)}\">{E(m.OrganisationName)}</option>"));
        string body = $"<section><h1>Signed in as {E(user.Name)}</h1><p class=\"muted\">Sangam id <code>{E(user.Id.ToString())}</code> · {E(principal.GetAuthenticationLevel())}</p></section>"
            + $"<section data-panel=\"organisations\"><h2>Your organisations in this application</h2><ul>{(memberships.Length == 0 ? "<li class=\"muted\">No role yet.</li>" : memberships)}</ul></section>"
            + $"<section data-panel=\"permission\"><h2>Check a permission</h2><div class=\"row\"><select id=\"org\" aria-label=\"Organisation\">{options}<option value=\"/0192a6b0-0000-7000-8000-00000000ffff/\">An organisation you have no role in</option></select>"
            + "<input id=\"permission\" value=\"vitals:read\" aria-label=\"Permission\"><button class=\"ghost\" id=\"ask\">Ask the server</button></div><p id=\"answer\"></p></section>"
            + $"<section data-panel=\"signature\"><h2>Sign record SOP-114</h2><p class=\"muted\">Signing needs a two-factor sign-in from the last five minutes ({SangamAcr.Signature}). "
            + (principal.Satisfies(SangamAcr.Signature) ? "Yours qualifies." : "Sangam will ask you to sign in again first.")
            + "</p><button data-action=\"sign\" id=\"sign\">Sign as approved</button><div id=\"signed\"></div></section>";
        string header = $"<span class=\"row\">{E(user.Name)} <a class=\"button ghost\" href=\"/auth/logout\">Sign out</a></span>";
        return page.Replace("{{user}}", header, StringComparison.Ordinal).Replace("{{body}}", body, StringComparison.Ordinal);
    }
}
