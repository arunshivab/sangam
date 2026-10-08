using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Domain;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Sms;

namespace Sangam.Identity.Server.Tests;

/// <summary>PR-15: verifying the mobile, and having a sign-in code texted instead of e-mailed, over HTTP without JavaScript.</summary>
[Collection("server")]
public sealed partial class SmsScreenTests
{
    private const string Password = "Correct-Horse-2026!";
    private readonly SangamServerFactory _factory;

    public SmsScreenTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task TheMobile_IsVerifiedWithATextedCode()
    {
        using BrowserSession s = new(_factory);
        (string _, string mobile) = await RegisterAsync(s);

        (HttpStatusCode status, string home) = await s.GetAsync("/account");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("href=\"/account/mobile\"", home, StringComparison.Ordinal);

        string page = WebUtility.HtmlDecode((await s.GetAsync("/account/mobile")).Html);
        Assert.Contains("not yet verified", page, StringComparison.Ordinal);
        Assert.Contains("••••••" + mobile[^4..], page, StringComparison.Ordinal);
        Assert.DoesNotContain(mobile, page, StringComparison.Ordinal);

        (_, string? sentLoc, _) = await s.PostFormAsync("/account/mobile", [], handler: "Send");
        Assert.Equal("/account/mobile", sentLoc);
        (_, string afterSend) = await s.GetAsync("/account/mobile");
        Assert.Contains("We texted a 6-digit code", afterSend, StringComparison.Ordinal);

        (HttpStatusCode wrongSt, _, string wrongHtml) = await s.PostFormAsync("/account/mobile", new Dictionary<string, string> { ["Code"] = Wrong(TextedCode(mobile)) }, handler: "Verify");
        Assert.Equal(HttpStatusCode.OK, wrongSt);
        Assert.Contains("not correct", wrongHtml, StringComparison.Ordinal);

        await s.PostFormAsync("/account/mobile", new Dictionary<string, string> { ["Code"] = TextedCode(mobile) }, handler: "Verify");
        (_, string done) = await s.GetAsync("/account/mobile");
        Assert.Contains("Your mobile number is verified", done, StringComparison.Ordinal);
        string homeAfter = WebUtility.HtmlDecode((await s.GetAsync("/account")).Html);
        Assert.Contains("· verified", homeAfter, StringComparison.Ordinal);
        Assert.DoesNotContain("Verify it now", homeAfter, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task AfterThePassword_TheCodeCanBeTexted_ToTheVerifiedMobile()
    {
        using BrowserSession s = new(_factory);
        (string email, string mobile) = await RegisterAsync(s);
        await VerifyMobileAsync(s, mobile);
        await s.PostFormAsync("/account", new Dictionary<string, string> { ["SignInPreference"] = "password_and_otp" }, handler: "Preference");
        await s.PostFormAsync("/logout", []);

        (_, string? loc, _) = await s.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });
        Assert.Equal("/login/verify", loc);
        (_, string emailStep) = await s.GetAsync("/login/verify");
        Assert.Contains("Check your email", emailStep, StringComparison.Ordinal);
        Assert.Contains("Text the code to my mobile instead", emailStep, StringComparison.Ordinal);

        await s.PostFormAsync("/login/verify", [], handler: "Sms");
        string smsStep = WebUtility.HtmlDecode((await s.GetAsync("/login/verify")).Html);
        Assert.Contains("Check your phone", smsStep, StringComparison.Ordinal);
        Assert.Contains("+91 ••••••" + mobile[^2..], smsStep, StringComparison.Ordinal);
        Assert.Contains("Email me the code instead", smsStep, StringComparison.Ordinal);

        // The e-mailed code no longer counts once the person chose a text; the texted one does.
        string emailed = Code(_factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message.TextBody);
        (HttpStatusCode wrongSt, _, _) = await s.PostFormAsync("/login/verify", new Dictionary<string, string> { ["Code"] = emailed == TextedCode(mobile) ? Wrong(emailed) : emailed });
        Assert.Equal(HttpStatusCode.OK, wrongSt);
        (_, string? done, _) = await s.PostFormAsync("/login/verify", new Dictionary<string, string> { ["Code"] = TextedCode(mobile) });
        Assert.Equal("/account", done);

        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        Guid userId = (await db.Users.SingleAsync(u => u.Email == email)).Id;
        string metadata = (await db.AuditEvents.Where(e => e.TargetId == userId && e.Action == AuditActions.UserLoginSuccess).OrderByDescending(e => e.OccurredAt).FirstAsync()).Metadata;
        Assert.Contains("\"channel\": \"sms\"", metadata, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task AfterThePassword_WithoutAVerifiedMobile_ThePageSaysSo_AndKeepsTheEmailedCode()
    {
        using BrowserSession s = new(_factory);
        (string email, _) = await RegisterAsync(s);
        await s.PostFormAsync("/account", new Dictionary<string, string> { ["SignInPreference"] = "password_and_otp" }, handler: "Preference");
        await s.PostFormAsync("/logout", []);
        await s.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });

        await s.PostFormAsync("/login/verify", [], handler: "Sms");
        (_, string page) = await s.GetAsync("/login/verify");
        Assert.Contains("Check your email", page, StringComparison.Ordinal);
        Assert.Contains("no verified mobile number yet", page, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task BeforeAnyPassword_TheTextedCodeScreen_RevealsNothingAboutTheAccount()
    {
        // A real passwordless account with a verified mobile…
        using BrowserSession owner = new(_factory);
        (string email, string mobile) = await RegisterAsync(owner);
        await VerifyMobileAsync(owner, mobile);
        await owner.PostFormAsync("/account", new Dictionary<string, string> { ["SignInPreference"] = "otp_only" }, handler: "Preference");
        await owner.PostFormAsync("/logout", []);
        await owner.PostFormAsync("/login/code", new Dictionary<string, string> { ["Email"] = email });
        await owner.PostFormAsync("/login/verify", [], handler: "Sms");
        string ownerPage = WebUtility.HtmlDecode((await owner.GetAsync("/login/verify")).Html);

        // …and an address with no account at all, get the same screen.
        using BrowserSession stranger = new(_factory);
        await stranger.PostFormAsync("/login/code", new Dictionary<string, string> { ["Email"] = "nobody-" + Guid.NewGuid().ToString("N") + "@example.in" });
        await stranger.PostFormAsync("/login/verify", [], handler: "Sms");
        string strangerPage = WebUtility.HtmlDecode((await stranger.GetAsync("/login/verify")).Html);

        foreach (string page in new[] { ownerPage, strangerPage })
        {
            Assert.Contains("If your account has a verified mobile number, we have texted", page, StringComparison.Ordinal);
            Assert.DoesNotContain("••••••", page, StringComparison.Ordinal);
        }

        (_, string? done, _) = await owner.PostFormAsync("/login/verify", new Dictionary<string, string> { ["Code"] = TextedCode(mobile) });
        Assert.Equal("/account", done);
    }

    [Fact]
    public async Task TheDeliveryReportWebhook_IsOff_WithoutAToken()
    {
        using HttpClient client = _factory.CreateClient();
        using StringContent body = new("{\"provider\":\"outbox\",\"messageId\":\"x\",\"delivered\":true}", System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync(new Uri("/sms/delivery-report", UriKind.Relative), body);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<(string Email, string Mobile)> RegisterAsync(BrowserSession s)
    {
        string email = $"sms-{Guid.NewGuid():N}@example.in";
        string national = Random.Shared.NextInt64(7000000000, 9999999999).ToString(CultureInfo.InvariantCulture);
        (HttpStatusCode st, string? loc, string html) = await s.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["FirstName"] = "Meena",
            ["LastName"] = "Iyer",
            ["Email"] = email,
            ["Country"] = "IN",
            ["MobileNumber"] = national,
            ["BirthDay"] = "9",
            ["BirthMonth"] = "8",
            ["BirthYear"] = "1988",
            ["Gender"] = "female",
            ["Password"] = Password,
            ["AcceptTerms"] = "true",
        });
        Assert.True(st == HttpStatusCode.Found && loc == "/verify", html);
        string code = Code(_factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message.TextBody);
        (_, string? okLoc, _) = await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = code });
        Assert.Equal("/verified", okLoc);
        return (email, "+91" + national);
    }

    private async Task VerifyMobileAsync(BrowserSession s, string mobile)
    {
        await s.PostFormAsync("/account/mobile", [], handler: "Send");
        await s.PostFormAsync("/account/mobile", new Dictionary<string, string> { ["Code"] = TextedCode(mobile) }, handler: "Verify");
    }

    private string TextedCode(string mobile)
        => Code(_factory.Services.GetRequiredService<InMemorySmsOutbox>().LatestFor(mobile)!.Message.Text);

    private static string Wrong(string code) => code == "000000" ? "111111" : "000000";

    private static string Code(string body) => CodeRegex().Match(body).Value;

    [GeneratedRegex("[0-9]{6}")]
    private static partial Regex CodeRegex();
}
