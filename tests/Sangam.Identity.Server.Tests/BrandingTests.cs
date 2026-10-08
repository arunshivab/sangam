using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Server.Tests;

/// <summary>PR-19 (SGM-209): an application's branding on Sangam's sign-in screens, its logo, and its messages.</summary>
[Collection("server")]
public sealed partial class BrandingTests
{
    private const string Password = "Correct-Horse-2026!";
    private readonly SangamServerFactory _factory;

    public BrandingTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task TheSignInScreen_ShowsTheApplicationsLogoWelcomeAndAccent_AndAnOrganisationsOwn()
    {
        (Guid appId, Guid partner) = await SampleAppWithPartnerAsync();
        Guid org = await AddOrganisationAsync(appId, "Apollo Clinic");
        try
        {
            using IServiceScope scope = _factory.Services.CreateScope();
            ICustomisationService c = scope.ServiceProvider.GetRequiredService<ICustomisationService>();
            Assert.True((await c.SaveBrandingAsync(partner, CustomisationScope.App, appId, new BrandingInput("#1D4E89", new Dictionary<string, string?> { ["en-IN"] = "Welcome to the sample", ["hi-IN"] = "नमूने में आपका स्वागत है" }, "https://help.example.in", null, null), null)).Succeeded);
            Assert.True((await c.SaveLogoAsync(partner, CustomisationScope.App, appId, "image/svg+xml", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"8\" height=\"8\"/></svg>"), null)).Succeeded);
            Assert.True((await c.SaveBrandingAsync(partner, CustomisationScope.Organisation, org, new BrandingInput(null, new Dictionary<string, string?> { ["en-IN"] = "Apollo Clinic staff" }, null, null, null), null)).Succeeded);

            using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            string authorize = "/connect/authorize?client_id=" + DevelopmentSeeder.SampleClientId;
            string html = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri("/login?returnUrl=" + Uri.EscapeDataString(authorize), UriKind.Relative)));
            Assert.Contains("Welcome to the sample", html, StringComparison.Ordinal);
            Assert.Contains("sg-auth--themed", html, StringComparison.Ordinal);
            Assert.Contains("--sg-theme-accent:#1D4E89", html, StringComparison.Ordinal);
            Assert.Contains("https://help.example.in", html, StringComparison.Ordinal);
            string logo = LogoRegex().Match(html).Groups[1].Value;
            Assert.StartsWith("/branding/logo/", logo, StringComparison.Ordinal);

            string hindi = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri("/login?culture=hi-IN&returnUrl=" + Uri.EscapeDataString(authorize), UriKind.Relative)));
            Assert.Contains("नमूने में आपका स्वागत है", hindi, StringComparison.Ordinal);

            string forOrg = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri("/register?returnUrl=" + Uri.EscapeDataString(authorize + "&sangam_org=" + org.ToString("D")), UriKind.Relative)));
            Assert.Contains("Apollo Clinic staff", forOrg, StringComparison.Ordinal);
            Assert.Contains("--sg-theme-accent:#1D4E89", forOrg, StringComparison.Ordinal);

            // The logo is served as a picture only.
            using HttpResponseMessage image = await client.GetAsync(new Uri(logo, UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, image.StatusCode);
            Assert.Equal("image/svg+xml", image.Content.Headers.ContentType!.MediaType);
            Assert.Contains("sandbox", string.Join(' ', image.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
            Assert.Equal("nosniff", string.Join(' ', image.Headers.GetValues("X-Content-Type-Options")));
            using HttpResponseMessage missing = await client.GetAsync(new Uri("/branding/logo/" + Guid.NewGuid().ToString("D"), UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            // Without the application, Sangam's own screen.
            string plain = await client.GetStringAsync(new Uri("/login", UriKind.Relative));
            Assert.DoesNotContain("sg-auth--themed", plain, StringComparison.Ordinal);
        }
        finally
        {
            await ClearAsync(appId, org);
        }
    }

    [PostgresFact]
    public async Task ACodeSentDuringAnApplicationsSignIn_UsesItsWording_AndAHindiAccountGetsHindi()
    {
        (Guid appId, Guid partner) = await SampleAppWithPartnerAsync();
        try
        {
            using (IServiceScope scope = _factory.Services.CreateScope())
            {
                ICustomisationService c = scope.ServiceProvider.GetRequiredService<ICustomisationService>();
                CustomisationResult saved = await c.SaveTemplateAsync(partner, CustomisationScope.App, appId,
                    new TemplateInput(MessageTemplateKinds.SignInCode, "en-IN", "{{code}} for the sample app", "Hi {{name}},\n\n    {{code}}\n\nThe sample app", null), null);
                Assert.True(saved.Succeeded, saved.Message);
            }

            using BrowserSession s = new(_factory);
            string email = await RegisterAsync(s, culture: null);
            using (IServiceScope scope = _factory.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<SangamDbContext>().Users.Where(u => u.Email == email)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.SignInPreference, SignInMode.PasswordAndOtp));
            }

            await s.PostFormAsync("/logout", []);
            string authorize = "/connect/authorize?client_id=" + DevelopmentSeeder.SampleClientId;
            (_, string? next, _) = await s.PostFormAsync("/login?returnUrl=" + Uri.EscapeDataString(authorize), new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });
            Assert.StartsWith("/login/verify", next, StringComparison.Ordinal);
            Application.Abstractions.EmailMessage code = _factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message;
            Assert.EndsWith(" for the sample app", code.Subject, StringComparison.Ordinal);
            Assert.Contains("The sample app", code.HtmlBody!, StringComparison.Ordinal);

            // Someone who registered in Hindi gets Sangam's Hindi text, even from an application with English wording.
            using BrowserSession h = new(_factory);
            string hindiEmail = await RegisterAsync(h, culture: "hi-IN");
            Application.Abstractions.EmailMessage verification = _factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(hindiEmail)!.Message;
            Assert.Contains("lang=\"hi-IN\"", verification.HtmlBody!, StringComparison.Ordinal);
            Assert.Contains("Sangam", verification.TextBody, StringComparison.Ordinal);
            Assert.DoesNotContain("Your Sangam verification code is", verification.TextBody, StringComparison.Ordinal);
        }
        finally
        {
            await ClearAsync(appId, null);
        }
    }

    private async Task<(Guid AppId, Guid Partner)> SampleAppWithPartnerAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        Guid appId = await db.Apps.Where(a => a.ClientId == DevelopmentSeeder.SampleClientId).Select(a => a.Id).SingleAsync();
        Guid partner = await db.Users.OrderBy(u => u.CreatedAt).Select(u => u.Id).FirstAsync();
        if (!await db.AppAdmins.AnyAsync(a => a.AppId == appId && a.UserId == partner && a.RevokedAt == null))
        {
            db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = appId, UserId = partner, Role = AppAdminRole.Owner, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        return (appId, partner);
    }

    private async Task<Guid> AddOrganisationAsync(Guid appId, string name)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        Guid id = Guid.NewGuid();
        db.Organisations.Add(new Organisation { Id = id, Name = name, OrgTypeCode = "clinic", Path = OrganisationPath.ForRoot(id), RegisteredViaAppId = appId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task ClearAsync(Guid appId, Guid? org)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        await db.Customisations.Where(c => c.ScopeId == appId || c.ScopeId == org).ExecuteDeleteAsync();
        await db.MessageTemplates.Where(t => t.ScopeId == appId || t.ScopeId == org).ExecuteDeleteAsync();
        await db.BrandingAssets.Where(a => a.ScopeId == appId || a.ScopeId == org).ExecuteDeleteAsync();
        if (org is Guid o)
        {
            await db.Organisations.Where(x => x.Id == o).ExecuteDeleteAsync();
        }
    }

    private async Task<string> RegisterAsync(BrowserSession s, string? culture)
    {
        if (culture is not null)
        {
            await s.FollowAsync("/culture?c=" + culture + "&returnUrl=%2Fregister");
        }

        string email = $"brand-{Guid.NewGuid():N}@example.in";
        (HttpStatusCode st, string? loc, string html) = await s.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["FirstName"] = "Kavya",
            ["LastName"] = "Iyer",
            ["Email"] = email,
            ["Country"] = "IN",
            ["MobileNumber"] = Random.Shared.NextInt64(7000000000, 9999999999).ToString(CultureInfo.InvariantCulture),
            ["BirthDay"] = "9",
            ["BirthMonth"] = "2",
            ["BirthYear"] = "1990",
            ["Gender"] = "female",
            ["Password"] = Password,
            ["AcceptTerms"] = "true",
        });
        Assert.True(st == HttpStatusCode.Found && loc == "/verify", html);
        string code = CodeRegex().Match(_factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message.TextBody).Value;
        (_, string? ok, _) = await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = code });
        Assert.Equal("/verified", ok);
        return email;
    }

    [GeneratedRegex("[0-9]{6}")]
    private static partial Regex CodeRegex();

    [GeneratedRegex("class=\"sg-partner-logo\" src=\"([^\"]+)\"")]
    private static partial Regex LogoRegex();
}
