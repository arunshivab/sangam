using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Server.Tests;

/// <summary>
/// rc.11 (founder decision, 13 Oct 2026): sign in with an email, a mobile number or a login ID, typed once on the first
/// screen; the screens after it never say whether an account exists; the regulars of a shared computer can tap their name.
/// </summary>
[Collection("server")]
public sealed partial class SignInIdentifierTests
{
    private const string Password = "Correct-Horse-2026!";
    private readonly SangamServerFactory _factory;

    public SignInIdentifierTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task ALoginId_SignsIn_LikeTheEmail()
    {
        (string email, _) = await RegisterAsync();
        string loginId = "devi.n" + Guid.NewGuid().ToString("N")[..8];
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            Guid id = (await scope.ServiceProvider.GetRequiredService<IAccountService>().FindByEmailAsync(email))!.Id;
            LoginIdCheck set = await scope.ServiceProvider.GetRequiredService<ILoginIdService>().SetAsync(id, loginId.ToUpperInvariant(), null);
            Assert.True(set.Ok, set.Problem);
        }

        using BrowserSession s = new(_factory);
        (_, string? done, string html) = await s.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = loginId, ["Password"] = Password });
        Assert.True(done == "/account", html[..Math.Min(400, html.Length)]);
    }

    [PostgresFact]
    public async Task AVerifiedMobile_SignsIn_AndAnUnverifiedOne_GetsTheWrongPasswordAnswer()
    {
        (string email, string national) = await RegisterAsync();
        using BrowserSession before = new(_factory);
        (_, _, string refused) = await before.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = national, ["Password"] = Password });
        Assert.Contains("That password does not match.", WebUtility.HtmlDecode(refused), StringComparison.Ordinal);

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
            await db.Users.Where(u => u.Email == email).ExecuteUpdateAsync(u => u.SetProperty(x => x.PhoneNumberConfirmed, true));
        }

        using BrowserSession after = new(_factory);
        (_, string? done, _) = await after.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = "+91 " + national, ["Password"] = Password });
        Assert.Equal("/account", done);
    }

    [PostgresFact]
    public async Task HowToSignIn_LooksTheSame_WhetherOrNotTheAccountExists()
    {
        (string email, _) = await RegisterAsync();
        string unknown = $"nobody-{Guid.NewGuid():N}@example.in";

        async Task<string> MethodScreenAsync(string identifier)
        {
            using BrowserSession s = new(_factory);
            (_, string? next, _) = await s.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = identifier });
            Assert.Equal("/login/method", next);
            (_, string html) = await s.GetAsync(next!);
            return TokenRegex().Replace(html.Replace(identifier, "WHO", StringComparison.Ordinal), "TOKEN");
        }

        string real = await MethodScreenAsync(email);
        Assert.Equal(real, await MethodScreenAsync(unknown));
        Assert.Contains("data-method=\"password\"", real, StringComparison.Ordinal);
        Assert.Contains("data-method=\"code\"", real, StringComparison.Ordinal);
        Assert.DoesNotContain("Devi", real, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task OnlyThoseWhoAsk_AreKeptOnTheDevicesList_AndCanRemoveThemselves()
    {
        (string email, _) = await RegisterAsync();
        using BrowserSession s = new(_factory);

        // Not asked: not kept.
        await s.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });
        await s.PostFormAsync("/logout", []);
        (_, string plain) = await s.GetAsync("/login");
        Assert.DoesNotContain("data-panel=\"device-people\"", plain, StringComparison.Ordinal);

        // Asked: kept, with the first name and a masked email only.
        (_, string? done, _) = await s.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password, ["Remember"] = "true" });
        Assert.Equal("/account", done);
        await s.PostFormAsync("/logout", []);
        (_, string listed) = await s.GetAsync("/login");
        listed = WebUtility.HtmlDecode(listed);
        Assert.Contains("data-panel=\"device-people\"", listed, StringComparison.Ordinal);
        Assert.Contains("Devi", listed, StringComparison.Ordinal);
        Assert.Contains(email[..2] + "•••@example.in", listed, StringComparison.Ordinal);
        Assert.DoesNotContain(email, listed, StringComparison.Ordinal);

        // Tapping the name goes straight to how to sign in; the password is still asked.
        string pick = PickRegex().Match(listed).Groups[1].Value;
        (HttpStatusCode pickStatus, string method) = await s.GetAsync("/login/method?pick=" + pick);
        Assert.Equal(HttpStatusCode.OK, pickStatus);
        Assert.Contains("Devi · " + email[..2] + "•••@example.in", WebUtility.HtmlDecode(method), StringComparison.Ordinal);
        (_, string? signedIn, _) = await s.PostFormAsync("/login/password", new Dictionary<string, string> { ["Password"] = Password });
        Assert.Equal("/account", signedIn);
        await s.PostFormAsync("/logout", []);

        // "Remove" takes the name off this browser's list.
        (_, string? back, _) = await s.SubmitAsync("/login/forget", listed, new Dictionary<string, string> { ["key"] = pick });
        Assert.Equal("/login", back);
        (_, string after) = await s.GetAsync("/login");
        Assert.DoesNotContain("data-panel=\"device-people\"", after, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task NothingTyped_GoesBackToTheFirstScreen()
    {
        using BrowserSession s = new(_factory);
        (_, string? method, _) = await s.GetWithLocationAsync("/login/method");
        Assert.Equal("/login", method);
        (_, string? password, _) = await s.GetWithLocationAsync("/login/password");
        Assert.Equal("/login", password);
        (_, string? bad, _) = await s.GetWithLocationAsync("/login/method?pick=nothing");
        Assert.Equal("/login", bad);
    }

    private async Task<(string Email, string National)> RegisterAsync()
    {
        using BrowserSession s = new(_factory);
        string email = $"ident-{Guid.NewGuid():N}@example.in";
        string national = Random.Shared.NextInt64(7000000000, 9999999999).ToString(CultureInfo.InvariantCulture);
        (_, string? location, string html) = await s.PostFormAsync("/register", new Dictionary<string, string>(StringComparer.Ordinal)
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
        });
        Assert.True(location == "/verify", html[..Math.Min(400, html.Length)]);
        string code = CodeRegex().Match(_factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message.TextBody).Value;
        (_, string? ok, _) = await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = code });
        Assert.Equal("/verified", ok);
        return (email, national);
    }

    [GeneratedRegex("value=\"[A-Za-z0-9_\\-]{40,}\"")]
    private static partial Regex TokenRegex();

    [GeneratedRegex("[0-9]{6}")]
    private static partial Regex CodeRegex();

    [GeneratedRegex("/login/method\\?pick=([a-f0-9]+)")]
    private static partial Regex PickRegex();
}
