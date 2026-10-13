using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Infrastructure;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Web.Shared.Localization;

namespace Sangam.Identity.Server.Tests;

/// <summary>PR-18 (SGM-209): the screens in Hindi and Malayalam, and how a language is chosen.</summary>
[Collection("server")]
public sealed partial class LanguageTests
{
    private const string Password = "Correct-Horse-2026!";
    private readonly SangamServerFactory _factory;

    public LanguageTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("hi-IN", "साइन इन करें")]
    [InlineData("ml-IN", "സൈൻ ഇൻ ചെയ്യുക")]
    public async Task TheSignInScreen_RendersInTheChosenLanguage(string culture, string signIn)
    {
        using HttpClient client = NewClient();
        string html = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri("/login?culture=" + culture, UriKind.Relative)));
        Assert.Contains($"<html lang=\"{culture}\">", html, StringComparison.Ordinal);
        Assert.Contains(signIn, html, StringComparison.Ordinal);
        // rc.11: the one field takes an email, a mobile number or a login ID.
        Assert.Contains(TextCatalogue.Find(culture, "Email, mobile number or login ID")!, html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Email, mobile number or login ID<", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePicker_RemembersTheChoice_AndOnlyGoesBackToSangamItself()
    {
        using HttpClient client = NewClient();
        using HttpResponseMessage switched = await client.GetAsync(new Uri("/culture?c=ml-IN&returnUrl=%2Fforgot", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Redirect, switched.StatusCode);
        Assert.Equal("/forgot", switched.Headers.Location!.ToString());
        Assert.Contains(switched.Headers.GetValues("Set-Cookie"), c => c.StartsWith(SangamLanguages.CookieName + "=", StringComparison.Ordinal) && c.Contains("ml-IN", StringComparison.Ordinal));

        string html = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri("/forgot", UriKind.Relative)));
        Assert.Contains("lang=\"ml-IN\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"true\">മലയാളം", html, StringComparison.Ordinal);

        using HttpResponseMessage away = await client.GetAsync(new Uri("/culture?c=hi-IN&returnUrl=%2F%2Fevil.example%2F", UriKind.Relative));
        Assert.Equal("/", away.Headers.Location!.ToString());

        using HttpResponseMessage unknown = await client.GetAsync(new Uri("/culture?c=fr-FR&returnUrl=%2Flogin", UriKind.Relative));
        Assert.False(unknown.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies) && cookies.Any(c => c.Contains("fr-FR", StringComparison.Ordinal)));
    }

    [PostgresFact]
    public async Task AnApplication_CanOpenSangamInThePersonsLanguage_WithUiLocales()
    {
        using HttpClient client = NewClient();
        string authorize = "/connect/authorize?client_id=" + DevelopmentSeeder.SampleClientId + "&ui_locales=hi%20en";
        string html = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri("/login?returnUrl=" + Uri.EscapeDataString(authorize), UriKind.Relative)));
        Assert.Contains("lang=\"hi-IN\"", html, StringComparison.Ordinal);
        Assert.Contains("साइन इन करें", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePickedLanguage_BeatsWhatTheBrowserAsksFor()
    {
        using HttpClient client = NewClient();
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri("/login?culture=ml-IN", UriKind.Relative));
        request.Headers.AcceptLanguage.ParseAdd("hi-IN");
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Contains("lang=\"ml-IN\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("ml-IN", WebHosting.SupportedCultures);
        Assert.All(SangamLanguages.Offered, l => Assert.Contains(l.Culture, WebHosting.SupportedCultures));
    }

    [PostgresFact]
    public async Task AfterSignIn_TheProfileLanguageApplies_UnlessThePersonPickedOneHere()
    {
        using BrowserSession s = new(_factory);
        string email = await RegisterAsync(s);
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
            await db.Users.Where(u => u.Email == email).ExecuteUpdateAsync(u => u.SetProperty(x => x.Locale, "ml-IN"));
        }

        await s.PostFormAsync("/logout", []);
        (_, string? afterLogin, _) = await s.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });
        Assert.NotNull(afterLogin);
        // rc.10: a signed-in page on the identity server (its own account page is retired).
        (_, string home) = await s.GetAsync("/account/password");
        home = WebUtility.HtmlDecode(home);
        Assert.Contains("lang=\"ml-IN\"", home, StringComparison.Ordinal);
        Assert.Contains("നിങ്ങളുടെ പാസ്‌വേഡ് മാറ്റുക", home, StringComparison.Ordinal);

        (_, string picked) = await s.GetAsync("/account/password?culture=hi-IN");
        Assert.Contains("lang=\"hi-IN\"", picked, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task AServiceMessage_ShownOnAScreen_IsTranslatedToo()
    {
        using BrowserSession s = new(_factory);
        await s.FollowAsync("/culture?c=hi-IN&returnUrl=%2Flogin");
        (_, _, string raw) = await s.PostFormAsync("/login", new Dictionary<string, string>
        {
            ["Email"] = $"nobody-{Guid.NewGuid():N}@example.in",
            ["Password"] = "Wrong-Password-1!",
        });
        string html = WebUtility.HtmlDecode(raw);
        Assert.DoesNotContain("That password does not match. Check what you typed, or use “Forgot your password?”.", html, StringComparison.Ordinal);
        Assert.Contains(TextCatalogue.Find("hi-IN", "That password does not match. Check what you typed, or use “Forgot your password?”.")!, html, StringComparison.Ordinal);
    }

    private HttpClient NewClient()
        => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private async Task<string> RegisterAsync(BrowserSession s)
    {
        string email = $"lang-{Guid.NewGuid():N}@example.in";
        (HttpStatusCode st, string? loc, string html) = await s.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["FirstName"] = "Meera",
            ["LastName"] = "Nair",
            ["Email"] = email,
            ["Country"] = "IN",
            ["MobileNumber"] = Random.Shared.NextInt64(7000000000, 9999999999).ToString(CultureInfo.InvariantCulture),
            ["BirthDay"] = "3",
            ["BirthMonth"] = "11",
            ["BirthYear"] = "1988",
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
}
