using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Server.Tests;

/// <summary>Screens 1–6 driven over HTTP with cookies and no JavaScript, exactly as a browser would.</summary>
[Collection("server")]
public sealed partial class AuthScreenTests
{
    private readonly SangamServerFactory _factory;

    public AuthScreenTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Screens_RenderWithoutDatabase_AndLoadNothingExternal()
    {
        using BrowserSession s = new(_factory);
        foreach (string path in new[] { "/login", "/register", "/forgot", "/login/code" })
        {
            (HttpStatusCode status, string html) = await s.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("<span class=\"sg-wordmark\">sangam</span>", html, StringComparison.Ordinal);
            Assert.Contains("__RequestVerificationToken", html, StringComparison.Ordinal);
            // Progressive enhancement only, and only from this origin: every screen works without it.
            foreach (Match script in ScriptSrcRegex().Matches(html))
            {
                Assert.StartsWith("/", script.Groups[1].Value, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("href=\"http", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("src=\"http", html, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ProtectedPages_RedirectAnonymousToLogin()
    {
        using BrowserSession s = new(_factory);
        (HttpStatusCode status, _) = await s.GetAsync("/account");
        Assert.Equal(HttpStatusCode.Found, status);
    }

    [Fact]
    public async Task DeveloperPages_AreNotServedOutsideDevelopment()
    {
        using BrowserSession s = new(_factory);
        foreach (string path in new[] { "/dev/outbox", "/dev/callback" })
        {
            (HttpStatusCode status, _) = await s.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, status);
        }
    }

    [Fact]
    public async Task Register_OffersTheDiallingCodeAsTheMobilePrefix_WithIndiaSelected()
    {
        using BrowserSession s = new(_factory);
        (HttpStatusCode status, string html) = await s.GetAsync("/register");

        Assert.Equal(HttpStatusCode.OK, status);

        // The select IS the prefix, so a chosen country and the code shown cannot disagree.
        // Codes are right-aligned with non-breaking spaces (&#xA0;) so the ISO codes form a column.
        Assert.Contains("<option value=\"IN\" selected=\"selected\">&#xA0;&#x2B;91&#xA0;IN</option>", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"CA\">&#xA0;&#xA0;&#x2B;1&#xA0;CA</option>", html, StringComparison.Ordinal);
        Assert.Contains("&#x2B;971&#xA0;AE", html, StringComparison.Ordinal);
        Assert.DoesNotContain("sg-dial\" aria-hidden", html, StringComparison.Ordinal);

        // The five requirement tokens sit beside the verdict; the grey box is gone.
        Assert.DoesNotContain("sg-reqs", html, StringComparison.Ordinal);
        // Razor encodes the "+" in the 8+ token, as it does in the dialling codes.
        foreach (string token in new[] { "8&#x2B;", "upper", "lower", "number", "symbol" })
        {
            Assert.Contains($"data-sg-token=\"{token}\"", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Register_MarksEachPasswordTokenMetOrUnmet()
    {
        using BrowserSession s = new(_factory);
        // "kaveri7" has lower and number, but is short and lacks upper and a symbol.
        (_, _, string html) = await s.PostFormAsync("/register", Form("IN", "9876543210", "kaveri7"));

        Assert.Contains("<span class=\"sg-token sg-token--met\" data-sg-token=\"lower\"", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"sg-token sg-token--met\" data-sg-token=\"number\"", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"sg-token \" data-sg-token=\"8&#x2B;\"", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"sg-token \" data-sg-token=\"upper\"", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"sg-token \" data-sg-token=\"symbol\"", html, StringComparison.Ordinal);
        Assert.Contains("Too weak", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_ValidatesTheNationalNumberLengthForTheChosenCountry()
    {
        using BrowserSession s = new(_factory);
        (_, _, string shortIndian) = await s.PostFormAsync("/register", Form("IN", "98765"));
        Assert.Contains("Enter your 10-digit India mobile number.", shortIndian, StringComparison.Ordinal);

        (_, _, string shortEmirati) = await s.PostFormAsync("/register", Form("AE", "5012345"));
        Assert.Contains("Enter your 9-digit United Arab Emirates mobile number.", shortEmirati, StringComparison.Ordinal);
        Assert.Contains("<option value=\"AE\" selected=\"selected\">", shortEmirati, StringComparison.Ordinal);
    }

    [GeneratedRegex("<script[^>]*src=\"([^\"]+)\"")]
    private static partial Regex ScriptSrcRegex();

    private static Dictionary<string, string> Form(string country, string mobile, string password = "Kaveri-River-2026!") => new(StringComparer.Ordinal)
    {
        ["FirstName"] = "Arun",
        ["LastName"] = "Shiva",
        ["Email"] = $"country-{Guid.NewGuid():N}@example.in",
        ["Country"] = country,
        ["MobileNumber"] = mobile,
        ["DateOfBirth"] = "1980-01-01",
        ["Gender"] = "male",
        ["Password"] = password,
        ["AcceptTerms"] = "true",
    };

    [Fact]
    public async Task Register_ShowsFieldErrors_AndServerRenderedStrengthMeter()
    {
        using BrowserSession s = new(_factory);
        (HttpStatusCode status, _, string html) = await s.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["FirstName"] = "",
            ["LastName"] = "Kumar",
            ["Email"] = "not-an-email",
            ["Country"] = "IN",
            ["MobileNumber"] = "",
            ["DateOfBirth"] = "",
            ["Gender"] = "",
            ["Password"] = "weakpass",
            ["AcceptTerms"] = "false",
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Enter your first name.", html, StringComparison.Ordinal);
        Assert.Contains("Enter a valid email address.", html, StringComparison.Ordinal);
        Assert.Contains("sg-meter--weak", html, StringComparison.Ordinal);
        Assert.Contains("Too weak", html, StringComparison.Ordinal);
        Assert.Contains("You need to accept the terms", html, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task FullJourney_Register_Verify_Account_SignOut_Forgot_Reset_SignIn()
    {
        using BrowserSession s = new(_factory);
        string email = $"journey-{Guid.NewGuid():N}@example.in";
        string mobile = Random.Shared.NextInt64(7000000000, 9999999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        InMemoryEmailOutbox outbox = _factory.Services.GetRequiredService<InMemoryEmailOutbox>();

        // 2. Register → /verify
        (HttpStatusCode st, string? loc, _) = await s.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["FirstName"] = "Rajesh",
            ["LastName"] = "Kumar",
            ["Email"] = email,
            ["Country"] = "IN",
            ["MobileNumber"] = mobile,
            ["DateOfBirth"] = "1984-03-14",
            ["Gender"] = "male",
            ["Password"] = "Correct-Horse-2026!",
            ["AcceptTerms"] = "true",
        });
        Assert.Equal(HttpStatusCode.Found, st);
        Assert.Equal("/verify", loc);

        // 3. Verify page shows the address and a disabled/enabled resend box; wrong code stays on the page
        (HttpStatusCode vs, string verifyHtml) = await s.GetAsync("/verify");
        Assert.Equal(HttpStatusCode.OK, vs);
        Assert.Contains(email, verifyHtml, StringComparison.Ordinal);
        Assert.Contains("Resend", verifyHtml, StringComparison.Ordinal);
        (HttpStatusCode wrongSt, _, string wrongHtml) = await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = "000000" });
        Assert.Equal(HttpStatusCode.OK, wrongSt);
        Assert.Contains("not correct", wrongHtml, StringComparison.Ordinal);

        string code = Code(outbox.LatestFor(email)!.Message.TextBody);
        (HttpStatusCode okSt, string? okLoc, _) = await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = code });
        Assert.Equal(HttpStatusCode.Found, okSt);
        Assert.Equal("/verified", okLoc);

        // 4. Verified → signed in
        (HttpStatusCode accSt, string accHtml) = await s.GetAsync("/account");
        Assert.Equal(HttpStatusCode.OK, accSt);
        Assert.Contains("Namaste, Rajesh", accHtml, StringComparison.Ordinal);
        Assert.Contains("verified", accHtml, StringComparison.Ordinal);

        // Sign out ends the session
        (HttpStatusCode outSt, string? outLoc, _) = await s.PostFormAsync("/logout", []);
        Assert.Equal(HttpStatusCode.Found, outSt);
        Assert.StartsWith("/login", outLoc, StringComparison.Ordinal);
        (HttpStatusCode afterOut, _) = await s.GetAsync("/account");
        Assert.Equal(HttpStatusCode.Found, afterOut);

        // 5. Forgot → /reset regardless of the address
        (HttpStatusCode fSt, string? fLoc, _) = await s.PostFormAsync("/forgot", new Dictionary<string, string> { ["Email"] = email });
        Assert.Equal(HttpStatusCode.Found, fSt);
        Assert.Equal("/reset", fLoc);
        (HttpStatusCode rGet, string resetHtml) = await s.GetAsync("/reset");
        Assert.Equal(HttpStatusCode.OK, rGet);
        Assert.Contains(email, resetHtml, StringComparison.Ordinal);
        Assert.Contains("data-sg-token=\"symbol\"", resetHtml, StringComparison.Ordinal);

        // 6. Reset with the emailed code
        string resetCode = Code(outbox.LatestFor(email)!.Message.TextBody);
        (HttpStatusCode rSt, string? rLoc, _) = await s.PostFormAsync("/reset", new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Code"] = resetCode,
            ["NewPassword"] = "Brand-New-Password-2026!",
            ["ConfirmPassword"] = "Brand-New-Password-2026!",
        });
        Assert.Equal(HttpStatusCode.Found, rSt);
        Assert.StartsWith("/login", rLoc, StringComparison.Ordinal);

        // 1. Sign in with the new password → /account
        (HttpStatusCode lSt, string? lLoc, _) = await s.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = "Brand-New-Password-2026!" });
        Assert.Equal(HttpStatusCode.Found, lSt);
        Assert.Equal("/account", lLoc);
    }

    [PostgresFact]
    public async Task TwoStepAndPasswordlessModes_RouteThroughTheCodeScreen()
    {
        using BrowserSession s = new(_factory);
        string email = $"modes-{Guid.NewGuid():N}@example.in";
        string mobile = Random.Shared.NextInt64(7000000000, 9999999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        InMemoryEmailOutbox outbox = _factory.Services.GetRequiredService<InMemoryEmailOutbox>();
        await RegisterAndVerifyAsync(s, outbox, email, mobile);

        // Set two-step, sign out, sign in → code screen → account
        (HttpStatusCode pSt, _, _) = await s.PostFormAsync("/account", new Dictionary<string, string> { ["SignInPreference"] = "password_and_otp" }, handler: "Preference");
        Assert.Equal(HttpStatusCode.Found, pSt);
        await s.PostFormAsync("/logout", []);

        (_, string? loc, _) = await s.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = "Correct-Horse-2026!" });
        Assert.Equal("/login/verify", loc);
        string code = Code(outbox.LatestFor(email)!.Message.TextBody);
        (_, string? afterCode, _) = await s.PostFormAsync("/login/verify", new Dictionary<string, string> { ["Code"] = code });
        Assert.Equal("/account", afterCode);
        (_, string accHtml) = await s.GetAsync("/account");
        Assert.Contains("password and emailed code", accHtml, StringComparison.Ordinal);

        // Set passwordless, sign out, use /login/code
        await s.PostFormAsync("/account", new Dictionary<string, string> { ["SignInPreference"] = "otp_only" }, handler: "Preference");
        await s.PostFormAsync("/logout", []);
        (_, string? codeLoc, _) = await s.PostFormAsync("/login/code", new Dictionary<string, string> { ["Email"] = email });
        Assert.Equal("/login/verify", codeLoc);
        string signInCode = Code(outbox.LatestFor(email)!.Message.TextBody);
        (_, string? pwlessLoc, _) = await s.PostFormAsync("/login/verify", new Dictionary<string, string> { ["Code"] = signInCode });
        Assert.Equal("/account", pwlessLoc);

        // Unknown address on /login/code gets the identical next screen and a generic failure
        using BrowserSession stranger = new(_factory);
        (_, string? strangerLoc, _) = await stranger.PostFormAsync("/login/code", new Dictionary<string, string> { ["Email"] = "nobody-" + Guid.NewGuid().ToString("N") + "@example.in" });
        Assert.Equal("/login/verify", strangerLoc);
        (HttpStatusCode strSt, _, string strHtml) = await stranger.PostFormAsync("/login/verify", new Dictionary<string, string> { ["Code"] = "123456" });
        Assert.Equal(HttpStatusCode.OK, strSt);
        Assert.Contains("not valid", strHtml, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Login_LocksOutAfterFiveFailures()
    {
        using BrowserSession s = new(_factory);
        string email = $"lock-{Guid.NewGuid():N}@example.in";
        string mobile = Random.Shared.NextInt64(7000000000, 9999999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await RegisterAndVerifyAsync(s, _factory.Services.GetRequiredService<InMemoryEmailOutbox>(), email, mobile);
        await s.PostFormAsync("/logout", []);

        string html = string.Empty;
        for (int i = 0; i < 5; i++)
        {
            (_, _, html) = await s.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = "wrong password!" });
        }

        Assert.Contains("Too many failed attempts", html, StringComparison.Ordinal);
        (HttpStatusCode st, _, string again) = await s.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = "Correct-Horse-2026!" });
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.Contains("Too many failed attempts", again, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RateLimiter_Returns429AfterTheConfiguredPosts()
    {
        using var limited = _factory.WithWebHostBuilder(b => b.UseSetting("Sangam:RateLimit:PostsPerMinute", "3"));
        using HttpClient client = limited.CreateClient();

        List<HttpStatusCode> statuses = [];
        for (int i = 0; i < 5; i++)
        {
            using FormUrlEncodedContent form = new(new Dictionary<string, string> { ["Email"] = "x" });
            using HttpResponseMessage r = await client.PostAsync(new Uri("/forgot", UriKind.Relative), form);
            statuses.Add(r.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    private static async Task RegisterAndVerifyAsync(BrowserSession s, InMemoryEmailOutbox outbox, string email, string mobile)
    {
        (HttpStatusCode st, string? loc, string html) = await s.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["FirstName"] = "Priya",
            ["LastName"] = "Nair",
            ["Email"] = email,
            ["Country"] = "IN",
            ["MobileNumber"] = mobile,
            ["DateOfBirth"] = "1990-07-02",
            ["Gender"] = "female",
            ["Password"] = "Correct-Horse-2026!",
            ["AcceptTerms"] = "true",
        });
        Assert.True(st == HttpStatusCode.Found && loc == "/verify", html);
        string code = Code(outbox.LatestFor(email)!.Message.TextBody);
        (_, string? okLoc, _) = await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = code });
        Assert.Equal("/verified", okLoc);
    }

    private static string Code(string body) => CodeRegex().Match(body).Value;

    [GeneratedRegex("[0-9]{6}")]
    private static partial Regex CodeRegex();
}
