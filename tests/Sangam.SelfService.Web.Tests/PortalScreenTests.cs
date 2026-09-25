using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
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

    private async Task<string> GetAsync(string path)
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));
        string html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}: {html[..Math.Min(400, html.Length)]}");
        return html;
    }
}
