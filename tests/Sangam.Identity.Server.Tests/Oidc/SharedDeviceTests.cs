using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Devices;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Server.Tests.Oidc;

/// <summary>
/// rc.12 (T1): a shared ward device is enrolled from the device itself; the people who used it appear as tiles; an
/// application locks a session with the person's own token; the person comes back with their PIN, and five wrong PINs send
/// them to a full sign-in.
/// </summary>
[Collection("server")]
public sealed partial class SharedDeviceTests
{
    private const string Pin = "482916";
    private readonly SangamServerFactory _factory;

    public SharedDeviceTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task AnEnrolledDevice_ShowsTiles_LocksAndUnlocksWithAPin()
    {
        using BrowserSession s = new(_factory);
        (Guid app, Guid ward) = await SeedWardAsync();

        // The admin enrols this browser from the organisation console's link.
        string admin = await RegisterAndVerifyAsync(s, "Anil");
        Guid adminId = await UserIdAsync(admin);
        await PlaceAsync(app, ward, adminId, admin: true);
        string token = await StartEnrolAsync(app, adminId, ward);
        Assert.Contains("Enrol this device", (await s.GetAsync("/shared-device/enrol?token=" + token)).Html, StringComparison.Ordinal);
        (_, _, string enrolled) = await s.PostFormAsync("/shared-device/enrol?token=" + token, new Dictionary<string, string> { ["Token"] = token });
        Assert.Contains("This device is enrolled", WebUtility.HtmlDecode(enrolled), StringComparison.Ordinal);
        await s.GetAsync("/login?switch=true");

        // A nurse signs in fully here and is offered a PIN.
        string nurse = await RegisterAndVerifyAsync(s, "Meera");
        Guid nurseId = await UserIdAsync(nurse);
        await PlaceAsync(app, ward, nurseId, admin: false);
        (string verifier, string challenge) = Pkce();
        (_, string at, _) = await s.FollowAsync(Authorize(challenge));
        Assert.StartsWith("/shared-device/pin", at, StringComparison.Ordinal);
        (_, _, string weak) = await s.PostFormAsync(at, new Dictionary<string, string> { ["Pin"] = "123456", ["Confirm"] = "123456" });
        Assert.Contains("not all the same", weak, StringComparison.Ordinal);
        (HttpStatusCode set, string? back, _) = await s.PostFormAsync(at, new Dictionary<string, string> { ["Pin"] = Pin, ["Confirm"] = Pin });
        Assert.Equal(HttpStatusCode.Found, set);
        (_, string consent, _) = await s.FollowAsync(back!);
        string access = (await ExchangeAsync(s, consent, verifier)).GetProperty("access_token").GetString()!;

        // The application's idle timer runs out: it locks the session with the nurse's own token.
        Assert.Equal(HttpStatusCode.NoContent, await LockAsync(access));

        // Back at Sangam: the PIN, not a password.
        (_, string c2) = Pkce();
        (_, string locked, string lockedHtml) = await s.FollowAsync(Authorize(c2));
        Assert.StartsWith("/unlock", locked, StringComparison.Ordinal);
        Assert.Contains("Meera", lockedHtml, StringComparison.Ordinal);
        Assert.Contains("Nurse", lockedHtml, StringComparison.Ordinal);
        (_, _, string wrong) = await s.PostFormAsync(locked, new Dictionary<string, string> { ["Pin"] = "000111" });
        Assert.Contains("4 tries left", wrong, StringComparison.Ordinal);
        (HttpStatusCode unlocked, string? resumed, _) = await s.PostFormAsync(locked, new Dictionary<string, string> { ["Pin"] = Pin });
        Assert.Equal(HttpStatusCode.Found, unlocked);
        Assert.StartsWith("/connect/authorize", resumed, StringComparison.Ordinal);
        (_, string afterUnlock, _) = await s.FollowAsync(resumed!);
        Assert.DoesNotContain("/unlock", afterUnlock, StringComparison.Ordinal);
        Assert.DoesNotContain("/login", afterUnlock, StringComparison.Ordinal);

        // Locked again; someone else takes the device. The nurse's tile shows the lock.
        Assert.Equal(HttpStatusCode.NoContent, await LockAsync(access));
        (_, string again, _) = await s.FollowAsync(Authorize(c2));
        (HttpStatusCode other, string? toLogin, _) = await s.PostFormAsync(again, [], handler: "Else");
        Assert.Equal(HttpStatusCode.Found, other);
        Assert.StartsWith("/login", toLogin, StringComparison.Ordinal);
        string loginHtml = WebUtility.HtmlDecode((await s.GetAsync("/login")).Html);
        Assert.Contains("Who’s using this device?", loginHtml, StringComparison.Ordinal);
        Assert.Contains("Locked · PIN", loginHtml, StringComparison.Ordinal);
        // The admin signed in before enrolling, not on the enrolled device, so has no tile.
        Assert.DoesNotContain("Anil", loginHtml, StringComparison.Ordinal);
        Guid tile = await TileAsync(nurseId);

        // Five wrong PINs: only a full sign-in now.
        for (int i = 0; i < 4; i++)
        {
            await s.PostFormAsync("/unlock?tile=" + tile, new Dictionary<string, string> { ["Tile"] = tile.ToString(), ["Pin"] = "000111" });
        }

        (_, _, string cancelled) = await s.PostFormAsync("/unlock?tile=" + tile, new Dictionary<string, string> { ["Tile"] = tile.ToString(), ["Pin"] = "000111" });
        Assert.Contains("after five wrong tries", WebUtility.HtmlDecode(cancelled), StringComparison.Ordinal);
        (_, _, string stillCancelled) = await s.PostFormAsync("/unlock?tile=" + tile, new Dictionary<string, string> { ["Tile"] = tile.ToString(), ["Pin"] = Pin });
        Assert.DoesNotContain("Unlock</button>", stillCancelled, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ALockOnAnOrdinaryComputer_EndsTheSession()
    {
        using BrowserSession s = new(_factory);
        await RegisterAndVerifyAsync(s, "Ravi");
        (string verifier, string challenge) = Pkce();
        (_, string consent, _) = await s.FollowAsync(Authorize(challenge));
        string access = (await ExchangeAsync(s, consent, verifier)).GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, await LockAsync(access));

        (_, string c2) = Pkce();
        (_, string at, _) = await s.FollowAsync(Authorize(c2));
        Assert.StartsWith("/login", at, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, await LockAsync(access));
    }

    private async Task<HttpStatusCode> LockAsync(string accessToken)
    {
        using HttpClient api = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/api/v1/sessions/lock", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await api.SendAsync(request);
        return response.StatusCode;
    }

    private async Task<(Guid App, Guid Ward)> SeedWardAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        Guid app = await db.Apps.Where(a => a.ClientId == DevelopmentSeeder.SampleClientId).Select(a => a.Id).SingleAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid rootId = Guid.NewGuid();
        Guid wardId = Guid.NewGuid();
        Organisation root = new() { Id = rootId, Name = "Sahyadri " + rootId.ToString("N")[..4], OrgTypeCode = "hospital", Path = OrganisationPath.ForRoot(rootId), RegisteredViaAppId = app, CreatedAt = now, UpdatedAt = now };
        db.Organisations.AddRange(
            root,
            new Organisation { Id = wardId, Name = "Ward 4B", OrgTypeCode = "hospital", ParentOrgId = rootId, Path = OrganisationPath.ForChild(root.Path, wardId), Depth = 1, RegisteredViaAppId = app, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return (app, wardId);
    }

    private async Task PlaceAsync(Guid app, Guid ward, Guid person, bool admin)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Role nurse = await db.Roles.FirstOrDefaultAsync(r => r.AppId == app && r.Code == "nurse" && r.OrgId == null)
            ?? db.Roles.Add(new Role { Id = Guid.NewGuid(), AppId = app, Code = "nurse", DisplayName = "Nurse", CreatedAt = now }).Entity;
        db.OrgMemberships.Add(new OrgMembership { Id = Guid.NewGuid(), AppId = app, OrgId = ward, UserId = person, RoleId = admin ? null : nurse.Id, GrantedAt = now });
        if (admin)
        {
            db.OrgAdmins.Add(new OrgAdmin { Id = Guid.NewGuid(), AppId = app, OrgId = ward, UserId = person, Right = OrgAdminRight.Admin, GrantedAt = now });
        }

        await db.SaveChangesAsync();
    }

    private async Task<string> StartEnrolAsync(Guid app, Guid admin, Guid ward)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        DeviceEnrolStart start = await scope.ServiceProvider.GetRequiredService<ISharedDeviceService>().StartEnrolAsync(app, admin, ward, "Ward 4B · Nursing station");
        Assert.Equal(DeviceEnrolStatus.Started, start.Status);
        return start.Token!;
    }

