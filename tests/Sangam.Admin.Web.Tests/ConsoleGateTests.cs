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
        Assert.Contains("https://account.example.invalid/profile", html, StringComparison.Ordinal);
        Assert.DoesNotContain("sg-table", html, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task AnEnrolledViewer_SeesUsers_ButNoActionsAndNoOperatorsList()
    {
        Guid viewer = await _factory.SeedAsync(PlatformRole.Viewer, mfa: true, "Viewer");
        Guid someone = await _factory.SeedAsync(role: null, mfa: false, "Someone");

        string users = await GetAsync(viewer, "/");
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

    private async Task<string> GetAsync(Guid userId, string path)
    {
        using HttpClient client = _factory.ClientFor(userId);
        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));
        string html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}: {html[..Math.Min(300, html.Length)]}");
        return html;
    }
}
