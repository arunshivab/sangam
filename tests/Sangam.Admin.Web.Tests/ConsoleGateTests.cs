using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Admin.Web.Tests;

/// <summary>The console's two bars, and what each rank is shown. Rendered by the real host.</summary>
[Collection("admin-db")]
public sealed class ConsoleGateTests : IClassFixture<ConsoleFactory>
{
    private readonly ConsoleFactory _factory;

    public ConsoleGateTests(ConsoleFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Anonymous_IsChallenged_NotShownAPage()
    {
        using HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpResponseMessage response = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Found, $"got {(int)response.StatusCode}");
    }

    [PostgresFact]
    public async Task ASignedInUserWithoutARank_IsToldPlainly_AndSeesNoData()
    {
        Guid patient = await _factory.SeedAsync(role: null, mfa: true, "Patient");
        string html = await GetAsync(patient, "/");

        Assert.Contains("This console is not for you", html, StringComparison.Ordinal);
        Assert.DoesNotContain("sg-table", html, StringComparison.Ordinal);
        Assert.DoesNotContain("@example.in", html, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task AnOperatorWithoutAnAuthenticator_IsSentToEnrol_WhateverTheirRank()
    {
        Guid owner = await _factory.SeedAsync(PlatformRole.Owner, mfa: false, "Unenrolled");
        string html = await GetAsync(owner, "/");

        Assert.Contains("Set up two-step sign-in first", html, StringComparison.Ordinal);
        Assert.Contains("https://account.example.invalid/security", html, StringComparison.Ordinal);
        Assert.DoesNotContain("sg-table", html, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task AnEnrolledViewer_SeesUsers_ButNoActionsAndNoOperatorsList()
    {
        Guid viewer = await _factory.SeedAsync(PlatformRole.Viewer, mfa: true, "Viewer");
        Guid someone = await _factory.SeedAsync(role: null, mfa: false, "Someone");

        string users = await GetAsync(viewer, "/users");
        Assert.Contains("sg-table", users, StringComparison.Ordinal);
        Assert.Contains(">viewer<", users, StringComparison.Ordinal);

        string detail = await GetAsync(viewer, $"/users/{someone:D}");
        Assert.Contains("You opened this record", detail, StringComparison.Ordinal);
        Assert.Contains("can look, but not act", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(">Suspend<", detail, StringComparison.Ordinal);

        string operators = await GetAsync(viewer, "/operators");
        Assert.Contains("Only an owner", operators, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task EveryOperator_SeesTheMonitoringPage_AndOthersDoNot()
    {
        Guid viewer = await _factory.SeedAsync(PlatformRole.Viewer, mfa: true, "Watcher");
        string html = System.Net.WebUtility.HtmlDecode(await GetAsync(viewer, "/monitoring"));
        Assert.Contains("href=\"/monitoring\"", html, StringComparison.Ordinal);
        foreach (string panel in new[] { "health", "last-hour", "hours", "anjal", "breach-list", "storage", "certificates", "grievances", "audit-archive", "backups" })
        {
            Assert.Contains($"data-panel=\"{panel}\"", html, StringComparison.Ordinal);
        }

        Assert.Contains("Hosts and database", html, StringComparison.Ordinal);

        Guid patient = await _factory.SeedAsync(role: null, mfa: true, "Patient");
        Assert.DoesNotContain("data-panel=\"health\"", await GetAsync(patient, "/monitoring"), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task TheGrievanceLog_IsReadByEveryOperator_AndWrittenBySupport()
    {
        Guid viewer = await _factory.SeedAsync(PlatformRole.Viewer, mfa: true, "Reader");
        string viewed = System.Net.WebUtility.HtmlDecode(await GetAsync(viewer, "/grievances"));
        Assert.Contains("href=\"/grievances\"", viewed, StringComparison.Ordinal);
        Assert.Contains("Acknowledge within two working days", viewed, StringComparison.Ordinal);
        Assert.DoesNotContain("data-panel=\"log-grievance\"", viewed, StringComparison.Ordinal);

        Guid support = await _factory.SeedAsync(PlatformRole.Support, mfa: true, "Desk");
        Assert.Contains("data-panel=\"log-grievance\"", await GetAsync(support, "/grievances"), StringComparison.Ordinal);

        Guid patient = await _factory.SeedAsync(role: null, mfa: true, "Outsider");
        Assert.DoesNotContain("Acknowledge within two working days", System.Net.WebUtility.HtmlDecode(await GetAsync(patient, "/grievances")), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task SamlServiceProviders_AreReadByEveryOperator_AndRegisteredByAnAppManager()
    {
        Guid viewer = await _factory.SeedAsync(PlatformRole.Viewer, mfa: true, "Looker");
        string viewed = System.Net.WebUtility.HtmlDecode(await GetAsync(viewer, "/apps/saml"));
        Assert.Contains("Older applications that speak SAML 2.0", viewed, StringComparison.Ordinal);
        Assert.Contains("/saml/metadata", viewed, StringComparison.Ordinal);
        Assert.DoesNotContain("data-panel=\"saml-sp\"", viewed, StringComparison.Ordinal);
        Assert.Contains("href=\"/apps/saml\"", await GetAsync(viewer, "/apps"), StringComparison.Ordinal);

        Guid manager = await _factory.SeedAsync(PlatformRole.AppManager, mfa: true, "Registrar");
        Assert.Contains("data-panel=\"saml-sp\"", await GetAsync(manager, "/apps/saml"), StringComparison.Ordinal);

        Guid patient = await _factory.SeedAsync(role: null, mfa: true, "Stranger");
        Assert.DoesNotContain("Older applications that speak SAML 2.0", System.Net.WebUtility.HtmlDecode(await GetAsync(patient, "/apps/saml")), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Support_SeesTheActions_ButNotDeleteNow()
    {
        Guid support = await _factory.SeedAsync(PlatformRole.Support, mfa: true, "Support");
        Guid someone = await _factory.SeedAsync(role: null, mfa: false, "Someone");

        string detail = await GetAsync(support, $"/users/{someone:D}");
        Assert.Contains(">Suspend<", detail, StringComparison.Ordinal);
        Assert.Contains("Sign out everywhere", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(">Delete now<", detail, StringComparison.Ordinal);

        // rc.6 (SGM-914): there is no support-assisted reset; a person with an authenticator recovers it with DigiLocker.
        Assert.DoesNotContain("Lost authenticator", detail, StringComparison.Ordinal);
        Guid enrolled = await _factory.SeedAsync(role: null, mfa: true, "Enrolled");
        string enrolledDetail = await GetAsync(support, $"/users/{enrolled:D}");
        Assert.Contains("recovers the account themselves with DigiLocker", enrolledDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("Video call", enrolledDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("Request a reset of two-step sign-in", enrolledDetail, StringComparison.Ordinal);

        // The review queue is open to Support.
        string recoveries = await GetAsync(support, "/recoveries");
        Assert.Contains("No recovery is waiting for review.", recoveries, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task AnAppManager_CanChangeApplications_ButCannotActOnUsers()
    {
        Guid manager = await _factory.SeedAsync(PlatformRole.AppManager, mfa: true, "Manager");
        Guid someone = await _factory.SeedAsync(role: null, mfa: false, "Someone");

        string detail = await GetAsync(manager, $"/users/{someone:D}");
        Assert.Contains("can look, but not act", detail, StringComparison.Ordinal);

        string apps = await GetAsync(manager, "/apps");
        Assert.DoesNotContain("can see the registry but not change it", apps, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task AnOwner_SeesTheOperatorsList_AndDeleteNow()
    {
        Guid owner = await _factory.SeedAsync(PlatformRole.Owner, mfa: true, "Owner");
        Guid someone = await _factory.SeedAsync(role: null, mfa: false, "Someone");

        string operators = await GetAsync(owner, "/operators");
        Assert.Contains("Give someone console access", operators, StringComparison.Ordinal);
        Assert.Contains("app manager", operators, StringComparison.Ordinal);

        string detail = await GetAsync(owner, $"/users/{someone:D}");
        Assert.Contains(">Delete now<", detail, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task SangamsOwnApplications_OfferNeitherDisableNorAssignOwner()
    {
        Guid owner = await _factory.SeedAsync(PlatformRole.Owner, mfa: true, "Owner");
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            db.Apps.Add(new App { Id = Guid.NewGuid(), ClientId = $"platform-{suffix}", Slug = $"platform-{suffix}", DisplayName = $"Platform {suffix}", OwnerCompanyName = "imagiQa", IsPlatform = true, CreatedAt = now, UpdatedAt = now });
            db.Apps.Add(new App { Id = Guid.NewGuid(), ClientId = $"partner-{suffix}", Slug = $"partner-{suffix}", DisplayName = $"Partner {suffix}", OwnerCompanyName = "Partner", CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        string html = await GetAsync(owner, "/apps");
        string platformRow = Row(html, $"platform-{suffix}");
        string partnerRow = Row(html, $"partner-{suffix}");

        Assert.Contains(">platform<", platformRow, StringComparison.Ordinal);
        Assert.DoesNotContain("Assign owner", platformRow, StringComparison.Ordinal);
        Assert.DoesNotContain(">Disable<", platformRow, StringComparison.Ordinal);
        Assert.Contains("Assign owner", partnerRow, StringComparison.Ordinal);
        Assert.Contains(">Disable<", partnerRow, StringComparison.Ordinal);
    }

    private static string Row(string html, string clientId)
    {
        int at = html.IndexOf(clientId, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{clientId} not on the page");
        int start = html.LastIndexOf("<tr", at, StringComparison.Ordinal);
        int end = html.IndexOf("</tr>", at, StringComparison.Ordinal);
        return html[start..end];
    }

    [PostgresFact]
    public async Task Defaults_AreEditedFromAppManagerUp_AndOfferTheTextMessages()
    {
        Guid manager = await _factory.SeedAsync(PlatformRole.AppManager, mfa: true, "Manager");
        string html = System.Net.WebUtility.HtmlDecode(await GetAsync(manager, "/customisation"));
        Assert.Contains("data-panel=\"branding\"", html, StringComparison.Ordinal);
        Assert.Contains("Text message: sign-in code", html, StringComparison.Ordinal);
        Assert.Contains("E-mail: notice that two-step sign-in was reset", html, StringComparison.Ordinal);

        Guid viewer = await _factory.SeedAsync(PlatformRole.Viewer, mfa: true, "Viewer");
        string denied = System.Net.WebUtility.HtmlDecode(await GetAsync(viewer, "/customisation"));
        Assert.True(denied.Contains("You cannot change these.", StringComparison.Ordinal), denied[Math.Max(0, denied.IndexOf("sg-main", StringComparison.Ordinal))..][..Math.Min(1500, denied.Length - Math.Max(0, denied.IndexOf("sg-main", StringComparison.Ordinal)))]);
        Assert.DoesNotContain("Save message", denied, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task NoConsolePage_LoadsAnythingFromAnotherHost()
    {
        Guid owner = await _factory.SeedAsync(PlatformRole.Owner, mfa: true, "Owner");
        foreach (string path in new[] { "/", "/apps", "/operators", "/customisation" })
        {
            string html = await GetAsync(owner, path);
            Assert.DoesNotContain("src=\"http", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("href=\"http", html.Replace("href=\"https://account.example.invalid", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Guards the PR-06 fix: without an interactive render mode every page is static HTML and no
    /// button's click handler ever runs — which is exactly how the portal shipped in PR-05. A live
    /// page carries Blazor's server marker; a static one does not.
    /// </summary>
    [PostgresFact]
    public async Task PagesAreInteractive_SoButtonsActuallyWork()
    {
        Guid owner = await _factory.SeedAsync(PlatformRole.Owner, mfa: true, "Live");
        foreach (string path in new[] { "/", "/apps", "/operators", "/customisation" })
        {
            string html = await GetAsync(owner, path);
            // Look only in the body: the <head> outlet is interactive too and carries the same
            // marker, which would let this pass even with the pages themselves static.
            string body = html[html.IndexOf("<body", StringComparison.Ordinal)..];
            Assert.Contains("<!--Blazor:{\"type\":\"server\"", body, StringComparison.Ordinal);
        }
    }

    [PostgresFact]
    public async Task EachRank_StartsWhereItsWorkIs()
    {
        // rc.10 (O1): an owner sees the platform overview; support the work queue; an app manager the applications; a viewer monitoring.
        Guid owner = await _factory.SeedAsync(PlatformRole.Owner, mfa: true, "Home");
        string overview = await GetAsync(owner, "/");
        Assert.Contains("data-panel=\"stats\"", overview, StringComparison.Ordinal);
        Assert.Contains("data-panel=\"attention\"", overview, StringComparison.Ordinal);
        Assert.Contains("data-panel=\"trail\"", overview, StringComparison.Ordinal);

        foreach ((PlatformRole rank, string home) in new[] { (PlatformRole.Support, "/queue"), (PlatformRole.AppManager, "/apps"), (PlatformRole.Viewer, "/monitoring") })
        {
            Guid someone = await _factory.SeedAsync(rank, mfa: true, "Home");
            using HttpClient client = _factory.ClientFor(someone);
            using HttpResponseMessage response = await client.GetAsync(new Uri("/", UriKind.Relative));
            Assert.True(response.StatusCode is HttpStatusCode.Found or HttpStatusCode.Redirect, $"{rank}: {(int)response.StatusCode}");
            Uri location = response.Headers.Location!;
            Assert.Equal(home, location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString);
        }
    }

    [PostgresFact]
    public async Task ASessionNotSignedInWithTwoStep_IsAskedToSignInAgain_AndSeesNoData()
    {
        // rc.10: an authenticator on the account is not enough; this session must have used it (or a passkey).
        Guid owner = await _factory.SeedAsync(PlatformRole.Owner, mfa: true, "Weak");
        using HttpClient client = _factory.ClientFor(owner, "urn:sangam:acr:1");
        string html = await client.GetStringAsync(new Uri("/users", UriKind.Relative));
        Assert.Contains("data-panel=\"two-step-required\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/reauth?returnUrl=%2Fusers\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("sg-table", html, StringComparison.Ordinal);
        Assert.DoesNotContain("@example.in", html, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task TheStrip_SaysWhichEnvironment_AndHowThisSessionSignedIn()
    {
        Guid viewer = await _factory.SeedAsync(PlatformRole.Viewer, mfa: true, "Strip");
        string html = System.Net.WebUtility.HtmlDecode(await GetAsync(viewer, "/monitoring"));
        Assert.Contains("data-panel=\"strip\"", html, StringComparison.Ordinal);
        Assert.Contains("TESTING · India", html, StringComparison.Ordinal);
        Assert.Contains("sg-strip--other", html, StringComparison.Ordinal);
        Assert.Contains("Signed in with two-step sign-in", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/reauth?returnUrl=", html, StringComparison.Ordinal);

        using HttpClient passkey = _factory.ClientFor(viewer, "urn:sangam:acr:3");
        Assert.Contains("Signed in with a passkey", await passkey.GetStringAsync(new Uri("/monitoring", UriKind.Relative)), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task TheSidebar_ShowsEachRankOnlyWhatItCanOpen()
    {
        Guid viewer = await _factory.SeedAsync(PlatformRole.Viewer, mfa: true, "Side");
        string seen = await GetAsync(viewer, "/users");
        Assert.Contains("href=\"/queue\"", seen, StringComparison.Ordinal);
        Assert.Contains("href=\"/customisation\"", seen, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/operators\"", seen, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/recoveries\"", seen, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/audit\"", seen, StringComparison.Ordinal);
        Assert.Contains("open to support and owners", System.Net.WebUtility.HtmlDecode(await GetAsync(viewer, "/audit")), StringComparison.Ordinal);

        Guid owner = await _factory.SeedAsync(PlatformRole.Owner, mfa: true, "Side");
        string all = await GetAsync(owner, "/users");
        Assert.Contains("href=\"/operators\"", all, StringComparison.Ordinal);
        Assert.Contains("href=\"/recoveries\"", all, StringComparison.Ordinal);
        Assert.Contains("Branding &amp; messages", all, StringComparison.Ordinal);
        Assert.Contains("href=\"/audit\"", all, StringComparison.Ordinal);
        string trail = await GetAsync(owner, "/audit");
        Assert.Contains("data-panel=\"filters\"", trail, StringComparison.Ordinal);
        Assert.Contains("data-panel=\"trail\"", trail, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task TheSigningKeysPage_IsReadOnly_AndOpenToEveryRank()
    {
        // rc.11: what the identity server publishes; the key listed first signs new tokens.
        Guid viewer = await _factory.SeedAsync(PlatformRole.Viewer, mfa: true, "Keys");
        string html = System.Net.WebUtility.HtmlDecode(await GetAsync(viewer, "/keys"));
        Assert.Contains("href=\"/keys\"", html, StringComparison.Ordinal);
        Assert.Contains("data-panel=\"rotation\"", html, StringComparison.Ordinal);
        Assert.Contains("CN=Sangam signing 2026", html, StringComparison.Ordinal);
        Assert.Contains("CN=Sangam signing 2025", html, StringComparison.Ordinal);
        Assert.Equal(1, html.Split("Signing new tokens").Length - 1);
        Assert.Contains("Previous: still published", html, StringComparison.Ordinal);
        Assert.Matches("([0-9A-F]{2}:){31}[0-9A-F]{2}", html);
        Assert.DoesNotContain("<form", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<button type=\"submit\"", html, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task TheWorkQueue_AndTheUsersPager_Render()
    {
        Guid support = await _factory.SeedAsync(PlatformRole.Support, mfa: true, "Queue");
        string queue = System.Net.WebUtility.HtmlDecode(await GetAsync(support, "/queue"));
        Assert.Contains("Work queue", queue, StringComparison.Ordinal);
        Assert.Contains("href=\"/queue?kind=recovery\"", queue, StringComparison.Ordinal);
        Assert.Contains("href=\"/queue?kind=grievance\"", queue, StringComparison.Ordinal);

        Guid viewer = await _factory.SeedAsync(PlatformRole.Viewer, mfa: true, "Queue");
        Assert.DoesNotContain("href=\"/queue?kind=recovery\"", await GetAsync(viewer, "/queue"), StringComparison.Ordinal);
        string users = System.Net.WebUtility.HtmlDecode(await GetAsync(viewer, "/users"));
        Assert.Contains("data-panel=\"pager\"", users, StringComparison.Ordinal);
        Assert.Contains("Showing 1–", users, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task TheUserRecord_PutsTheDestructiveActionLast_AndAlone()
    {
        // rc.10 (O3): safest first; "Delete now" stands apart after the others.
        Guid owner = await _factory.SeedAsync(PlatformRole.Owner, mfa: true, "Order");
        Guid someone = await _factory.SeedAsync(role: null, mfa: false, "Someone");
        string html = await GetAsync(owner, $"/users/{someone:D}");
        int signOut = html.IndexOf("Sign out everywhere", StringComparison.Ordinal);
        int hold = html.IndexOf(">Hold deletion<", StringComparison.Ordinal);
        int suspend = html.IndexOf(">Suspend<", StringComparison.Ordinal);
        int delete = html.IndexOf(">Delete now<", StringComparison.Ordinal);
        Assert.True(signOut > 0 && signOut < hold && hold < suspend && suspend < delete, $"{signOut} {hold} {suspend} {delete}");
        Assert.Contains("sg-actions-danger", html, StringComparison.Ordinal);
    }

    private async Task<string> GetAsync(Guid userId, string path)
    {
        using HttpClient client = _factory.ClientFor(userId);
        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));
        string html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}: {html[..Math.Min(300, html.Length)]}");
        return html;
    }
}
