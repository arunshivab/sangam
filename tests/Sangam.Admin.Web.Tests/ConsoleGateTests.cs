using System.Net;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Admin.Web.Tests;

/// <summary>The console's two bars, and what each rank is shown. Rendered by the real host.</summary>
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
    public async Task Support_SeesTheActions_ButNotDeleteNow()
    {
        Guid support = await _factory.SeedAsync(PlatformRole.Support, mfa: true, "Support");
        Guid someone = await _factory.SeedAsync(role: null, mfa: false, "Someone");

        string detail = await GetAsync(support, $"/users/{someone:D}");
        Assert.Contains(">Suspend<", detail, StringComparison.Ordinal);
        Assert.Contains("Sign out everywhere", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(">Delete now<", detail, StringComparison.Ordinal);
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
    public async Task NoConsolePage_LoadsAnythingFromAnotherHost()
    {
        Guid owner = await _factory.SeedAsync(PlatformRole.Owner, mfa: true, "Owner");
        foreach (string path in new[] { "/", "/apps", "/operators" })
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
        foreach (string path in new[] { "/", "/apps", "/operators" })
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
