using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Domain;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Sms;

namespace Sangam.Identity.Server.Tests;

/// <summary>
/// D-L (founder's decision on V-09): registration, sign-in and the code flows never reveal whether an address or
/// mobile has an account. Concealment is on by default; the real owner is told instead, a few times a day at most.
/// </summary>
[Collection("server")]
public sealed partial class RegistrationConcealmentTests
{
    private const string Password = "Correct-Horse-2026!";
    private readonly SangamServerFactory _factory;

    public RegistrationConcealmentTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task ByDefault_ATakenAddress_GetsTheSameScreenAsANewOne_AndTheOwnerIsTold()
    {
        (string email, _) = await RegisterNewAsync(_factory);

        // A real, new registration for comparison.
        using BrowserSession fresh = new(_factory);
        string newEmail = $"conceal-new-{Guid.NewGuid():N}@example.in";
        (_, string? freshLoc, _) = await fresh.PostFormAsync("/register", Form(newEmail, Mobile()));
        Assert.Equal("/verify", freshLoc);
        (_, string freshPage) = await fresh.GetAsync("/verify");

        // The same address again, by someone else.
        using BrowserSession prober = new(_factory);
        (HttpStatusCode status, string? location, _) = await prober.PostFormAsync("/register", Form(email, Mobile()));
        Assert.Equal(HttpStatusCode.Found, status);
        Assert.Equal("/verify", location);
        (_, string probePage) = await prober.GetAsync("/verify");
        Assert.Equal(Normalise(freshPage, newEmail), Normalise(probePage, email));

        (HttpStatusCode codeStatus, _, string codeHtml) = await prober.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = "123456" });
        Assert.Equal(HttpStatusCode.OK, codeStatus);
        Assert.Contains("That code is not correct", codeHtml, StringComparison.Ordinal);

        InMemoryEmailOutbox outbox = _factory.Services.GetRequiredService<InMemoryEmailOutbox>();
        Assert.Equal("Someone tried to create a Sangam account with your address", outbox.LatestFor(email)!.Message.Subject);
        Assert.Equal(1, await AccountsWithAsync(email));
    }

    [PostgresFact]
    public async Task ATakenMobile_TextsItsOwner_AndTellsTheNewAddressNothing()
    {
        (_, string national) = await RegisterNewAsync(_factory);
        string e164 = "+91" + national;
        using BrowserSession prober = new(_factory);
        string other = $"conceal-mobile-{Guid.NewGuid():N}@example.in";
        (_, string? location, _) = await prober.PostFormAsync("/register", Form(other, national));
        Assert.Equal("/verify", location);

        Assert.Null(_factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(other));
        Assert.Equal(0, await AccountsWithAsync(other));
        SentSms? text = _factory.Services.GetRequiredService<InMemorySmsOutbox>().LatestFor(e164);
        Assert.NotNull(text);
        Assert.Equal(SmsSettings.RegistrationNoticeTemplate, text.Message.TemplateKey);
        Assert.StartsWith("Someone tried to create a SangamID account with this mobile number.", text.Message.Text, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task WhenSmsIsOff_ATakenMobile_EmailsItsOwner()
    {
        using WebApplicationFactory<Program> noSms = _factory.WithWebHostBuilder(b => b.UseSetting("Sangam:Sms:Enabled", "false"));
        (string email, string national) = await RegisterNewAsync(noSms);
        using BrowserSession prober = new(noSms);
        (_, string? location, _) = await prober.PostFormAsync("/register", Form($"conceal-nosms-{Guid.NewGuid():N}@example.in", national));
        Assert.Equal("/verify", location);
        Assert.Equal("Someone tried to create a Sangam account with your mobile number", noSms.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message.Subject);
    }

    [PostgresFact]
    public async Task AnOwnerIsToldAtMostThreeTimesADay_ButEveryAttemptIsAudited()
    {
        (string email, _) = await RegisterNewAsync(_factory);
        InMemoryEmailOutbox outbox = _factory.Services.GetRequiredService<InMemoryEmailOutbox>();
        int before = outbox.Recent.Count(m => m.Message.ToEmail == email);
        for (int i = 0; i < 5; i++)
        {
            using BrowserSession prober = new(_factory);
            (_, string? location, _) = await prober.PostFormAsync("/register", Form(email, Mobile()));
            Assert.Equal("/verify", location);
        }

        Assert.Equal(3, outbox.Recent.Count(m => m.Message.ToEmail == email) - before);
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        Guid owner = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        Assert.Equal(5, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.UserRegisterDuplicate && e.TargetId == owner));
    }

    [PostgresFact]
    public async Task WhenTurnedOff_TheFormSaysTheAccountExists()
    {
        using WebApplicationFactory<Program> open = _factory.WithWebHostBuilder(b => b.UseSetting("Sangam:Registration:ConcealExistingAccounts", "false"));
        (string email, string national) = await RegisterNewAsync(open);
        using BrowserSession again = new(open);
        (HttpStatusCode status, string? location, string html) = await again.PostFormAsync("/register", Form(email, national));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(location);
        Assert.Contains("An account already exists", html, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task AnAddressWithNoAccount_LocksOutAfterFiveWrongPasswords_LikeARealOne()
    {
        (string real, _) = await RegisterNewAsync(_factory);
        string unknown = $"nobody-{Guid.NewGuid():N}@example.in";
        List<string> realPages = [];
        List<string> unknownPages = [];
        for (int i = 0; i < 6; i++)
        {
            realPages.Add(await WrongPasswordAsync(real));
            unknownPages.Add(await WrongPasswordAsync(unknown));
        }

        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(Message(realPages[i]), Message(unknownPages[i]));
        }

        Assert.Equal("That email and password do not match.", Message(unknownPages[0]));
        Assert.StartsWith("Too many failed attempts", Message(unknownPages[4]), StringComparison.Ordinal);
        Assert.StartsWith("Too many failed attempts", Message(unknownPages[5]), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ForgotPasswordAndCodeSignIn_LookTheSame_ForAnAddressWithNoAccount()
    {
        (string real, _) = await RegisterNewAsync(_factory);
        string unknown = $"nobody-{Guid.NewGuid():N}@example.in";

        async Task<(string? Location, string Page)> ForgotAsync(string email)
        {
            using BrowserSession s = new(_factory);
            (_, string? loc, _) = await s.PostFormAsync("/forgot", new Dictionary<string, string> { ["Email"] = email });
            (_, string page) = await s.GetAsync(loc!);
            return (loc, Normalise(page, email));
        }

        async Task<(string? Location, string Page)> CodeAsync(string email)
        {
            using BrowserSession s = new(_factory);
            (_, string? loc, _) = await s.PostFormAsync("/login/code", new Dictionary<string, string> { ["Email"] = email });
            (_, string page) = await s.GetAsync(loc!);
            return (loc, Normalise(Countdown().Replace(page, "N"), email));
        }

        Assert.Equal(await ForgotAsync(real), await ForgotAsync(unknown));
        Assert.Equal(await CodeAsync(real), await CodeAsync(unknown));
    }

    private async Task<string> WrongPasswordAsync(string email)
    {
        using BrowserSession s = new(_factory);
        (_, _, string html) = await s.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = "Wrong-Horse-2026!" });
        return html;
    }

    private static string Message(string html) => WebUtility.HtmlDecode(ErrorRegex().Match(html).Groups[1].Value).Trim();

    private async Task<int> AccountsWithAsync(string email)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SangamDbContext>().Users.CountAsync(u => u.Email == email);
    }

    private static string Normalise(string html, string email)
        => TokenRegex().Replace(html.Replace(email, "EMAIL", StringComparison.Ordinal), "TOKEN");

    private static async Task<(string Email, string National)> RegisterNewAsync(WebApplicationFactory<Program> factory)
    {
        using BrowserSession s = new(factory);
        string email = $"conceal-{Guid.NewGuid():N}@example.in";
        string national = Mobile();
        (_, string? location, string html) = await s.PostFormAsync("/register", Form(email, national));
        Assert.True(location == "/verify", html);
        string code = CodeRegex().Match(factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message.TextBody).Value;
        (_, string? ok, _) = await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = code });
        Assert.Equal("/verified", ok);
        return (email, national);
    }

    private static string Mobile() => Random.Shared.NextInt64(7000000000, 9999999999).ToString(CultureInfo.InvariantCulture);

    private static Dictionary<string, string> Form(string email, string national) => new(StringComparer.Ordinal)
    {
        ["FirstName"] = "Devi",
        ["LastName"] = "Nair",
        ["Email"] = email,
        ["Country"] = "IN",
        ["MobileNumber"] = national,
        ["BirthDay"] = "14",
        ["BirthMonth"] = "2",
        ["BirthYear"] = "1989",
        ["Gender"] = "female",
        ["Password"] = Password,
        ["AcceptTerms"] = "true",
    };

    [GeneratedRegex("value=\"[A-Za-z0-9_\\-]{40,}\"")]
    private static partial Regex TokenRegex();

    [GeneratedRegex("[0-9]{6}")]
    private static partial Regex CodeRegex();

    [GeneratedRegex("[0-9]+(:[0-9]{2})?")]
    private static partial Regex Countdown();

    [GeneratedRegex("role=\"alert\"[^>]*>(.*?)</", RegexOptions.Singleline)]
    private static partial Regex ErrorRegex();
}
