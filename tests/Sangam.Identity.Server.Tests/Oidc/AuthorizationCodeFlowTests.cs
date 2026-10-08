using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Server.Tests.Oidc;

/// <summary>
/// The full Authorization Code + PKCE journey, driven over HTTP exactly as a browser with
/// JavaScript disabled would: app starts sign-in, user signs in, consent screen, code,
/// token, userinfo, refresh, and RP-initiated sign-out.
/// </summary>
[Collection("server")]
public sealed class AuthorizationCodeFlowTests
{
    private const string Scope = "openid profile email phone orgs.read offline_access";
    private readonly SangamServerFactory _factory;

    public AuthorizationCodeFlowTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Discovery_AdvertisesTheCodeFlowEndpointsAndPkce()
    {
        using BrowserSession s = new(_factory);
        using HttpClient client = _factory.CreateClient();

        JsonElement doc = await client.GetFromJsonAsync<JsonElement>(new Uri("/.well-known/openid-configuration", UriKind.Relative));

        string[] grants = [.. doc.GetProperty("grant_types_supported").EnumerateArray().Select(e => e.GetString()!)];
        string[] scopes = [.. doc.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()!)];
        Assert.Contains("authorization_code", grants);
        Assert.Contains("refresh_token", grants);
        Assert.Contains("S256", doc.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("phone", scopes);
        Assert.Contains("orgs.read", scopes);
        Assert.EndsWith("/connect/authorize", doc.GetProperty("authorization_endpoint").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("/connect/userinfo", doc.GetProperty("userinfo_endpoint").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("/connect/endsession", doc.GetProperty("end_session_endpoint").GetString(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Authorize_SendsAnonymousUserToSignIn_WithThePartnerChip()
    {
        using BrowserSession s = new(_factory);
        (_, string location, string html) = await s.FollowAsync(Authorize(Pkce().Challenge));

        Assert.StartsWith("/login", location, StringComparison.Ordinal);
        Assert.Contains("sg-partner-chip", html, StringComparison.Ordinal);
        Assert.Contains("Sangam development sample", html, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task FullFlow_Consent_Code_Token_UserInfo_Refresh_And_SignOut()
    {
        using BrowserSession s = new(_factory);
        (string verifier, string challenge) = Pkce();
        string email = await RegisterAndVerifyAsync(s);

        // 1. Authorize lands on the consent screen with the real values.
        (HttpStatusCode consentStatus, string consentPath, string consentHtml) = await s.FollowAsync(Authorize(challenge));
        Assert.Equal(HttpStatusCode.OK, consentStatus);
        Assert.StartsWith("/consent", consentPath, StringComparison.Ordinal);
        Assert.Contains("WILL BE SHARED", consentHtml, StringComparison.Ordinal);
        Assert.Contains("WILL NOT BE SHARED", consentHtml, StringComparison.Ordinal);
        Assert.Contains(email, consentHtml, StringComparison.Ordinal);
        Assert.Contains("Your password", consentHtml, StringComparison.Ordinal);

        // 2. Allow → the app receives a code and the state it sent.
        (HttpStatusCode allowStatus, string? allowLocation, _) = await s.PostFormAsync(consentPath, [], handler: "Allow");
        Assert.Equal(HttpStatusCode.Found, allowStatus);
        string redirect = await FollowToPartnerAsync(s, allowLocation!);
        Uri callback = new(redirect);
        System.Collections.Specialized.NameValueCollection query = HttpUtility.ParseQueryString(callback.Query);
        Assert.Equal(DevelopmentSeeder.SampleRedirectUri, callback.GetLeftPart(UriPartial.Path));
        Assert.Equal("xyz", query["state"]);
        string code = query["code"]!;

        // 3. Token exchange requires the verifier.
        using HttpClient api = _factory.CreateClient();
        JsonElement bad = await PostTokenAsync(api, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = DevelopmentSeeder.SampleRedirectUri,
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_secret"] = DevelopmentSeeder.SampleClientSecret,
            ["code_verifier"] = "wrong-verifier-wrong-verifier-wrong-verifier",
        }, expectSuccess: false);
        Assert.Equal("invalid_grant", bad.GetProperty("error").GetString());

        JsonElement tokens = await PostTokenAsync(api, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = DevelopmentSeeder.SampleRedirectUri,
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_secret"] = DevelopmentSeeder.SampleClientSecret,
            ["code_verifier"] = verifier,
        });

        string accessToken = tokens.GetProperty("access_token").GetString()!;
        string refreshToken = tokens.GetProperty("refresh_token").GetString()!;
        JsonElement idToken = Payload(tokens.GetProperty("id_token").GetString()!);
        Assert.Equal(DevelopmentSeeder.SampleClientId, idToken.GetProperty("aud").GetString());
        Assert.Equal(email, idToken.GetProperty("email").GetString());
        Assert.True(idToken.GetProperty("email_verified").GetBoolean());
        Assert.True(idToken.TryGetProperty("sangam_orgs", out _));
        Assert.False(idToken.TryGetProperty("birthdate", out _), "The ID token stays small; profile detail comes from userinfo.");

        // 4. UserInfo returns the scoped claims.
        using HttpRequestMessage me = new(HttpMethod.Get, "/connect/userinfo");
        me.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage meResponse = await api.SendAsync(me);
        JsonElement claims = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        Assert.Equal(email, claims.GetProperty("email").GetString());
        Assert.Equal("1991-11-05", claims.GetProperty("birthdate").GetString());
        Assert.Equal("female", claims.GetProperty("gender").GetString());
        Assert.False(claims.GetProperty("phone_number_verified").GetBoolean());

        // Without a token, userinfo is closed.
        using HttpResponseMessage anonymous = await api.GetAsync(new Uri("/connect/userinfo", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // 5. Refresh returns fresh tokens for the same user.
        JsonElement refreshed = await PostTokenAsync(api, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_secret"] = DevelopmentSeeder.SampleClientSecret,
        });
        JsonElement refreshedId = Payload(refreshed.GetProperty("id_token").GetString()!);
        Assert.Equal(idToken.GetProperty("sub").GetString(), refreshedId.GetProperty("sub").GetString());

        // 6. A second authorize needs no consent screen — straight back to the app.
        (string secondVerifier, string secondChallenge) = Pkce();
        (_, string? secondLocation, _) = (HttpStatusCode.Found, (await s.FollowAsync(Authorize(secondChallenge))).Location, string.Empty);
        Assert.StartsWith(DevelopmentSeeder.SampleRedirectUri, secondLocation, StringComparison.Ordinal);
        Assert.Contains("code=", secondLocation, StringComparison.Ordinal);
        Assert.DoesNotContain("error=", secondLocation, StringComparison.Ordinal);
        Assert.NotEqual(secondVerifier, verifier);

        // 7. App-initiated sign-out ends the Sangam session and returns to the app.
        string endSession = "/connect/endsession?id_token_hint=" + Uri.EscapeDataString(refreshed.GetProperty("id_token").GetString()!)
            + "&post_logout_redirect_uri=" + Uri.EscapeDataString(DevelopmentSeeder.SamplePostLogoutRedirectUri)
            + "&client_id=" + DevelopmentSeeder.SampleClientId + "&state=bye";
        (HttpStatusCode signOutStatus, string signOutPath, string signOutHtml) = await s.FollowAsync(endSession);
        Assert.Equal(HttpStatusCode.OK, signOutStatus);
        Assert.Contains("Sign out of Sangam?", signOutHtml, StringComparison.Ordinal);
        Assert.Contains("without signing out", signOutHtml, StringComparison.Ordinal);

        (HttpStatusCode postStatus, string? postLocation, _) = await s.PostFormAsync(signOutPath, HiddenFields(signOutHtml));
        Assert.Equal(HttpStatusCode.Found, postStatus);
        Assert.StartsWith(DevelopmentSeeder.SamplePostLogoutRedirectUri, postLocation, StringComparison.Ordinal);
        Assert.Contains("state=bye", postLocation, StringComparison.Ordinal);

        // The session is gone: /account challenges back to sign-in (absolute URL from the cookie handler).
        (_, string afterSignOut, _) = await s.FollowAsync("/account");
        Assert.Contains("/login", afterSignOut, StringComparison.Ordinal);

        // R7: and the refresh token died with it.
        JsonElement afterLogout = await PostTokenAsync(api, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshed.GetProperty("refresh_token").GetString()!,
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_secret"] = DevelopmentSeeder.SampleClientSecret,
        }, expectSuccess: false);
        Assert.Equal("invalid_grant", afterLogout.GetProperty("error").GetString());
    }

    [PostgresFact]
    public async Task ARefreshToken_StopsWorking_WhenThePersonSignsOutEverywhere_OrTheirSecurityChanges()
    {
        // R7 (ASVS V3.3.1, V3.3.3): before, refresh tokens kept working for up to 90 days after either.
        using BrowserSession s = new(_factory);
        await RegisterAndVerifyAsync(s);
        using HttpClient api = _factory.CreateClient();

        (Guid userId, string first) = await RefreshTokenAsync(s, api);
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<Sangam.Identity.Application.Portal.IPortalService>().RevokeAllSessionsAsync(userId, "203.0.113.5");
        }

        Assert.Equal("invalid_grant", (await RefreshAsync(api, first, expectSuccess: false)).GetProperty("error").GetString());

        // A new sign-in, then a change to the person's security stamp (as a password reset makes).
        using BrowserSession again = new(_factory);
        string email = await EmailOfAsync(userId);
        await SignInAsync(again, email);
        (_, string second) = await RefreshTokenAsync(again, api);
        Assert.Equal(JsonValueKind.String, (await RefreshAsync(api, second)).GetProperty("access_token").ValueKind);
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            UserManager<Sangam.Identity.Domain.Entities.SangamUser> users = scope.ServiceProvider.GetRequiredService<UserManager<Sangam.Identity.Domain.Entities.SangamUser>>();
            await users.UpdateSecurityStampAsync((await users.FindByIdAsync(userId.ToString("D")))!);
        }

        JsonElement refused = await RefreshAsync(api, second, expectSuccess: false);
        Assert.Equal("invalid_grant", refused.GetProperty("error").GetString());
    }

    [PostgresFact]
    public async Task Consent_Deny_ReturnsAccessDeniedToTheApp()
    {
        using BrowserSession s = new(_factory);
        (_, string challenge) = Pkce();
        await RegisterAndVerifyAsync(s);

        (_, string consentPath, _) = await s.FollowAsync(Authorize(challenge));
        (HttpStatusCode status, string? location, _) = await s.PostFormAsync(consentPath, [], handler: "Deny");
        Assert.Equal(HttpStatusCode.Found, status);

        string redirect = await FollowToPartnerAsync(s, location!);
        Assert.StartsWith(DevelopmentSeeder.SampleRedirectUri, redirect, StringComparison.Ordinal);
        Assert.Contains("error=access_denied", redirect, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Authorize_RejectsUnknownClientsAndPromptNoneWithoutSession()
    {
        using BrowserSession s = new(_factory);
        (_, string challenge) = Pkce();

        (_, string unknown, _) = await s.FollowAsync(Authorize(challenge).Replace(DevelopmentSeeder.SampleClientId, "no-such-app", StringComparison.Ordinal));
        Assert.DoesNotContain("code=", unknown, StringComparison.Ordinal);

        (_, string silent, _) = await s.FollowAsync(Authorize(challenge) + "&prompt=none");
        Assert.StartsWith(DevelopmentSeeder.SampleRedirectUri, silent, StringComparison.Ordinal);
        Assert.Contains("error=login_required", silent, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    [PostgresFact]
    public async Task SangamsOwnPortal_SkipsTheConsentScreen_AndTheImplicitConsentIsAudited()
    {
        using BrowserSession s = new(_factory);
        (_, string challenge) = Pkce();
        await RegisterAndVerifyAsync(s);
        string redirect = DevelopmentSeeder.PortalRedirectUris[0];
        string authorize = "/connect/authorize?client_id=" + DevelopmentSeeder.PortalClientId
            + "&redirect_uri=" + Uri.EscapeDataString(redirect)
            + "&response_type=code&scope=" + Uri.EscapeDataString("openid profile email")
            + "&state=xyz&code_challenge=" + challenge + "&code_challenge_method=S256";

        (_, string? location, _) = (HttpStatusCode.Found, (await s.FollowAsync(authorize)).Location, string.Empty);

        Assert.StartsWith(redirect, location, StringComparison.Ordinal);
        Assert.Contains("code=", location, StringComparison.Ordinal);
        using IServiceScope scope = _factory.Services.CreateScope();
        Sangam.Identity.Infrastructure.Persistence.SangamDbContext db = scope.ServiceProvider.GetRequiredService<Sangam.Identity.Infrastructure.Persistence.SangamDbContext>();
        string? metadata = db.AuditEvents.Where(e => e.Action == Sangam.Identity.Domain.AuditActions.ConsentGrant).OrderByDescending(e => e.Id).Select(e => e.Metadata).FirstOrDefault();
        Assert.NotNull(metadata);
        Assert.Contains("first_party_implicit", metadata, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task AFormPostAnswer_UsesOnlyTheScriptTheContentSecurityPolicyAllows()
    {
        // R7: ASP.NET Core's OpenID Connect handler asks for response_mode=form_post. OpenIddict answers with a page whose
        // inline script submits the form; the identity server's policy allows exactly that script by its hash. Should an
        // OpenIddict upgrade change the script, this fails before a person is left on a blank page.
        using BrowserSession s = new(_factory);
        (_, string challenge) = Pkce();
        await RegisterAndVerifyAsync(s);
        string redirect = DevelopmentSeeder.PortalRedirectUris[0];
        string authorize = "/connect/authorize?client_id=" + DevelopmentSeeder.PortalClientId
            + "&redirect_uri=" + Uri.EscapeDataString(redirect)
            + "&response_type=code&response_mode=form_post&scope=" + Uri.EscapeDataString("openid profile email")
            + "&state=xyz&code_challenge=" + challenge + "&code_challenge_method=S256";

        (HttpStatusCode status, _, string html) = await s.FollowAsync(authorize);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("action=\"" + redirect + "\"", html, StringComparison.Ordinal);
        string[] scripts = [.. System.Text.RegularExpressions.Regex.Matches(html, "<script>(.*?)</script>", System.Text.RegularExpressions.RegexOptions.Singleline).Select(m => m.Groups[1].Value)];
        string script = Assert.Single(scripts);
        string source = "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script))) + "'";
        Assert.Equal(Sangam.Web.Shared.Hosting.SecurityHeaders.FormPostScriptSource, source);
    }

    private static async Task<(Guid UserId, string RefreshToken)> RefreshTokenAsync(BrowserSession s, HttpClient api)
    {
        (string verifier, string challenge) = Pkce();
        (_, string location, _) = await s.FollowAsync(Authorize(challenge));
        if (location.StartsWith("/consent", StringComparison.Ordinal))
        {
            (_, string? allowed, _) = await s.PostFormAsync(location, [], handler: "Allow");
            location = await FollowToPartnerAsync(s, allowed!);
        }

        string code = HttpUtility.ParseQueryString(new Uri(location).Query)["code"]!;
        JsonElement tokens = await PostTokenAsync(api, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = DevelopmentSeeder.SampleRedirectUri,
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_secret"] = DevelopmentSeeder.SampleClientSecret,
            ["code_verifier"] = verifier,
        });
        Guid userId = Guid.Parse(Payload(tokens.GetProperty("id_token").GetString()!).GetProperty("sub").GetString()!);
        return (userId, tokens.GetProperty("refresh_token").GetString()!);
    }

    private static Task<JsonElement> RefreshAsync(HttpClient api, string refreshToken, bool expectSuccess = true)
        => PostTokenAsync(api, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_secret"] = DevelopmentSeeder.SampleClientSecret,
        }, expectSuccess);

    private async Task<string> EmailOfAsync(Guid userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<Sangam.Identity.Domain.Entities.SangamUser> users = scope.ServiceProvider.GetRequiredService<UserManager<Sangam.Identity.Domain.Entities.SangamUser>>();
        return (await users.FindByIdAsync(userId.ToString("D")))!.Email!;
    }

    private static async Task SignInAsync(BrowserSession s, string email)
    {
        (HttpStatusCode status, string? location, string html) = await s.PostFormAsync("/login", new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = "Kaveri-River-2026!",
        });
        Assert.True(status == HttpStatusCode.Found, html[..Math.Min(400, html.Length)]);
        Assert.NotNull(location);
    }

    private static string Authorize(string challenge)
        => "/connect/authorize?client_id=" + DevelopmentSeeder.SampleClientId
        + "&redirect_uri=" + Uri.EscapeDataString(DevelopmentSeeder.SampleRedirectUri)
        + "&response_type=code&scope=" + Uri.EscapeDataString(Scope)
        + "&state=xyz&code_challenge=" + challenge + "&code_challenge_method=S256";

    private static (string Verifier, string Challenge) Pkce()
    {
        string verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    private static JsonElement Payload(string jwt)
    {
        string payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement;
    }

    private static async Task<JsonElement> PostTokenAsync(HttpClient client, Dictionary<string, string> form, bool expectSuccess = true)
    {
        using FormUrlEncodedContent content = new(form);
        using HttpResponseMessage response = await client.PostAsync(new Uri("/connect/token", UriKind.Relative), content);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode == expectSuccess, body);
        return JsonDocument.Parse(body).RootElement;
    }

    /// <summary>Follows local hops until the response points at the partner's redirect URI.</summary>
    private static async Task<string> FollowToPartnerAsync(BrowserSession s, string location)
    {
        if (!location.StartsWith('/'))
        {
            return location;
        }

        (_, string final, _) = await s.FollowAsync(location);
        return final;
    }

    private static Dictionary<string, string> HiddenFields(string html)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\""))
        {
            if (match.Groups[1].Value != "__RequestVerificationToken")
            {
                fields[match.Groups[1].Value] = System.Net.WebUtility.HtmlDecode(match.Groups[2].Value);
            }
        }

        return fields;
    }

    private async Task<string> RegisterAndVerifyAsync(BrowserSession s)
    {
        string email = $"oidc-{Guid.NewGuid():N}@example.in";
        string mobile = Random.Shared.NextInt64(7000000000, 9999999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        InMemoryEmailOutbox outbox = _factory.Services.GetRequiredService<InMemoryEmailOutbox>();

        (HttpStatusCode status, string? location, string html) = await s.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["FirstName"] = "Meera",
            ["LastName"] = "Iyer",
            ["Email"] = email,
            ["Country"] = "IN",
            ["MobileNumber"] = mobile,
            ["DateOfBirth"] = "1991-11-05",
            ["Gender"] = "female",
            ["Password"] = "Kaveri-River-2026!",
            ["AcceptTerms"] = "true",
        });
        Assert.True(status == HttpStatusCode.Found && location!.StartsWith("/verify", StringComparison.Ordinal), html);

        string code = System.Text.RegularExpressions.Regex.Match(outbox.LatestFor(email)!.Message.TextBody, "[0-9]{6}").Value;
        (_, string? verified, _) = await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = code });
        Assert.StartsWith("/verified", verified, StringComparison.Ordinal);
        return email;
    }
}
