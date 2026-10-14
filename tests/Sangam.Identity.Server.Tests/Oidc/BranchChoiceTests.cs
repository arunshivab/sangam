using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Server.Tests.Oidc;

/// <summary>
/// rc.12 (A6, A7): a person with two or more organisations in an application chooses where they are working, the token
/// carries that one, the choice can be remembered on the device, and the application can ask again or name one.
/// </summary>
[Collection("server")]
public sealed partial class BranchChoiceTests
{
    private readonly SangamServerFactory _factory;

    public BranchChoiceTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task APersonAtTwoBranches_ChoosesOne_AndTheTokenCarriesIt()
    {
        using BrowserSession s = new(_factory);
        string email = await RegisterAndVerifyAsync(s);
        (Guid pune, Guid mumbai) = await PlaceAtTwoBranchesAsync(email);

        // Asked which branch, cards last used first then A to Z.
        (string verifier, string challenge) = Pkce();
        (HttpStatusCode status, string at, string html) = await s.FollowAsync(Authorize(challenge, "openid profile orgs.read"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("/select-org", at, StringComparison.Ordinal);
        Assert.Contains("Working at which branch?", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf("Kauvery Mumbai", StringComparison.Ordinal) < html.IndexOf("Kauvery Pune", StringComparison.Ordinal));

        // A branch the person does not work at is not taken.
        (HttpStatusCode refused, _, _) = await s.PostFormAsync(at, new Dictionary<string, string> { ["OrgId"] = Guid.NewGuid().ToString("D") });
        Assert.Equal(HttpStatusCode.OK, refused);

        (HttpStatusCode chose, string? back, _) = await s.PostFormAsync(at, new Dictionary<string, string> { ["OrgId"] = pune.ToString("D"), ["Remember"] = "true" });
        Assert.Equal(HttpStatusCode.Found, chose);
        (_, string consent, string consentHtml) = await s.FollowAsync(back!);
        Assert.Contains("Working at", WebUtility.HtmlDecode(consentHtml), StringComparison.Ordinal);
        Assert.Contains("Kauvery Pune (doctor)", WebUtility.HtmlDecode(consentHtml), StringComparison.Ordinal);

        JsonElement tokens = await ExchangeAsync(s, consent, verifier);
        JsonElement org = Payload(tokens.GetProperty("id_token").GetString()!).GetProperty("sangam_org");
        Assert.Equal(pune.ToString("D"), org.GetProperty("id").GetString());
        Assert.Equal("doctor", org.GetProperty("roles")[0].GetString());
        Assert.Equal(2, Payload(tokens.GetProperty("access_token").GetString()!).GetProperty("sangam_orgs").GetArrayLength());

        // Remembered on this device: the next sign-in does not ask.
        (_, string c2) = Pkce();
        (_, string again, _) = await s.FollowAsync(Authorize(c2, "openid profile orgs.read"));
        Assert.DoesNotContain("/select-org", again, StringComparison.Ordinal);

        // The application's Switch asks again; and an application may name the branch (a ward computer).
        (_, string asked, _) = await s.FollowAsync(Authorize(c2, "openid profile orgs.read") + "&prompt=select_org");
        Assert.StartsWith("/select-org", asked, StringComparison.Ordinal);
        Assert.Contains("last used", (await s.GetAsync(asked)).Html, StringComparison.Ordinal);
        (string v3, string c3) = Pkce();
        (_, string named, _) = await s.FollowAsync(Authorize(c3, "openid profile orgs.read") + "&prompt=select_org&sangam_org=" + mumbai.ToString("D"));
        Assert.StartsWith("/select-org", named, StringComparison.Ordinal);
        (_, string hinted, _) = await s.FollowAsync(Authorize(c3, "openid profile orgs.read") + "&sangam_org=" + mumbai.ToString("D"));
        JsonElement hintedTokens = await ExchangeAsync(s, hinted, v3);
        Assert.Contains(mumbai.ToString("D"), Payload(hintedTokens.GetProperty("access_token").GetString()!).GetProperty("sangam_org").ToString(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task OnlyTheChosenOrganisation_GoesToAnApplicationThatAsksForItAlone()
    {
        using BrowserSession s = new(_factory);
        string email = await RegisterAndVerifyAsync(s);
        (Guid pune, _) = await PlaceAtTwoBranchesAsync(email);

        (string verifier, string challenge) = Pkce();
        (_, string at, _) = await s.FollowAsync(Authorize(challenge, "openid profile org.current"));
        (_, string? back, _) = await s.PostFormAsync(at, new Dictionary<string, string> { ["OrgId"] = pune.ToString("D") });
        (_, string consent, string consentHtml) = await s.FollowAsync(back!);
        Assert.Contains("Other organisations&#x27; roles", consentHtml, StringComparison.Ordinal);
        JsonElement access = Payload((await ExchangeAsync(s, consent, verifier)).GetProperty("access_token").GetString()!);
        Assert.True(access.TryGetProperty("sangam_org", out _));
        Assert.False(access.TryGetProperty("sangam_orgs", out _));
    }

    private async Task<(Guid Pune, Guid Mumbai)> PlaceAtTwoBranchesAsync(string email)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        Guid app = await db.Apps.Where(a => a.ClientId == DevelopmentSeeder.SampleClientId).Select(a => a.Id).SingleAsync();
        Guid person = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid rootId = Guid.NewGuid();
        Organisation root = new() { Id = rootId, Name = "Kauvery " + rootId.ToString("N")[..4], OrgTypeCode = "hospital", Path = OrganisationPath.ForRoot(rootId), RegisteredViaAppId = app, CreatedAt = now, UpdatedAt = now };
        Guid puneId = Guid.NewGuid();
        Guid mumbaiId = Guid.NewGuid();
        db.Organisations.AddRange(
            root,
            new Organisation { Id = puneId, Name = "Kauvery Pune", OrgTypeCode = "hospital", ParentOrgId = rootId, Path = OrganisationPath.ForChild(root.Path, puneId), Depth = 1, RegisteredViaAppId = app, CreatedAt = now, UpdatedAt = now },
            new Organisation { Id = mumbaiId, Name = "Kauvery Mumbai", OrgTypeCode = "hospital", ParentOrgId = rootId, Path = OrganisationPath.ForChild(root.Path, mumbaiId), Depth = 1, RegisteredViaAppId = app, CreatedAt = now, UpdatedAt = now });
        Role doctor = await db.Roles.FirstOrDefaultAsync(r => r.AppId == app && r.Code == "doctor" && r.OrgId == null)
            ?? db.Roles.Add(new Role { Id = Guid.NewGuid(), AppId = app, Code = "doctor", DisplayName = "Doctor", CreatedAt = now }).Entity;
        db.OrgMemberships.AddRange(
            new OrgMembership { Id = Guid.NewGuid(), AppId = app, OrgId = puneId, UserId = person, RoleId = doctor.Id, GrantedAt = now },
            new OrgMembership { Id = Guid.NewGuid(), AppId = app, OrgId = mumbaiId, UserId = person, GrantedAt = now });
        await db.SaveChangesAsync();
        return (puneId, mumbaiId);
    }

    private async Task<string> RegisterAndVerifyAsync(BrowserSession s)
    {
        string email = $"branch-{Guid.NewGuid():N}@example.in";
        string mobile = Random.Shared.NextInt64(7000000000, 9999999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        InMemoryEmailOutbox outbox = _factory.Services.GetRequiredService<InMemoryEmailOutbox>();
        (HttpStatusCode status, string? location, string html) = await s.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["FirstName"] = "Meera",
            ["LastName"] = "Iyer",
            ["Email"] = email,
            ["Country"] = "IN",
            ["MobileNumber"] = mobile,
            ["DateOfBirth"] = "1991-11-05",
            ["Gender"] = "female",
            ["Password"] = "Kaveri-River-2026!",
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

    private static string Authorize(string challenge, string scope)
        => "/connect/authorize?client_id=" + DevelopmentSeeder.SampleClientId
        + "&redirect_uri=" + Uri.EscapeDataString(DevelopmentSeeder.SampleRedirectUri)
        + "&response_type=code&scope=" + Uri.EscapeDataString(scope)
        + "&state=xyz&code_challenge=" + challenge + "&code_challenge_method=S256";

    private static (string Verifier, string Challenge) Pkce()
    {
        string verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    private static JsonElement Payload(string jwt)
    {
        string payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement;
    }

    [GeneratedRegex("[0-9]{6}")]
    private static partial Regex SixDigits();
}
