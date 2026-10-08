using System.Globalization;
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
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Sangam.Identity.Domain;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Server.Signatures;

namespace Sangam.Identity.Server.Tests.Oidc;

/// <summary>PR-17 (SGM-207 §5): an application requests a signature; the person signs after a fresh two-step sign-in; the token proves it.</summary>
[Collection("server")]
public sealed partial class SignatureTests
{
    private const string Password = "Correct-Horse-2026!";
    private readonly SangamServerFactory _factory;

    public SignatureTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task ARecord_IsSigned_AfterAFreshTwoStepSignIn_AndTheTokenVerifiesWithThePublishedKeys()
    {
        using HttpClient api = _factory.CreateClient();
        string appToken = await ClientTokenAsync(api);
        string hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("SOP-QA-014 rev 3")));
        using HttpResponseMessage created = await SendAsync(api, appToken, HttpMethod.Post, "/api/v1/signatures", new
        {
            recordId = "SOP-QA-014/3",
            recordHash = hash,
            meaning = "Approved",
            displayText = "SOP-QA-014 Handling of radioactive waste, revision 3",
            returnUrl = DevelopmentSeeder.SampleRedirectUri,
        });
        string createdBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, createdBody);
        JsonElement request = JsonDocument.Parse(createdBody).RootElement;
        string requestId = request.GetProperty("requestId").GetString()!;
        string ceremony = request.GetProperty("ceremonyPath").GetString()!;

        // The person is signed in with a password only: the ceremony sends them to sign in again, with a code.
        using BrowserSession s = new(_factory);
        (Guid userId, string email) = await RegisterAsync(s);
        (_, string location, _) = await s.FollowAsync(ceremony);
        Assert.Equal("/login", location.Split('?')[0]);
        (_, string? afterPassword, _) = await s.PostFormAsync("/login?returnUrl=" + Uri.EscapeDataString(ceremony), new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });
        Assert.Equal("/login/verify", afterPassword?.Split('?')[0]);
        string code = CodeRegex().Match(_factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message.TextBody).Value;
        (_, string? afterCode, _) = await s.PostFormAsync(afterPassword!, new Dictionary<string, string> { ["Code"] = code });
        Assert.Equal(ceremony, afterCode);

        (HttpStatusCode pageStatus, string page) = await s.GetAsync(ceremony);
        Assert.Equal(HttpStatusCode.OK, pageStatus);
        Assert.Contains("Approved", page, StringComparison.Ordinal);
        Assert.Contains("Handling of radioactive waste", page, StringComparison.Ordinal);
        Assert.Contains(hash, page, StringComparison.Ordinal);

        (_, string? back, _) = await s.PostFormAsync(ceremony, [], handler: "Sign");
        Assert.StartsWith(DevelopmentSeeder.SampleRedirectUri, back, StringComparison.Ordinal);
        Assert.Contains("signature_request=" + requestId, back, StringComparison.Ordinal);
        Assert.Contains("status=signed", back, StringComparison.Ordinal);

        // Only once: the ceremony no longer offers to sign.
        (_, string againHtml) = await s.GetAsync(ceremony);
        Assert.Contains("already been answered", againHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Sign as", againHtml, StringComparison.Ordinal);

        // The application reads the result and verifies the token as it would any Sangam token.
        using HttpResponseMessage read = await SendAsync(api, appToken, HttpMethod.Get, "/api/v1/signatures/" + requestId, null);
        JsonElement result = JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("signed", result.GetProperty("status").GetString());
        string token = result.GetProperty("token").GetString()!;

        JsonElement discovery = JsonDocument.Parse(await api.GetStringAsync(new Uri("/.well-known/openid-configuration", UriKind.Relative))).RootElement;
        string issuer = discovery.GetProperty("issuer").GetString()!;
        JsonWebKeySet keys = new(await api.GetStringAsync(new Uri(discovery.GetProperty("jwks_uri").GetString()!)));
        TokenValidationResult validated = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = DevelopmentSeeder.SampleClientId,
            IssuerSigningKeys = keys.GetSigningKeys(),
            ValidTypes = [SignatureTokenIssuer.TokenType],
            RequireExpirationTime = false,
        });
        Assert.True(validated.IsValid, validated.Exception?.Message);
        Assert.Equal(userId.ToString("D"), validated.Claims["sub"]);
        Assert.Equal(hash, validated.Claims["sig_record_hash"]);
        Assert.Equal("Approved", validated.Claims["sig_meaning"]);
        Assert.Equal(AuthenticationAssurance.AcrSign, validated.Claims["acr"]);
        Assert.Contains("mfa", ((System.Collections.IEnumerable)validated.Claims["amr"]).Cast<object>().Select(o => o.ToString()));

        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.UserSignatureSign && e.ActorUserId == userId));
    }

    [PostgresFact]
    public async Task Requests_AreRefused_WithAnUnregisteredReturnUrl_OrAMalformedHash()
    {
        using HttpClient api = _factory.CreateClient();
        string appToken = await ClientTokenAsync(api);
        string hash = "sha256:" + new string('a', 64);

        using HttpResponseMessage elsewhere = await SendAsync(api, appToken, HttpMethod.Post, "/api/v1/signatures", new { recordId = "R1", recordHash = hash, meaning = "Approved", displayText = "Doc", returnUrl = "https://evil.example/collect" });
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);

        using HttpResponseMessage badHash = await SendAsync(api, appToken, HttpMethod.Post, "/api/v1/signatures", new { recordId = "R1", recordHash = "md5:abcd", meaning = "Approved", displayText = "Doc", returnUrl = DevelopmentSeeder.SampleRedirectUri });
        Assert.Equal(HttpStatusCode.BadRequest, badHash.StatusCode);

        using HttpResponseMessage anonymous = await api.PostAsJsonAsync(new Uri("/api/v1/signatures", UriKind.Relative), new { recordId = "R1" });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [PostgresFact]
    public async Task ARequestForANamedSigner_CannotBeSignedBySomeoneElse_AndDeclineGoesBack()
    {
        using BrowserSession named = new(_factory);
        (Guid namedId, _) = await RegisterAsync(named);
        using HttpClient api = _factory.CreateClient();
        string appToken = await ClientTokenAsync(api);
        using HttpResponseMessage created = await SendAsync(api, appToken, HttpMethod.Post, "/api/v1/signatures", new
        {
            recordId = "ORDER-77",
            recordHash = "sha256:" + new string('b', 64),
            meaning = "Ordered",
            displayText = "Chemotherapy order",
            returnUrl = DevelopmentSeeder.SampleRedirectUri,
            signer = namedId,
        });
        string ceremony = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("ceremonyPath").GetString()!;

        using BrowserSession other = new(_factory);
        (_, string otherEmail) = await RegisterAsync(other);
        await SignInWithCodeAsync(other, otherEmail, ceremony);
        (_, string page) = await other.GetAsync(ceremony);
        Assert.Contains("for someone else to sign", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Chemotherapy order", page, StringComparison.Ordinal);
    }

    private async Task SignInWithCodeAsync(BrowserSession s, string email, string returnUrl)
    {
        await s.FollowAsync(returnUrl);
        (_, string? afterPassword, _) = await s.PostFormAsync("/login?returnUrl=" + Uri.EscapeDataString(returnUrl), new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });
        string code = CodeRegex().Match(_factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message.TextBody).Value;
        await s.PostFormAsync(afterPassword!, new Dictionary<string, string> { ["Code"] = code });
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

    private async Task<(Guid UserId, string Email)> RegisterAsync(BrowserSession s)
    {
        string email = $"sign-{Guid.NewGuid():N}@example.in";
        (HttpStatusCode st, string? loc, string html) = await s.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["FirstName"] = "Uma",
            ["LastName"] = "Menon",
            ["Email"] = email,
            ["Country"] = "IN",
            ["MobileNumber"] = Random.Shared.NextInt64(7000000000, 9999999999).ToString(CultureInfo.InvariantCulture),
            ["BirthDay"] = "5",
            ["BirthMonth"] = "5",
            ["BirthYear"] = "1979",
            ["Gender"] = "female",
            ["Password"] = Password,
            ["AcceptTerms"] = "true",
        });
        Assert.True(st == HttpStatusCode.Found && loc == "/verify", html);
        string code = CodeRegex().Match(_factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message.TextBody).Value;
        await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = code });
        using IServiceScope scope = _factory.Services.CreateScope();
        Guid id = await scope.ServiceProvider.GetRequiredService<SangamDbContext>().Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        _ = HttpUtility.UrlEncode(email);
        return (id, email);
    }

    [GeneratedRegex("[0-9]{6}")]
    private static partial Regex CodeRegex();
}
