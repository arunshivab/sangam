using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Server.Tests;

/// <summary>D-K: the owner sees a pending support reset at sign-in, and can cancel it there or from the e-mail link.</summary>
[Collection("server")]
public sealed partial class TwoStepResetPagesTests
{
    private const string Password = "Correct-Horse-2026!";
    private readonly SangamServerFactory _factory;

    public TwoStepResetPagesTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task AtSignIn_ThePendingResetIsShown_AndTheOwnerCancelsIt()
    {
        (string email, Guid userId) = await RegisterAsync();
        await PendingAsync(userId);

        using BrowserSession s = new(_factory);
        (_, string? next, _) = await s.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });
        Assert.StartsWith("/account/reset-notice", next, StringComparison.Ordinal);
        (_, string notice) = await s.GetAsync(next!);
        Assert.Contains("This wasn't me — cancel the reset", WebUtility.HtmlDecode(notice), StringComparison.Ordinal);

        (_, _, string done) = await s.PostFormAsync(next!, [], handler: "Cancel");
        Assert.Contains("The reset is cancelled.", WebUtility.HtmlDecode(done), StringComparison.Ordinal);
        Assert.Null(await StateAsync(userId, r => r.CancelledAt == null));
        Assert.Equal("owner", await StateAsync(userId, r => r.CancelledBy != null));
    }

    [PostgresFact]
    public async Task ItWasMe_ContinuesOnce_AndTheNoticeIsNotShownAgain()
    {
        (string email, Guid userId) = await RegisterAsync();
        await PendingAsync(userId);
        using (BrowserSession s = new(_factory))
        {
            (_, string? next, _) = await s.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });
            (_, string? after, _) = await s.PostFormAsync(next!, [], handler: "Continue");
            Assert.False(after!.StartsWith("/account/reset-notice", StringComparison.Ordinal));
        }

        using BrowserSession again = new(_factory);
        (_, string? second, _) = await again.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });
        Assert.False(second!.StartsWith("/account/reset-notice", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task TheEmailLink_ShowsOneButton_AndOnlyThePostCancels()
    {
        (_, Guid userId) = await RegisterAsync();
        string token = await PendingAsync(userId);

        using BrowserSession s = new(_factory);
        (_, string page) = await s.GetAsync("/account/reset/cancel/" + token);
        Assert.Contains("This wasn't me — cancel the reset", WebUtility.HtmlDecode(page), StringComparison.Ordinal);
        Assert.Null(await StateAsync(userId, r => r.CancelledAt != null));

        (_, _, string done) = await s.PostFormAsync("/account/reset/cancel/" + token, []);
        Assert.Contains("The reset is cancelled.", WebUtility.HtmlDecode(done), StringComparison.Ordinal);
        (_, string used) = await s.GetAsync("/account/reset/cancel/" + token);
        Assert.Contains("This link no longer works", WebUtility.HtmlDecode(used), StringComparison.Ordinal);
    }

    private async Task<string> PendingAsync(Guid userId)
    {
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        db.MfaResetRequests.Add(new MfaResetRequest
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RequestedByUserId = userId,
            VerificationMethod = "video_call",
            Reference = "HELP-1",
            RequestedAt = DateTimeOffset.UtcNow,
            EffectiveAt = DateTimeOffset.UtcNow.AddHours(24),
            CancelTokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
        });
        await db.SaveChangesAsync();
        return token;
    }

    private async Task<string?> StateAsync(Guid userId, System.Linq.Expressions.Expression<Func<MfaResetRequest, bool>> filter)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        MfaResetRequest? row = await scope.ServiceProvider.GetRequiredService<SangamDbContext>().MfaResetRequests.Where(r => r.UserId == userId).Where(filter).FirstOrDefaultAsync();
        return row is null ? null : row.CancelledBy ?? "pending";
    }

    private async Task<(string Email, Guid UserId)> RegisterAsync()
    {
        using BrowserSession s = new(_factory);
        string email = $"reset-{Guid.NewGuid():N}@example.in";
        (_, string? loc, string html) = await s.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["FirstName"] = "Asha",
            ["LastName"] = "Menon",
            ["Email"] = email,
            ["Country"] = "IN",
            ["MobileNumber"] = Random.Shared.NextInt64(7000000000, 9999999999).ToString(CultureInfo.InvariantCulture),
            ["BirthDay"] = "5",
            ["BirthMonth"] = "5",
            ["BirthYear"] = "1985",
            ["Gender"] = "female",
            ["Password"] = Password,
            ["AcceptTerms"] = "true",
        });
        Assert.True(loc == "/verify", html);
        string code = CodeRegex().Match(_factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message.TextBody).Value;
        await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = code });
        using IServiceScope scope = _factory.Services.CreateScope();
        Guid id = await scope.ServiceProvider.GetRequiredService<SangamDbContext>().Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        return (email, id);
    }

    [GeneratedRegex("[0-9]{6}")]
    private static partial Regex CodeRegex();
}
