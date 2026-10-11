using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Domain;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Server.Tests;

/// <summary>
/// rc.9 (ASVS V2.2.3): a sign-in from a browser that has not signed in to the account before is told to the person by e-mail,
/// with the browser, the address, the time and a link to the devices page; the same browser is not reported twice.
/// </summary>
[Collection("server")]
public sealed partial class NewDeviceNoticeTests
{
    private const string Subject = "A new sign-in to your Sangam account";
    private const string Password = "Correct-Horse-2026!";

    private readonly SangamServerFactory _factory;

    public NewDeviceNoticeTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task ANewBrowser_IsToldByEmail_Once_AndAudited()
    {
        InMemoryEmailOutbox outbox = _factory.Services.GetRequiredService<InMemoryEmailOutbox>();
        string email = $"device-{Guid.NewGuid():N}@example.in";
        using (BrowserSession first = new(_factory))
        {
            await RegisterAndVerifyAsync(first, outbox, email);
        }

        // Just registered: the person's own first browser is not reported.
        Assert.Equal(0, Notices(outbox, email));

        // A day later, a different browser signs in.
        Guid userId;
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
            userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.CreatedAt, x => x.CreatedAt.AddDays(-1)));
        }

        using BrowserSession other = new(_factory);
        (_, string? to, _) = await other.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });
        Assert.Equal("/account", to);
        Assert.Equal(1, Notices(outbox, email));
        string body = outbox.LatestFor(email)!.Message.TextBody;
        Assert.Contains("Network address:", body, StringComparison.Ordinal);
        Assert.Contains("IST", body, StringComparison.Ordinal);
        Assert.Contains("/devices", body, StringComparison.Ordinal);

        // The same browser again: no second notice.
        await other.PostFormAsync("/logout", []);
        (_, string? again, _) = await other.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });
        Assert.Equal("/account", again);
        Assert.Equal(1, Notices(outbox, email));

        await using AsyncServiceScope check = _factory.Services.CreateAsyncScope();
        SangamDbContext audit = check.ServiceProvider.GetRequiredService<SangamDbContext>();
        Assert.Equal(1, await audit.AuditEvents.CountAsync(e => e.Action == AuditActions.UserLoginNewDevice && e.ActorUserId == userId));
    }

    private static int Notices(InMemoryEmailOutbox outbox, string email)
        => outbox.Recent.Count(m => string.Equals(m.Message.ToEmail, email, StringComparison.OrdinalIgnoreCase) && m.Message.Subject == Subject);

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
            ["Password"] = Password,
            ["AcceptTerms"] = "true",
        });
        Assert.True(st == HttpStatusCode.Found && loc == "/verify", html);
        string code = CodeRegex().Match(outbox.LatestFor(email)!.Message.TextBody).Value;
        (_, string? okLoc, _) = await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = code });
        Assert.Equal("/verified", okLoc);
    }

    [GeneratedRegex("[0-9]{6}")]
    private static partial Regex CodeRegex();
}
