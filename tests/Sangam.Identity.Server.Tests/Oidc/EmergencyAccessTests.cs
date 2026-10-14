using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Server.Tests.Oidc;

/// <summary>
/// rc.12 (M12): emergency access. The application asks with its own case reference; the person proves it is them again
/// with an authenticator; gives a reason; the token carries the marker; the reviewers are e-mailed; the application ends it
/// early with a reason. Without a passkey or authenticator, or where it is switched off, Sangam says why.
/// </summary>
[Collection("server")]
public sealed partial class EmergencyAccessTests
{
    private const string Password = "Sahyadri-Hills-2026!";
    private readonly SangamServerFactory _factory;

    public EmergencyAccessTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task AStaffMember_ProvesItIsThemAgain_GivesAReason_AndTheTokenCarriesTheMarker()
    {
        using BrowserSession reviewerBrowser = new(_factory);
        string reviewer = await RegisterAndVerifyAsync(reviewerBrowser, "Sunita");
        using BrowserSession s = new(_factory);
        string nurse = await RegisterAndVerifyAsync(s, "Meera");
        await EnrolAuthenticatorAsync(nurse);
        (Guid app, Guid root, Guid ward) = await SeedAsync(emergencyOn: true);
        await PlaceAsync(app, ward, await UserIdAsync(nurse));
        await ReviewerAsync(app, root, await UserIdAsync(reviewer));

        // The registration sign-in was a password alone: Sangam asks for the authenticator first.
        (string verifier, string challenge) = Pkce();
        string authorize = Authorize(challenge) + "&sangam_emergency=LIPI-48213";
        (_, string signIn, _) = await s.FollowAsync(authorize);
        Assert.StartsWith("/login", signIn, StringComparison.Ordinal);
        string returnUrl = HttpUtility.ParseQueryString(new Uri("https://x" + signIn).Query)["returnUrl"]!;
        Assert.Contains("acr_values=", returnUrl, StringComparison.Ordinal);
        (_, string? mfa, _) = await s.PostFormAsync("/login?returnUrl=" + Uri.EscapeDataString(returnUrl), new Dictionary<string, string> { ["Email"] = nurse, ["Password"] = Password });
        Assert.StartsWith("/login/authenticator", mfa, StringComparison.Ordinal);
        (_, string? back, _) = await s.PostFormAsync(mfa!, new Dictionary<string, string> { ["Code"] = await CodeAsync(nurse) });

        // Now the reason.
        (_, string at, string page) = await s.FollowAsync(back!);
        Assert.StartsWith("/emergency", at, StringComparison.Ordinal);
        Assert.Contains("LIPI-48213", page, StringComparison.Ordinal);
        (_, _, string noReason) = await s.PostFormAsync(at, new Dictionary<string, string> { ["Reason"] = "" });
        Assert.Contains("Give the reason", WebUtility.HtmlDecode(noReason), StringComparison.Ordinal);
        (HttpStatusCode started, string? resume, _) = await s.PostFormAsync(at, new Dictionary<string, string> { ["Reason"] = "Patient deteriorating, treating doctor not reachable" });
        Assert.Equal(HttpStatusCode.Found, started);
        (_, string consent, _) = await s.FollowAsync(resume!);
        JsonElement tokens = await ExchangeAsync(s, consent, verifier);
        JsonElement marker = Payload(tokens.GetProperty("id_token").GetString()!).GetProperty("sangam_emergency");
        Assert.Equal("LIPI-48213", marker.GetProperty("case").GetString());
        Assert.Equal(ward.ToString("D"), marker.GetProperty("org").GetString());
        long until = marker.GetProperty("until").GetInt64();
        Assert.InRange(until - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 3500, 3600);

        // The reviewer is told at once; the e-mail carries the case number and the reason, never patient details.
        InMemoryEmailOutbox outbox = _factory.Services.GetRequiredService<InMemoryEmailOutbox>();
        Assert.Contains("LIPI-48213", outbox.LatestFor(reviewer)!.Message.Subject, StringComparison.Ordinal);

        // The application ends it early, with a reason.
        string access = tokens.GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.BadRequest, await EndAsync(access, ""));
        Assert.Equal(HttpStatusCode.NoContent, await EndAsync(access, "Care handed over"));
        Assert.Equal(HttpStatusCode.NotFound, await EndAsync(access, "Again"));
        using IServiceScope scope = _factory.Services.CreateScope();
        EmergencyAccess use = await scope.ServiceProvider.GetRequiredService<SangamDbContext>().EmergencyAccesses.SingleAsync(e => e.CaseRef == "LIPI-48213" && e.OrgId == ward);
        Assert.Equal("Care handed over", use.EndReason);
        Assert.Equal("totp", use.Method);
    }

    [PostgresFact]
    public async Task WithoutAnAuthenticator_OrWhereItIsOff_SangamSaysWhy()
    {
        using BrowserSession s = new(_factory);
        string nurse = await RegisterAndVerifyAsync(s, "Ravi");
        (Guid app, _, Guid ward) = await SeedAsync(emergencyOn: false);
        await PlaceAsync(app, ward, await UserIdAsync(nurse));

        (_, string challenge) = Pkce();
        (_, string at, string page) = await s.FollowAsync(Authorize(challenge) + "&sangam_emergency=LIPI-1");
        Assert.StartsWith("/emergency", at, StringComparison.Ordinal);
        Assert.Contains("needs a passkey or an authenticator app", WebUtility.HtmlDecode(page), StringComparison.Ordinal);
        Assert.DoesNotContain("Start emergency access", page, StringComparison.Ordinal);

        // Going back without it returns to the ordinary sign-in.
        (HttpStatusCode cancelled, string? plain, _) = await s.PostFormAsync(at, [], handler: "Cancel");
        Assert.Equal(HttpStatusCode.Found, cancelled);
        Assert.DoesNotContain("sangam_emergency", plain, StringComparison.Ordinal);
    }

    private async Task<HttpStatusCode> EndAsync(string accessToken, string reason)
    {
        using HttpClient api = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/api/v1/sessions/emergency/end", UriKind.Relative)) { Content = JsonContent.Create(new { reason }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await api.SendAsync(request);
        return response.StatusCode;
    }

    private async Task EnrolAuthenticatorAsync(string email)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        IMfaService mfa = scope.ServiceProvider.GetRequiredService<IMfaService>();
        UserManager<SangamUser> users = scope.ServiceProvider.GetRequiredService<UserManager<SangamUser>>();
        Guid id = (await accounts.FindByEmailAsync(email))!.Id;
        await mfa.BeginEnrolmentAsync(id);
        SangamUser user = (await users.FindByIdAsync(id.ToString("D")))!;
        Assert.True((await mfa.ConfirmEnrolmentAsync(id, await AuthenticatorApp.CurrentCodeAsync(users, user), null)).Succeeded);
    }

    private async Task<string> CodeAsync(string email)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<SangamUser> users = scope.ServiceProvider.GetRequiredService<UserManager<SangamUser>>();
        return await AuthenticatorApp.CurrentCodeAsync(users, (await users.FindByEmailAsync(email))!, TimeSpan.FromSeconds(30));
    }

    private async Task<(Guid App, Guid Root, Guid Ward)> SeedAsync(bool emergencyOn)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        Guid app = await db.Apps.Where(a => a.ClientId == DevelopmentSeeder.SampleClientId).Select(a => a.Id).SingleAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid rootId = Guid.NewGuid();
        Guid wardId = Guid.NewGuid();
        Organisation root = new() { Id = rootId, Name = "Apulki " + rootId.ToString("N")[..4], OrgTypeCode = "hospital", Path = OrganisationPath.ForRoot(rootId), RegisteredViaAppId = app, CreatedAt = now, UpdatedAt = now, EmergencyAccess = emergencyOn };
        db.Organisations.AddRange(
            root,
            new Organisation { Id = wardId, Name = "ICU", OrgTypeCode = "hospital", ParentOrgId = rootId, Path = OrganisationPath.ForChild(root.Path, wardId), Depth = 1, RegisteredViaAppId = app, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return (app, rootId, wardId);
    }

    private async Task PlaceAsync(Guid app, Guid org, Guid person)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        db.OrgMemberships.Add(new OrgMembership { Id = Guid.NewGuid(), AppId = app, OrgId = org, UserId = person, GrantedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task ReviewerAsync(Guid app, Guid org, Guid person)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        db.OrgMemberships.Add(new OrgMembership { Id = Guid.NewGuid(), AppId = app, OrgId = org, UserId = person, GrantedAt = now });
        db.OrgAdmins.Add(new OrgAdmin { Id = Guid.NewGuid(), AppId = app, OrgId = org, UserId = person, Right = OrgAdminRight.EmergencyReviewer, GrantedAt = now });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> UserIdAsync(string email)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SangamDbContext>().Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
    }

    private async Task<string> RegisterAndVerifyAsync(BrowserSession s, string firstName)
    {
        string email = $"break-{Guid.NewGuid():N}@example.in";
        string mobile = Random.Shared.NextInt64(7000000000, 9999999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        InMemoryEmailOutbox outbox = _factory.Services.GetRequiredService<InMemoryEmailOutbox>();
        (HttpStatusCode status, string? location, string html) = await s.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["FirstName"] = firstName,
            ["LastName"] = "Deshpande",
            ["Email"] = email,
            ["Country"] = "IN",
            ["MobileNumber"] = mobile,
            ["DateOfBirth"] = "1988-07-21",
            ["Gender"] = "female",
            ["Password"] = Password,
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

    private static JsonElement Payload(string jwt)
    {
        string payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement;
    }

    [GeneratedRegex("[0-9]{6}")]
    private static partial Regex SixDigits();
}
