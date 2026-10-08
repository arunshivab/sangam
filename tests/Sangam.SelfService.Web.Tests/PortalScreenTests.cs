using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Portal;

namespace Sangam.SelfService.Web.Tests;

/// <summary>The five portal screens, rendered by the real host against a real database.</summary>
public sealed class PortalScreenTests : IClassFixture<PortalFactory>, IAsyncLifetime
{
    private readonly PortalFactory _factory;
    private Guid _appId;
    private Guid _currentSession;
    private Guid _otherSession;

    public PortalScreenTests(PortalFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        if (PortalFactory.HasDatabase)
        {
            (_appId, _currentSession, _otherSession) = await _factory.SeedUserAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task EveryPageRequiresSignIn()
    {
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (string path in new[] { "/", "/apps", "/devices", "/audit", "/profile" })
        {
            using HttpRequestMessage request = new(HttpMethod.Get, path);
            request.Headers.Add("X-Test-Anonymous", "1");
            using HttpResponseMessage response = await client.SendAsync(request);
            Assert.True(response.StatusCode is HttpStatusCode.Found or HttpStatusCode.Unauthorized, $"{path} returned {(int)response.StatusCode}");
        }
    }

    [PostgresFact]
    public async Task Overview_ShowsTheCountersAndTheFourDestinations()
    {
        string html = await GetAsync("/");

        Assert.Contains("Namaste, Ravi", html, StringComparison.Ordinal);
        Assert.Contains("CONNECTED APPS", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Devices and sessions", html, StringComparison.Ordinal);
        Assert.Contains("Audit log", html, StringComparison.Ordinal);
        Assert.Contains("Personal details", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"http", html, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task ConnectedApps_ListsTheAppWithItsScopes()
    {
        string html = await GetAsync("/apps");

        Assert.Contains("LiPi HIS", html, StringComparison.Ordinal);
        Assert.Contains("Hospital information system", html, StringComparison.Ordinal);
        Assert.Contains(">openid<", html, StringComparison.Ordinal);
        Assert.Contains(">profile<", html, StringComparison.Ordinal);
        Assert.Contains(">email<", html, StringComparison.Ordinal);
        Assert.Contains("Revoke", html, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Devices_ShowsTheAppLabelTheBrowserAndTheAddress_AndMarksThisDevice()
    {
        string html = await GetAsync("/devices");

        Assert.Contains("First Floor Radiology", html, StringComparison.Ordinal);
        Assert.Contains("Chrome on Windows", html, StringComparison.Ordinal);
        Assert.Contains("10.4.2.37", html, StringComparison.Ordinal);
        Assert.Contains("Safari on iOS", html, StringComparison.Ordinal);
        Assert.Contains("This device", html, StringComparison.Ordinal);
        Assert.Contains("Sign out of all devices", html, StringComparison.Ordinal);
        Assert.Contains("never guesses a location", html, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Audit_NarratesEventsAndOffersTheRangeSelector()
    {
        string html = await GetAsync("/audit");

        Assert.Contains("You signed in with your password.", html, StringComparison.Ordinal);
        Assert.Contains("30 days", html, StringComparison.Ordinal);
        Assert.Contains("1 year", html, StringComparison.Ordinal);
        Assert.Contains("Everything", html, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Profile_OffersExportAndDeletion_AndNeverShowsACredential()
    {
        string html = await GetAsync("/profile");

        Assert.Contains("Download my data", html, StringComparison.Ordinal);
        Assert.Contains("Delete your account", html, StringComparison.Ordinal);
        Assert.Contains("30 days", html, StringComparison.Ordinal);
        Assert.Contains("How you sign in", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://id.example.invalid/account/passkeys\"", html, StringComparison.Ordinal);
        Assert.Contains("Add a passkey", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://id.example.invalid/account/mobile\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("argon2", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecurityStamp", html, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task ThePortalServiceBehindTheScreens_SeesThisUsersDataOnly()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IPortalService portal = scope.ServiceProvider.GetRequiredService<IPortalService>();

        IReadOnlyList<PortalSession> sessions = await portal.GetSessionsAsync(PortalFactory.UserId, _currentSession);
        Assert.Equal(2, sessions.Count);
        Assert.True(sessions[0].IsCurrent);
        Assert.Equal(_currentSession, sessions[0].Id);

        // Another user's id sees nothing, and cannot revoke this user's session or app.
        Guid stranger = Guid.NewGuid();
        Assert.Empty(await portal.GetSessionsAsync(stranger, null));
        Assert.Empty(await portal.GetLinkedAppsAsync(stranger));
        Assert.False(await portal.RevokeSessionAsync(stranger, _otherSession, null));
        Assert.False(await portal.RevokeAppAsync(stranger, _appId, null));
        Assert.Equal(2, (await portal.GetSessionsAsync(PortalFactory.UserId, _currentSession)).Count);
    }

    /// <summary>
    /// Guards the PR-06 fix: the portal shipped in PR-05 with no interactive render mode, so every
    /// page was static HTML and no button's click handler ran. A live page carries Blazor's server
    /// marker; a static one does not.
    /// </summary>
    [PostgresFact]
    public async Task PagesAreInteractive_SoButtonsActuallyWork()
    {
        foreach (string path in new[] { "/", "/apps", "/devices", "/audit", "/profile" })
        {
            string html = await GetAsync(path);
            // Look only in the body: the <head> outlet is interactive too and carries the same
            // marker, which would let this pass even with the pages themselves static.
            string body = html[html.IndexOf("<body", StringComparison.Ordinal)..];
            Assert.Contains("<!--Blazor:{\"type\":\"server\"", body, StringComparison.Ordinal);
        }
    }

    [PostgresFact]
    public async Task VerifyingWithDigiLocker_TakesTheRecordsValues_LocksThem_AndCanBeRemoved()
    {
        // PR-26, end to end through the portal's endpoints and a stand-in DigiLocker.
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        string profile = await client.GetStringAsync(new Uri("/profile", UriKind.Relative));
        Assert.Contains("data-panel=\"identity\"", profile, StringComparison.Ordinal);
        Assert.Contains("never your Aadhaar number", System.Net.WebUtility.HtmlDecode(profile), StringComparison.Ordinal);

        // Without agreeing, nothing starts; without the antiforgery token, the post is refused.
        using (HttpResponseMessage unagreed = await PostAsync(client, "/verify/digilocker", Token(profile), agree: false))
        {
            Assert.EndsWith("digilocker=agree", unagreed.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        using (HttpResponseMessage forged = await PostAsync(client, "/verify/digilocker", token: null, agree: true))
        {
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        }

        // The state round-trips; a wrong one ends the attempt.
        (string state, string challenge, string redirectUri) = await StartAsync(client, profile);
        string code = FakeDigiLocker.Shared.Issue(challenge, redirectUri, "dl-" + Guid.NewGuid().ToString("N"), "RAVI KUMAR MENON", "12061986", "M");
        Assert.EndsWith("digilocker=expired", await CallbackAsync(client, code, state + "x"), StringComparison.Ordinal);

        (state, challenge, redirectUri) = await StartAsync(client, profile);
        string digiLockerId = "dl-" + Guid.NewGuid().ToString("N");
        code = FakeDigiLocker.Shared.Issue(challenge, redirectUri, digiLockerId, "RAVI KUMAR MENON", "14061986", "M", identityInToken: false);
        Assert.EndsWith("digilocker=verified", await CallbackAsync(client, code, state), StringComparison.Ordinal);

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            Identity.Infrastructure.Persistence.SangamDbContext db = scope.ServiceProvider.GetRequiredService<Identity.Infrastructure.Persistence.SangamDbContext>();
            Identity.Domain.Entities.SangamUser user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == PortalFactory.UserId);
            Assert.Equal(("RAVI KUMAR", "MENON", new DateOnly(1986, 6, 14)), (user.FirstName, user.LastName, user.DateOfBirth));
            Assert.NotNull(user.IdentityVerifiedAt);
            Identity.Domain.Entities.IdentityVerification row = await db.IdentityVerifications.AsNoTracking().SingleAsync(v => v.UserId == PortalFactory.UserId);
            Assert.NotEqual(digiLockerId, row.SubjectHash);
            Assert.DoesNotContain(digiLockerId, row.SubjectHash, StringComparison.Ordinal);
        }

        string verified = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync(new Uri("/profile?digilocker=verified", UriKind.Relative)));
        Assert.Contains("Your identity is verified with DigiLocker.", verified, StringComparison.Ordinal);
        Assert.Contains("Remove the verification", verified, StringComparison.Ordinal);
        Assert.Contains("data-identity=\"verified\"", verified, StringComparison.Ordinal);

        // The profile service refuses a changed name while verified.
        using IServiceScope portalScope = _factory.Services.CreateScope();
        IPortalService portal = portalScope.ServiceProvider.GetRequiredService<IPortalService>();
        ProfileUpdateResult renamed = await portal.UpdateProfileAsync(PortalFactory.UserId, new ProfileUpdate("Ravi", "Menon", "en-IN", Identity.Domain.Enums.Gender.Male, "IN", "9876500001"), null);
        Assert.False(renamed.Succeeded);

        using (HttpResponseMessage removed = await PostAsync(client, "/verify/digilocker/remove", Token(verified), agree: false))
        {
            Assert.EndsWith("digilocker=removed", removed.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        Assert.Contains("Verify with DigiLocker", System.Net.WebUtility.HtmlDecode(await client.GetStringAsync(new Uri("/profile", UriKind.Relative))), StringComparison.Ordinal);
    }

    private static async Task<(string State, string Challenge, string RedirectUri)> StartAsync(HttpClient client, string profile)
    {
        using HttpResponseMessage start = await PostAsync(client, "/verify/digilocker", Token(profile), agree: true);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Uri authorize = start.Headers.Location!;
        Assert.StartsWith("https://digilocker.example.invalid/public/oauth2/1/authorize?", authorize.AbsoluteUri, StringComparison.Ordinal);
        System.Collections.Specialized.NameValueCollection query = System.Web.HttpUtility.ParseQueryString(authorize.Query);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(FakeDigiLocker.ClientId, query["client_id"]);
        return (query["state"]!, query["code_challenge"]!, query["redirect_uri"]!);
    }

    private static async Task<string> CallbackAsync(HttpClient client, string code, string state)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri($"/verify/digilocker/callback?code={code}&state={state}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string? token, bool agree)
    {
        Dictionary<string, string> form = [];
        if (token is not null)
        {
            form["__RequestVerificationToken"] = token;
        }

        if (agree)
        {
            form["agree"] = "true";
        }

        using FormUrlEncodedContent content = new(form);
        return await client.PostAsync(new Uri(path, UriKind.Relative), content);
    }

    private static string Token(string html)
        => System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);

    private async Task<string> GetAsync(string path)
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));
        string html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}: {html[..Math.Min(400, html.Length)]}");
        return html;
    }
}
