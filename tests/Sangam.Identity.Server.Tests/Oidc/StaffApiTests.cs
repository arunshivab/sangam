using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// rc.12: an application adds staff for one of its admins, who signed in to it with Sangam; the person joins on the e-mailed
/// link with the admin's code, creating their account on the way.
/// </summary>
[Collection("server")]
public sealed partial class StaffApiTests
{
    private static readonly string[] WardRead = ["ward.read"];
    private static readonly string[] WardNurse = ["ward_nurse"];
    private readonly SangamServerFactory _factory;

    public StaffApiTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task AnApplication_AddsStaff_ForItsSignedInAdmin_AndThePersonJoinsWithTheCode()
    {
        // The admin signs in to the application with Sangam, which gives the application their access token.
        using BrowserSession admin = new(_factory);
        string adminEmail = await RegisterAndVerifyAsync(admin, "Anita", "Rao", $"staff-admin-{Guid.NewGuid():N}@example.in");
        (string verifier, string challenge) = Pkce();
        (_, string consentPath, _) = await admin.FollowAsync(Authorize(challenge));
        string adminToken = (await ExchangeAsync(admin, consentPath, verifier)).GetProperty("access_token").GetString()!;

        using HttpClient client = _factory.CreateClient();
        string appToken = await ClientTokenAsync(client);
        Guid orgId = Guid.NewGuid();
        using (HttpResponseMessage org = await SendAsync(client, appToken, HttpMethod.Put, $"/api/v1/orgs/{orgId:D}", new { name = "Staff Clinic", type = "hospital", parentId = (Guid?)null, metadata = (string?)null }, null))
        {
            Assert.Equal(HttpStatusCode.OK, org.StatusCode);
        }

        using (HttpResponseMessage role = await SendAsync(client, appToken, HttpMethod.Put, "/api/v1/roles/ward_nurse", new { displayName = "Ward nurse", description = "rc.12", permissions = WardRead, orgId = (Guid?)null }, null))
        {
            Assert.Equal(HttpStatusCode.OK, role.StatusCode);
        }

        await MakeAdminAsync(adminEmail, orgId);
        // A last name of its own, so an earlier run's Kiran in the shared test database is not "similar".
        string lastName = "Das" + Guid.NewGuid().ToString("N")[..6];
        object person = new { firstName = "Kiran", lastName, email = $"kiran-{Guid.NewGuid():N}@example.in", mobile = (string?)null, dateOfBirth = "1993-02-14" };

        // Without the admin's own token, or with the application's token in its place, nothing happens.
        using (HttpResponseMessage none = await SendAsync(client, appToken, HttpMethod.Post, $"/api/v1/orgs/{orgId:D}/staff/check", person, null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        }

        using (HttpResponseMessage machine = await SendAsync(client, appToken, HttpMethod.Post, $"/api/v1/orgs/{orgId:D}/staff/check", person, appToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, machine.StatusCode);
        }

        using (HttpResponseMessage check = await SendAsync(client, appToken, HttpMethod.Post, $"/api/v1/orgs/{orgId:D}/staff/check", person, adminToken))
        {
            JsonElement body = await check.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(HttpStatusCode.OK, check.StatusCode);
            Assert.Equal("none", body.GetProperty("outcome").GetString());
        }

        string code;
        using (HttpResponseMessage added = await SendAsync(client, appToken, HttpMethod.Post, $"/api/v1/orgs/{orgId:D}/staff", new { person, roles = WardNurse }, adminToken))
        {
            JsonElement body = await added.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            Assert.Equal("waiting", body.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("userId").ValueKind);
            code = body.GetProperty("activationCode").GetString()!;
        }

        // The e-mail-only invitation is gone.
        using (HttpResponseMessage old = await SendAsync(client, appToken, HttpMethod.Post, $"/api/v1/orgs/{orgId:D}/invitations", new { email = "x@example.in", role = "ward_nurse", appliesToDescendants = false }, null))
        {
            Assert.Equal(HttpStatusCode.Gone, old.StatusCode);
        }

        // The person opens the link, enters the code, creates their account, and joins.
        string email = JsonSerializer.SerializeToElement(person).GetProperty("email").GetString()!;
        InMemoryEmailOutbox outbox = _factory.Services.GetRequiredService<InMemoryEmailOutbox>();
        string link = LinkPath().Match(outbox.LatestFor(email)!.Message.TextBody).Value;
        using BrowserSession kiran = new(_factory);
        (HttpStatusCode opened, string page) = await kiran.GetAsync(link);
        Assert.Equal(HttpStatusCode.OK, opened);
        Assert.Contains("Staff Clinic", page, StringComparison.Ordinal);

        (HttpStatusCode wrongStatus, _, string wrongPage) = await kiran.PostFormAsync(link, new Dictionary<string, string> { ["Code"] = "AAAA-AAAA" }, handler: "Code");
        Assert.Equal(HttpStatusCode.OK, wrongStatus);
        Assert.Contains("4 more tries", WebUtility.HtmlDecode(wrongPage), StringComparison.Ordinal);

        (HttpStatusCode right, string? ticketed, _) = await kiran.PostFormAsync(link, new Dictionary<string, string> { ["Code"] = code }, handler: "Code");
        Assert.Equal(HttpStatusCode.Found, right);
        Assert.Contains("ticket=", ticketed, StringComparison.Ordinal);

        (_, string register) = await kiran.GetAsync("/register?returnUrl=" + Uri.EscapeDataString(ticketed!));
        Assert.Contains("Kiran", register, StringComparison.Ordinal);
        await RegisterAndVerifyAsync(kiran, "Kiran", lastName, email, ticketed);

        (HttpStatusCode joinStatus, _, string joined) = await kiran.PostFormAsync(ticketed!, [], handler: "Join");
        Assert.Equal(HttpStatusCode.OK, joinStatus);
        Assert.Contains("You have joined", WebUtility.HtmlDecode(joined), StringComparison.Ordinal);

        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        Guid kiranId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        Assert.Equal("ward_nurse", await db.OrgMemberships.Where(m => m.UserId == kiranId && m.OrgId == orgId && m.RevokedAt == null).Select(m => m.Role!.Code).SingleAsync());
    }

    private async Task MakeAdminAsync(string email, Guid orgId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        Guid app = await db.Apps.Where(a => a.ClientId == DevelopmentSeeder.SampleClientId).Select(a => a.Id).SingleAsync();
        SangamUser user = await db.Users.SingleAsync(u => u.Email == email);
        user.IdentityVerifiedAt = DateTimeOffset.UtcNow;
        db.OrgAdmins.Add(new OrgAdmin { Id = Guid.NewGuid(), AppId = app, OrgId = orgId, UserId = user.Id, Right = OrgAdminRight.Admin, GrantedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task<string> RegisterAndVerifyAsync(BrowserSession s, string first, string last, string email, string? returnUrl = null)
    {
        string mobile = Random.Shared.NextInt64(7000000000, 9999999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        InMemoryEmailOutbox outbox = _factory.Services.GetRequiredService<InMemoryEmailOutbox>();
        string path = returnUrl is null ? "/register" : "/register?returnUrl=" + Uri.EscapeDataString(returnUrl);
        (HttpStatusCode status, string? location, string html) = await s.PostFormAsync(path, new Dictionary<string, string>
        {
            ["FirstName"] = first,
            ["LastName"] = last,
            ["Email"] = email,
            ["Country"] = "IN",
            ["MobileNumber"] = mobile,
            ["DateOfBirth"] = "1993-02-14",
            ["Gender"] = "female",
            ["Password"] = "Kaveri-River-2026!",
            ["AcceptTerms"] = "true",
        });
        Assert.True(status == HttpStatusCode.Found && location!.StartsWith("/verify", StringComparison.Ordinal), html);

        string code = Regex.Match(outbox.LatestFor(email)!.Message.TextBody, "[0-9]{6}").Value;
        (_, string? verified, _) = await s.PostFormAsync(location, new Dictionary<string, string> { ["Code"] = code });
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

    private static async Task<string> ClientTokenAsync(HttpClient client)
    {
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_secret"] = DevelopmentSeeder.SampleClientSecret,
            ["scope"] = "sangam.manage",
        });
        using HttpResponseMessage response = await client.PostAsync(new Uri("/connect/token", UriKind.Relative), form);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, object? body, string? actingAdmin)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (actingAdmin is not null)
        {
            request.Headers.Add("Sangam-Acting-Admin", actingAdmin);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static string Authorize(string challenge)
        => "/connect/authorize?client_id=" + DevelopmentSeeder.SampleClientId
        + "&redirect_uri=" + Uri.EscapeDataString(DevelopmentSeeder.SampleRedirectUri)
        + "&response_type=code&scope=" + Uri.EscapeDataString("openid profile email")
        + "&state=xyz&code_challenge=" + challenge + "&code_challenge_method=S256";

    private static (string Verifier, string Challenge) Pkce()
    {
        string verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    [GeneratedRegex(@"/staff/[A-Za-z0-9_-]+")]
    private static partial Regex LinkPath();
}
