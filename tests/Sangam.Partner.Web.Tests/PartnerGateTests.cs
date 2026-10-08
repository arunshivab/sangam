using System.Net;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Partner.Web.Tests;

/// <summary>
/// The partner console's two bars, what each rank is shown, and that one partner never sees
/// another's application. Rendered by the real host; the rules themselves are proved in the
/// service tests, so these check that the pages hand nothing extra to the browser.
/// </summary>
public sealed class PartnerGateTests : IClassFixture<PartnerFactory>
{
    private readonly PartnerFactory _factory;

    public PartnerGateTests(PartnerFactory factory)
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
    public async Task SomeoneWhoAdministersNothing_IsToldPlainly_AndSeesNoData()
    {
        Guid app = await _factory.SeedAppAsync("Hidden HIS");
        Guid patient = await _factory.SeedUserAsync(mfa: true, "Patient");

        string home = await GetAsync(patient, "/");
        Assert.Contains("You do not administer any application", home, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden HIS", home, StringComparison.Ordinal);

        string workspace = await GetAsync(patient, $"/apps/{app:D}");
        Assert.DoesNotContain("Hidden HIS", workspace, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task TheProvisioningTab_IsForTheApplicationsAdministrators()
    {
        // PR-23: SCIM settings and the delivery log, for the application's own administrators only.
        Guid app = await _factory.SeedAppAsync("Scim Lab");
        Guid admin = await _factory.SeedUserAsync(mfa: true, "Provisioner");
        await _factory.MakeAdminAsync(app, admin, AppAdminRole.Admin);
        string tab = System.Net.WebUtility.HtmlDecode(await GetAsync(admin, $"/apps/{app:D}/provisioning"));
        Assert.Contains("data-panel=\"scim-settings\"", tab, StringComparison.Ordinal);
        Assert.Contains("SCIM base address", tab, StringComparison.Ordinal);
        Assert.Contains("Nothing sent yet.", tab, StringComparison.Ordinal);

        Guid stranger = await _factory.SeedUserAsync(mfa: true, "Outsider");
        Assert.DoesNotContain("data-panel=\"scim-settings\"", await GetAsync(stranger, $"/apps/{app:D}/provisioning"), StringComparison.Ordinal);

        // PR-24: the webhooks tab, with every event type offered.
        string hooks = System.Net.WebUtility.HtmlDecode(await GetAsync(admin, $"/apps/{app:D}/webhooks"));
        Assert.Contains("data-panel=\"webhook-endpoints\"", hooks, StringComparison.Ordinal);
        Assert.Contains("membership.granted", hooks, StringComparison.Ordinal);
        Assert.Contains("No endpoints yet.", hooks, StringComparison.Ordinal);
        Assert.DoesNotContain("data-panel=\"webhook-endpoints\"", await GetAsync(stranger, $"/apps/{app:D}/webhooks"), StringComparison.Ordinal);

        // PR-25: attributes and custom claims, with the not-health-data declaration.
        string attributes = System.Net.WebUtility.HtmlDecode(await GetAsync(admin, $"/apps/{app:D}/attributes"));
        Assert.Contains("data-panel=\"attributes\"", attributes, StringComparison.Ordinal);
        Assert.Contains("data-panel=\"claims\"", attributes, StringComparison.Ordinal);
        Assert.Contains("This is not health data", attributes, StringComparison.Ordinal);
        Assert.DoesNotContain("data-panel=\"attributes\"", await GetAsync(stranger, $"/apps/{app:D}/attributes"), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task AnAdministratorWithoutAnAuthenticator_IsSentToEnrol()
    {
        Guid app = await _factory.SeedAppAsync("Enrol HIS");
        Guid owner = await _factory.SeedUserAsync(mfa: false, "Unenrolled");
        await _factory.MakeAdminAsync(app, owner, AppAdminRole.Owner);

        string html = await GetAsync(owner, "/");
        Assert.Contains("Set up two-step sign-in first", html, StringComparison.Ordinal);
        Assert.Contains("https://account.example.invalid/profile", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Enrol HIS", html, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task APartner_SeesOnlyTheirOwnApplication_NeverAnothers()
    {
        Guid mine = await _factory.SeedAppAsync("Mine HIS");
        Guid theirs = await _factory.SeedAppAsync("Theirs Lab");
        Guid admin = await _factory.SeedUserAsync(mfa: true, "Admin");
        await _factory.MakeAdminAsync(mine, admin, AppAdminRole.Admin);

        string home = await GetAsync(admin, "/");
        Assert.Contains("Mine HIS", home, StringComparison.Ordinal);
        Assert.DoesNotContain("Theirs Lab", home, StringComparison.Ordinal);

        // Straight to the other application's address: nothing of it is rendered.
        foreach (string tab in new[] { string.Empty, "/roles", "/admins", "/settings" })
        {
            string html = await GetAsync(admin, $"/apps/{theirs:D}{tab}");
            Assert.Contains("You do not administer that application", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Theirs Lab", html, StringComparison.Ordinal);
        }
    }

    [PostgresFact]
    public async Task AnAdministrator_DoesNotGetTheAdministratorsTab_AnOwnerDoes()
    {
        Guid app = await _factory.SeedAppAsync("Ranks HIS");
        Guid owner = await _factory.SeedUserAsync(mfa: true, "Owner");
        Guid admin = await _factory.SeedUserAsync(mfa: true, "Admin");
        await _factory.MakeAdminAsync(app, owner, AppAdminRole.Owner);
        await _factory.MakeAdminAsync(app, admin, AppAdminRole.Admin);

        string asAdmin = await GetAsync(admin, $"/apps/{app:D}/admins");
        Assert.DoesNotContain(">Administrators<", asAdmin, StringComparison.Ordinal);
        Assert.DoesNotContain("Add an administrator", asAdmin, StringComparison.Ordinal);
        // They land on the organisations tab instead of an error or an empty page.
        Assert.Contains("The organisations using Ranks HIS", asAdmin, StringComparison.Ordinal);

        string asOwner = await GetAsync(owner, $"/apps/{app:D}/admins");
        Assert.Contains(">Administrators<", asOwner, StringComparison.Ordinal);
        Assert.Contains("Add an administrator", asOwner, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Settings_HaveTheSignInPageEditor_AndMessagesHaveTheTemplates()
    {
        Guid app = await _factory.SeedAppAsync("Brand HIS");
        Guid admin = await _factory.SeedUserAsync(mfa: true, "Admin");
        await _factory.MakeAdminAsync(app, admin, AppAdminRole.Admin);

        string settings = System.Net.WebUtility.HtmlDecode(await GetAsync(admin, $"/apps/{app:D}/settings"));
        Assert.Contains("data-panel=\"branding\"", settings, StringComparison.Ordinal);
        Assert.Contains("Accent colour (buttons and links)", settings, StringComparison.Ordinal);

        string messages = System.Net.WebUtility.HtmlDecode(await GetAsync(admin, $"/apps/{app:D}/messages"));
        Assert.Contains("data-panel=\"templates\"", messages, StringComparison.Ordinal);
        Assert.Contains("E-mail: sign-in code", messages, StringComparison.Ordinal);
        Assert.DoesNotContain("two-step sign-in was reset", messages, StringComparison.Ordinal);
        Assert.Contains("data-source=\"default\"", messages, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Settings_OfferOnlyTheRulesAPartnerMaySet()
    {
        Guid app = await _factory.SeedAppAsync("Rules HIS");
        Guid admin = await _factory.SeedUserAsync(mfa: true, "Admin");
        await _factory.MakeAdminAsync(app, admin, AppAdminRole.Admin);

        // Text now comes from the catalogue and is HTML-encoded ("person&#x27;s"); compare what a reader sees.
        string html = System.Net.WebUtility.HtmlDecode(await GetAsync(admin, $"/apps/{app:D}/settings"));
        Assert.Contains("Each person's own choice", html, StringComparison.Ordinal);
        Assert.Contains("Always two-step", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Password only", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Email code only", html, StringComparison.Ordinal);

        // PR-16: passkey only, and the password and second-factor policy, with what is not yet available said plainly.
        Assert.Contains("Passkey only", html, StringComparison.Ordinal);
        Assert.Contains("Passwords and second factor", html, StringComparison.Ordinal);
        Assert.Contains("Shortest password allowed", html, StringComparison.Ordinal);
        Assert.Contains("Required for this application", html, StringComparison.Ordinal);
        Assert.Contains("not yet switched on for Sangam", html, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Settings_ShowARuleThePlatformSet_AndOfferOnlyToTightenIt()
    {
        Guid app = await _factory.SeedAppAsync("Otp HIS", SignInPolicy.OtpOnly);
        Guid admin = await _factory.SeedUserAsync(mfa: true, "Admin");
        await _factory.MakeAdminAsync(app, admin, AppAdminRole.Admin);

        string html = await GetAsync(admin, $"/apps/{app:D}/settings");
        Assert.Contains("Email code only, no password", html, StringComparison.Ordinal);
        Assert.Contains("set by Sangam", html, StringComparison.Ordinal);
        Assert.Contains("Always two-step", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Each person's own choice", html, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task NoPartnerPage_LoadsAnythingFromAnotherHost()
    {
        Guid app = await _factory.SeedAppAsync("Hosts HIS");
        Guid owner = await _factory.SeedUserAsync(mfa: true, "Owner");
        await _factory.MakeAdminAsync(app, owner, AppAdminRole.Owner);

        foreach (string path in Paths(app))
        {
            // Sangam's own identity server is first-party: the sign-in page preview links there, and a logo is served from there (PR-19).
            string html = (await GetAsync(owner, path)).Replace("=\"https://id.example.invalid/", "=\"/", StringComparison.Ordinal);
            Assert.DoesNotContain("src=\"http", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("href=\"http", html, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Guards the PR-06 lesson: without an interactive render mode every page is static HTML and
    /// no button's click handler ever runs. A live page carries Blazor's server marker in its body.
    /// </summary>
    [PostgresFact]
    public async Task PagesAreInteractive_SoButtonsActuallyWork()
    {
        Guid app = await _factory.SeedAppAsync("Live HIS");
        Guid owner = await _factory.SeedUserAsync(mfa: true, "Live");
        await _factory.MakeAdminAsync(app, owner, AppAdminRole.Owner);

        foreach (string path in Paths(app))
        {
            string html = await GetAsync(owner, path);
            string body = html[html.IndexOf("<body", StringComparison.Ordinal)..];
            Assert.Contains("<!--Blazor:{\"type\":\"server\"", body, StringComparison.Ordinal);
        }
    }

    private static string[] Paths(Guid app)
        => ["/", $"/apps/{app:D}", $"/apps/{app:D}/roles", $"/apps/{app:D}/admins", $"/apps/{app:D}/settings", $"/apps/{app:D}/messages"];

    private async Task<string> GetAsync(Guid userId, string path)
    {
        using HttpClient client = _factory.ClientFor(userId);
        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));
        string html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}: {html[..Math.Min(300, html.Length)]}");
        return html;
    }
}
