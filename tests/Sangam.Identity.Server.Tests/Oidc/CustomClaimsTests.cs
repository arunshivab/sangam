using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Server.Tests.Oidc;

/// <summary>
/// PR-25: an application's custom claims reach its access token and userinfo only under the <c>attributes</c> scope, the
/// consent screen shows the values it will release, and the management API reads and writes the values.
/// </summary>
[Collection("server")]
public sealed class CustomClaimsTests
{
    private readonly SangamServerFactory _factory;

    public CustomClaimsTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task CustomClaims_AreShownOnConsent_AndReleasedOnlyUnderTheAttributesScope()
    {
        using BrowserSession s = new(_factory);
        string email = await RegisterAndVerifyAsync(s);
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string key = "badge_" + suffix;
        string claim = "badge_" + suffix;
        await SeedAsync(email, key, claim, "B-" + suffix);

        // With the attributes scope: the consent screen names the value, and both the access token and userinfo carry it.
        (string verifier, string challenge) = Pkce();
        (HttpStatusCode status, string consentPath, string consentHtml) = await s.FollowAsync(Authorize(challenge, "openid profile email attributes"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Details Sangam development sample keeps about you", WebUtility.HtmlDecode(consentHtml), StringComparison.Ordinal);
        Assert.Contains("B-" + suffix, consentHtml, StringComparison.Ordinal);

        JsonElement tokens = await ExchangeAsync(s, consentPath, verifier);
        string accessToken = tokens.GetProperty("access_token").GetString()!;
        Assert.Equal("B-" + suffix, Payload(accessToken).GetProperty(claim).GetString());
        Assert.False(Payload(tokens.GetProperty("id_token").GetString()!).TryGetProperty(claim, out _), "Custom claims stay out of the ID token.");
        Assert.Equal("B-" + suffix, (await UserInfoAsync(accessToken)).GetProperty(claim).GetString());

        // Without it: nothing.
        (string verifier2, string challenge2) = Pkce();
        (_, string consent2, string html2) = await s.FollowAsync(Authorize(challenge2, "openid profile email"));
        Assert.DoesNotContain("B-" + suffix, html2, StringComparison.Ordinal);
        JsonElement plain = await ExchangeAsync(s, consent2, verifier2);
        string plainToken = plain.GetProperty("access_token").GetString()!;
        Assert.False(Payload(plainToken).TryGetProperty(claim, out _));
        Assert.False((await UserInfoAsync(plainToken)).TryGetProperty(claim, out _));
    }

    [PostgresFact]
    public async Task TheManagementApi_ReadsAndWritesValues_ForPeopleWhoLinkedTheApplication()
    {
        using BrowserSession s = new(_factory);
        string email = await RegisterAndVerifyAsync(s);
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string key = "staff_" + suffix;
        Guid person = await SeedAsync(email, key, claim: null, value: null);

        using HttpClient client = _factory.CreateClient();
        string token = await ClientTokenAsync(client);

        // Not linked yet: the application learns nothing.
        using (HttpResponseMessage before = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/users/{person:D}/attributes", null))
        {
            Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        }

        await LinkAsync(person);
        using (HttpResponseMessage refused = await SendAsync(client, token, HttpMethod.Put, $"/api/v1/users/{person:D}/attributes", new Dictionary<string, string?> { ["nothing_" + suffix] = "x" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        using (HttpResponseMessage put = await SendAsync(client, token, HttpMethod.Put, $"/api/v1/users/{person:D}/attributes", new Dictionary<string, string?> { [key] = "S-77" }))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        using HttpResponseMessage get = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/users/{person:D}/attributes", null);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        JsonElement values = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("S-77", values.GetProperty(key).GetString());
    }

    private async Task<Guid> SeedAsync(string email, string key, string? claim, string? value)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        Guid app = await db.Apps.Where(a => a.ClientId == DevelopmentSeeder.SampleClientId).Select(a => a.Id).SingleAsync();
        Guid person = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        UserAttributeDefinition definition = new()
        {
            Id = Guid.NewGuid(),
            AppId = app,
            Key = key,
            Label = "Badge",
            Type = "text",
            EditableBy = "admin",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.UserAttributeDefinitions.Add(definition);
        if (value is not null)
        {
            db.UserAttributeValues.Add(new UserAttributeValue { DefinitionId = definition.Id, UserId = person, Value = value, UpdatedAt = DateTimeOffset.UtcNow });
        }

        if (claim is not null)
        {
            db.AppClaimMappings.Add(new AppClaimMapping { Id = Guid.NewGuid(), AppId = app, ClaimName = claim, Source = "attribute", AttributeKey = key, CreatedAt = DateTimeOffset.UtcNow });
        }

        await db.SaveChangesAsync();
        return person;
    }

    private async Task LinkAsync(Guid person)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        Guid app = await db.Apps.Where(a => a.ClientId == DevelopmentSeeder.SampleClientId).Select(a => a.Id).SingleAsync();
        db.AppGrants.Add(new AppGrant { Id = Guid.NewGuid(), AppId = app, UserId = person, GrantedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task<JsonElement> ExchangeAsync(BrowserSession s, string consentPath, string verifier)
    {
        // A second sign-in within scopes already allowed skips the consent screen and lands on the callback directly.
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

    private async Task<JsonElement> UserInfoAsync(string accessToken)
    {
        using HttpClient api = _factory.CreateClient();
        using HttpRequestMessage me = new(HttpMethod.Get, "/connect/userinfo");
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await api.SendAsync(me);
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

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, object? body)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private async Task<string> RegisterAndVerifyAsync(BrowserSession s)
    {
        string email = $"claims-{Guid.NewGuid():N}@example.in";
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

        string code = System.Text.RegularExpressions.Regex.Match(outbox.LatestFor(email)!.Message.TextBody, "[0-9]{6}").Value;
        (_, string? verified, _) = await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = code });
        Assert.StartsWith("/verified", verified, StringComparison.Ordinal);
        return email;
    }
}
