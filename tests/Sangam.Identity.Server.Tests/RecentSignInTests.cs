using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Server.Tests;

/// <summary>
/// rc.9 (ASVS V3.7.1, V3.3.4): on the identity server's own account pages, a sensitive change needs a sign-in from the last
/// 15 minutes. The host's clock is moved forward to make a sign-in old.
/// </summary>
[Collection("server")]
public sealed partial class RecentSignInTests
{
    private const string Banner = "For your security, sign in again before you make this change.";

    private readonly SangamServerFactory _factory;

    public RecentSignInTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task AnOldSignIn_CannotChangeTheSignInPreference_UntilThePersonSignsInAgain()
    {
        MovableClock clock = new();
        using WebApplicationFactory<Program> host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<TimeProvider>(clock)));
        using BrowserSession s = new(host);
        InMemoryEmailOutbox outbox = host.Services.GetRequiredService<InMemoryEmailOutbox>();
        string email = $"recent-{Guid.NewGuid():N}@example.in";
        await RegisterAndVerifyAsync(s, outbox, email);

        // Just signed in: no banner, and the change goes through.
        (_, string fresh) = await s.GetAsync("/account/passkeys");
        Assert.DoesNotContain(Banner, fresh, StringComparison.Ordinal);
        (HttpStatusCode ok, _, _) = await s.PostFormAsync("/account", new Dictionary<string, string> { ["SignInPreference"] = "password_and_otp" }, handler: "Preference");
        Assert.Equal(HttpStatusCode.Found, ok);

        // Twenty minutes later the same change is refused and the page asks for a fresh sign-in.
        clock.Advance(TimeSpan.FromMinutes(20));
        (_, string stale) = await s.GetAsync("/account/passkeys");
        Assert.Contains(Banner, stale, StringComparison.Ordinal);
        (HttpStatusCode refused, _, string refusedHtml) = await s.PostFormAsync("/account", new Dictionary<string, string> { ["SignInPreference"] = "password" }, handler: "Preference");
        Assert.Equal(HttpStatusCode.OK, refused);
        Assert.Contains(Banner, refusedHtml, StringComparison.Ordinal);
        using (IServiceScope scope = host.Services.CreateScope())
        {
            UserManager<SangamUser> users = scope.ServiceProvider.GetRequiredService<UserManager<SangamUser>>();
            Assert.Equal(SignInMode.PasswordAndOtp, (await users.FindByEmailAsync(email))!.SignInPreference);
        }

        // "Sign in again" ends this browser's sign-in and comes back to the page afterwards.
        (HttpStatusCode again, string? to, _) = await s.PostFormAsync("/account/passkeys", [], handler: "Again");
        Assert.Equal(HttpStatusCode.Redirect, again);
        Assert.Equal("/login?returnUrl=%2Faccount%2Fpasskeys", to);
        (HttpStatusCode signedOut, string? signedOutTo, _) = await s.GetWithLocationAsync("/account");
        Assert.Equal(HttpStatusCode.Redirect, signedOut);
        Assert.Contains("/login", signedOutTo, StringComparison.Ordinal);
    }

    private static async Task RegisterAndVerifyAsync(BrowserSession s, InMemoryEmailOutbox outbox, string email)
    {
        string mobile = Random.Shared.NextInt64(7000000000, 9999999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
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
        string code = CodeRegex().Match(outbox.LatestFor(email)!.Message.TextBody).Value;
        (_, string? okLoc, _) = await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = code });
        Assert.Equal("/verified", okLoc);
    }

    [GeneratedRegex("[0-9]{6}")]
    private static partial Regex CodeRegex();

    /// <summary>The real time, plus however far the test has moved it.</summary>
    private sealed class MovableClock : TimeProvider
    {
        private TimeSpan _offset;

        public void Advance(TimeSpan by) => _offset += by;

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + _offset;
    }
}
