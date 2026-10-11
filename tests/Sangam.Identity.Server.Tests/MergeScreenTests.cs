using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Server.Tests;

/// <summary>
/// rc.7 over HTTP: a person merges two of their accounts — signed in to one, then to the other, then choosing the one that
/// stays — and either account's e-mail can cancel it with one click.
/// </summary>
[Collection("server")]
public sealed partial class MergeScreenTests
{
    private const string Password = "Correct-Horse-2026!";
    private readonly SangamServerFactory _factory;

    public MergeScreenTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TheMergePage_NeedsASignIn()
    {
        using BrowserSession s = new(_factory);
        (HttpStatusCode status, string? where, _) = await s.GetWithLocationAsync("/account/merge");
        Assert.Equal(HttpStatusCode.Found, status);
        Assert.Contains("/login", where, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task SignedInToBoth_ThePersonChoosesTheOneThatStays_AndEitherEmailCanCancel()
    {
        using BrowserSession s = new(_factory);
        (Guid first, string firstEmail) = await RegisterAsync(s);

        (_, string start) = await s.GetAsync("/account/merge");
        Assert.Contains("data-merge=\"start\"", start, StringComparison.Ordinal);

        // Starting signs this account out and asks for the other one.
        (HttpStatusCode started, string? toLogin, _) = await s.PostFormAsync("/account/merge", [], "Start");
        Assert.Equal(HttpStatusCode.Found, started);
        Assert.Equal("/login?returnUrl=%2Faccount%2Fmerge", toLogin);

        // Signing in to the same account again is caught.
        (_, string? afterSame, _) = await s.PostFormAsync("/login?returnUrl=%2Faccount%2Fmerge", new Dictionary<string, string> { ["Email"] = firstEmail, ["Password"] = Password });
        Assert.Equal("/account/merge", afterSame);
        (_, string same) = await s.GetAsync("/account/merge");
        Assert.Contains("You signed in to the same account twice", WebUtility.HtmlDecode(same), StringComparison.Ordinal);

        // Start again from the first account, then make (and so sign in to) the second.
        await s.PostFormAsync("/account/merge", [], "Start");
        (Guid second, string secondEmail) = await RegisterAsync(s);
        (_, string preview) = await s.GetAsync("/account/merge");
        Assert.Contains(firstEmail, preview, StringComparison.Ordinal);
        Assert.Contains(secondEmail, preview, StringComparison.Ordinal);
        Assert.Contains("data-merge=\"confirm\"", preview, StringComparison.Ordinal);

        // Without ticking the box nothing happens; with it, the wait starts and the page says so.
        await s.PostFormAsync("/account/merge", new Dictionary<string, string> { ["Survivor"] = "second" }, "Confirm");
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            Assert.False(await scope.ServiceProvider.GetRequiredService<SangamDbContext>().AccountMergeRequests.AnyAsync(r => r.ClosedUserId == first));
        }

        (_, string preview2) = await s.GetAsync("/account/merge");
        Assert.Contains("data-merge=\"confirm\"", preview2, StringComparison.Ordinal);
        (_, string? afterConfirm, _) = await s.PostFormAsync("/account/merge", new Dictionary<string, string> { ["Survivor"] = "second", ["agree"] = "true" }, "Confirm");
        Assert.Equal("/account/merge", afterConfirm);
        (_, string pending) = await s.GetAsync("/account/merge");
        Assert.Contains("data-merge=\"pending\"", pending, StringComparison.Ordinal);

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AccountMergeRequest request = await scope.ServiceProvider.GetRequiredService<SangamDbContext>().AccountMergeRequests.SingleAsync(r => r.ClosedUserId == first);
            Assert.Equal(second, request.SurvivorUserId);
        }

        // The first account's e-mail cancels it, with one button (a mail scanner opening the link changes nothing).
        string mail = _factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(firstEmail)!.Message.TextBody;
        string link = CancelRegex().Match(mail).Value;
        Assert.False(string.IsNullOrEmpty(link));
        using BrowserSession other = new(_factory);
        (_, string cancelPage) = await other.GetAsync(link);
        Assert.Contains("cancel the merge", WebUtility.HtmlDecode(cancelPage), StringComparison.Ordinal);
        (_, _, string cancelled) = await other.PostFormAsync(link, []);
        Assert.Contains("The merge is cancelled", cancelled, StringComparison.Ordinal);
        (_, string again) = await other.GetAsync(link);
        Assert.Contains("This link no longer works", again, StringComparison.Ordinal);
    }

    private async Task<(Guid UserId, string Email)> RegisterAsync(BrowserSession s)
    {
        string email = $"merge-{Guid.NewGuid():N}@example.in";
        string national = Random.Shared.NextInt64(7000000000, 9999999999).ToString(CultureInfo.InvariantCulture);
        (HttpStatusCode st, string? loc, string html) = await s.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["FirstName"] = "Asha",
            ["LastName"] = "Menon",
            ["Email"] = email,
            ["Country"] = "IN",
            ["MobileNumber"] = national,
            ["BirthDay"] = "12",
            ["BirthMonth"] = "4",
            ["BirthYear"] = "1988",
            ["Gender"] = "female",
            ["Password"] = Password,
            ["AcceptTerms"] = "true",
        });
        Assert.True(st == HttpStatusCode.Found && loc == "/verify", html);
        string code = CodeRegex().Match(_factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message.TextBody).Value;
        (_, string? okLoc, _) = await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = code });
        Assert.Equal("/verified", okLoc);

        using IServiceScope scope = _factory.Services.CreateScope();
        Guid id = await scope.ServiceProvider.GetRequiredService<SangamDbContext>().Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        return (id, email);
    }

    [GeneratedRegex("[0-9]{6}")]
    private static partial Regex CodeRegex();

    [GeneratedRegex("/account/merge/cancel/[0-9a-f]{64}")]
    private static partial Regex CancelRegex();
}