    private async Task<Guid> UserIdAsync(string email)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SangamDbContext>().Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
    }

    private async Task<Guid> TileAsync(Guid userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SangamDbContext>().SharedDevicePeople.Where(p => p.UserId == userId).Select(p => p.Id).SingleAsync();
    }

    private async Task<string> RegisterAndVerifyAsync(BrowserSession s, string firstName)
    {
        string email = $"ward-{Guid.NewGuid():N}@example.in";
        string mobile = Random.Shared.NextInt64(7000000000, 9999999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        InMemoryEmailOutbox outbox = _factory.Services.GetRequiredService<InMemoryEmailOutbox>();
        (HttpStatusCode status, string? location, string html) = await s.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["FirstName"] = firstName,
            ["LastName"] = "Kulkarni",
            ["Email"] = email,
            ["Country"] = "IN",
            ["MobileNumber"] = mobile,
            ["DateOfBirth"] = "1990-03-12",
            ["Gender"] = "female",
            ["Password"] = "Sahyadri-Hills-2026!",
            ["AcceptTerms"] = "true",
        });
        Assert.True(status == HttpStatusCode.Found && location!.StartsWith("/verify", StringComparison.Ordinal), html);
        string code = SixDigits().Match(outbox.LatestFor(email)!.Message.TextBody).Value;
        (_, string? verified, _) = await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = code });
        Assert.StartsWith("/verified", verified, StringComparison.Ordinal);
        return email;
    }

    private async Task<JsonElement> ExchangeAsync(BrowserSession s, string consentPath, string verifier)
    {
        string redirect = consentPath;
        if (consentPath.StartsWith("/consent", StringComparison.Ordinal))
        {
            (HttpStatusCode allowStatus, string? allowLocation, _) = await s.PostFormAsync(consentPath, [], handler: "Allow");
            Assert.Equal(HttpStatusCode.Found, allowStatus);
            redirect = allowLocation!;
        }

        if (redirect.StartsWith('/'))
        {
            (_, redirect, _) = await s.FollowAsync(redirect);
        }

        string code = HttpUtility.ParseQueryString(new Uri(redirect).Query)["code"]!;
        using HttpClient api = _factory.CreateClient();
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = DevelopmentSeeder.SampleRedirectUri,
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_secret"] = DevelopmentSeeder.SampleClientSecret,
            ["code_verifier"] = verifier,
        });
        using HttpResponseMessage response = await api.PostAsync(new Uri("/connect/token", UriKind.Relative), form);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static string Authorize(string challenge)
        => "/connect/authorize?client_id=" + DevelopmentSeeder.SampleClientId
        + "&redirect_uri=" + Uri.EscapeDataString(DevelopmentSeeder.SampleRedirectUri)
        + "&response_type=code&scope=" + Uri.EscapeDataString("openid profile")
        + "&state=xyz&code_challenge=" + challenge + "&code_challenge_method=S256";

    private static (string Verifier, string Challenge) Pkce()
    {
        string verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    [GeneratedRegex("[0-9]{6}")]
    private static partial Regex SixDigits();
}
