using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Consents;
using Sangam.Identity.Domain;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Server.Tests.Oidc;

/// <summary>
/// PR-21 (SGM-219): native and mobile applications (public clients, PKCE, RFC 8252 redirects, refresh rotation with
/// reuse detection), the device authorization grant with the /device page, pushed authorization requests, and token
/// exchange between applications.
/// </summary>
[Collection("server")]
public sealed partial class NativeAndDeviceTests
{
    private const string Password = "Correct-Horse-2026!";
    private readonly SangamServerFactory _factory;

    public NativeAndDeviceTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Discovery_AdvertisesTheDeviceEndpoint_Par_AndTokenExchange()
    {
        using HttpClient client = _factory.CreateClient();
        JsonElement doc = JsonDocument.Parse(await client.GetStringAsync(new Uri("/.well-known/openid-configuration", UriKind.Relative))).RootElement;
        Assert.EndsWith("/connect/device", doc.GetProperty("device_authorization_endpoint").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("/connect/par", doc.GetProperty("pushed_authorization_request_endpoint").GetString(), StringComparison.Ordinal);
        string[] grants = [.. doc.GetProperty("grant_types_supported").EnumerateArray().Select(g => g.GetString()!)];
        Assert.Contains("urn:ietf:params:oauth:grant-type:device_code", grants);
        Assert.Contains("urn:ietf:params:oauth:grant-type:token-exchange", grants);
    }

    [Theory]
    [InlineData("in.sangamid.app:/callback", true)]
    [InlineData("https://app.example.in/callback", true)]
    [InlineData("http://127.0.0.1/callback", true)]
    [InlineData("http://[::1]:8080/callback", true)]
    [InlineData("http://localhost/callback", false)]
    [InlineData("http://app.example.in/callback", false)]
    [InlineData("myapp:/callback", false)]
    [InlineData("https://app.example.in/callback#x", false)]
    [InlineData("https://127.0.0.1/callback", false)]
    public void NativeRedirects_FollowRfc8252(string uri, bool allowed)
    {
        Assert.Equal(allowed, NativeRedirectUris.Validate(uri) is null);
    }

    [PostgresFact]
    public async Task ADevice_SignsIn_WithACodeApprovedOnAnotherDevice()
    {
        using BrowserSession s = new(_factory);
        (Guid userId, string email) = await RegisterAsync(s);
        using HttpClient device = _factory.CreateClient();

        JsonElement started = await PostAsync(device, "/connect/device", new() { ["client_id"] = SangamServerFactory.DeviceClientId, ["scope"] = "openid profile email offline_access" });
        string deviceCode = started.GetProperty("device_code").GetString()!;
        string userCode = started.GetProperty("user_code").GetString()!;
        Assert.EndsWith("/device", started.GetProperty("verification_uri").GetString(), StringComparison.Ordinal);
        Assert.Contains("user_code=", started.GetProperty("verification_uri_complete").GetString(), StringComparison.Ordinal);

        Dictionary<string, string> poll = new() { ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["device_code"] = deviceCode, ["client_id"] = SangamServerFactory.DeviceClientId };
        Assert.Equal("authorization_pending", (await PostAsync(device, "/connect/token", poll, expectError: true)).GetProperty("error").GetString());

        // A wrong code is told so; the right one shows who asks and what would be shared, after sign-in.
        (_, string wrong) = await s.GetAsync("/device?user_code=NOTACODE");
        Assert.Contains("That code is not right, or it has expired.", WebUtility.HtmlDecode(wrong), StringComparison.Ordinal);
        (_, string at, string approve) = await s.FollowAsync("/device?user_code=" + Uri.EscapeDataString(userCode));
        Assert.StartsWith("/device", at, StringComparison.Ordinal);
        approve = WebUtility.HtmlDecode(approve);
        Assert.Contains("wants to sign in as you", approve, StringComparison.Ordinal);
        Assert.Contains(email, approve, StringComparison.Ordinal);
        Assert.Contains(userCode, approve, StringComparison.Ordinal);

        (HttpStatusCode status, string? location, _) = await s.SubmitAsync("/device?handler=Allow", approve, new() { ["user_code"] = userCode });
        Assert.True(status is HttpStatusCode.Found or HttpStatusCode.SeeOther, $"got {(int)status}");
        Assert.Contains("result=approved", location, StringComparison.Ordinal);

        JsonElement tokens = await PostAsync(device, "/connect/token", poll);
        Assert.True(tokens.TryGetProperty("access_token", out _));
        Assert.True(tokens.TryGetProperty("refresh_token", out _));
        Assert.Equal(userId.ToString("D"), Claims(tokens.GetProperty("id_token").GetString()!).GetProperty("sub").GetString());

        // The code is spent; the approval is audited.
        Assert.Equal("invalid_grant", (await PostAsync(device, "/connect/token", poll, expectError: true)).GetProperty("error").GetString());
        using IServiceScope scope = _factory.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<SangamDbContext>().AuditEvents.AnyAsync(e => e.Action == AuditActions.DeviceApprove && e.ActorUserId == userId));
    }

    [PostgresFact]
    public async Task ARefusedDevice_IsToldAccessWasDenied()
    {
        using BrowserSession s = new(_factory);
        await RegisterAsync(s);
        using HttpClient device = _factory.CreateClient();
        JsonElement started = await PostAsync(device, "/connect/device", new() { ["client_id"] = SangamServerFactory.DeviceClientId, ["scope"] = "openid profile" });
        string userCode = started.GetProperty("user_code").GetString()!;
        (_, _, string approve) = await s.FollowAsync("/device?user_code=" + Uri.EscapeDataString(userCode));
        (_, string? location, _) = await s.SubmitAsync("/device?handler=Deny", approve, new() { ["user_code"] = userCode });
        Assert.Contains("result=refused", location, StringComparison.Ordinal);

        Dictionary<string, string> poll = new() { ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["device_code"] = started.GetProperty("device_code").GetString()!, ["client_id"] = SangamServerFactory.DeviceClientId };
        Assert.Equal("access_denied", (await PostAsync(device, "/connect/token", poll, expectError: true)).GetProperty("error").GetString());
    }

    [PostgresFact]
    public async Task ANativeApp_SignsIn_WithoutASecret_AndAReusedRefreshTokenEndsTheSignIn()
    {
        using BrowserSession s = new(_factory);
        await RegisterAsync(s);
        using HttpClient app = _factory.CreateClient();
        JsonElement tokens = await AuthorizeAsync(s, app, SangamServerFactory.NativeClientId, SangamServerFactory.NativeRedirectUri, secret: null, "openid profile offline_access");
        string first = tokens.GetProperty("refresh_token").GetString()!;

        Dictionary<string, string> refresh = new() { ["grant_type"] = "refresh_token", ["client_id"] = SangamServerFactory.NativeClientId, ["refresh_token"] = first };
        string second = (await PostAsync(app, "/connect/token", refresh)).GetProperty("refresh_token").GetString()!;
        Assert.NotEqual(first, second);

        // Reusing the spent token is theft: it fails, and so does the one it was rotated into.
        Assert.Equal("invalid_grant", (await PostAsync(app, "/connect/token", refresh, expectError: true)).GetProperty("error").GetString());
        refresh["refresh_token"] = second;
        Assert.Equal("invalid_grant", (await PostAsync(app, "/connect/token", refresh, expectError: true)).GetProperty("error").GetString());
    }

    [PostgresFact]
    public async Task ADesktopApp_MayUseAnyLoopbackPort()
    {
        using BrowserSession s = new(_factory);
        await RegisterAsync(s);
        using HttpClient app = _factory.CreateClient();
        JsonElement tokens = await AuthorizeAsync(s, app, SangamServerFactory.NativeClientId, "http://127.0.0.1:53123/callback", secret: null, "openid profile");
        Assert.True(tokens.TryGetProperty("access_token", out _));
    }

    [PostgresFact]
    public async Task PushedRequests_Work_AndAnApplicationCanBeHeldToThem()
    {
        using BrowserSession s = new(_factory);
        await RegisterAsync(s);
        using HttpClient api = _factory.CreateClient();
        (string verifier, string challenge) = Pkce();
        JsonElement pushed = await PostAsync(api, "/connect/par", new()
        {
            ["client_id"] = SangamServerFactory.ParOnlyClientId,
            ["client_secret"] = SangamServerFactory.ExchangerSecret,
            ["redirect_uri"] = "https://par.example.in/signin-sangam",
            ["response_type"] = "code",
            ["scope"] = "openid profile",
            ["state"] = "xyz",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        });
        string requestUri = pushed.GetProperty("request_uri").GetString()!;
        string code = await CodeFromAsync(s, "/connect/authorize?client_id=" + SangamServerFactory.ParOnlyClientId + "&request_uri=" + Uri.EscapeDataString(requestUri));
        JsonElement tokens = await PostAsync(api, "/connect/token", new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "https://par.example.in/signin-sangam",
            ["client_id"] = SangamServerFactory.ParOnlyClientId,
            ["client_secret"] = SangamServerFactory.ExchangerSecret,
            ["code_verifier"] = verifier,
        });
        Assert.True(tokens.TryGetProperty("access_token", out _));

        // Without a pushed request, that application is refused.
        (HttpStatusCode refused, string location, string page) = await s.FollowAsync("/connect/authorize?client_id=" + SangamServerFactory.ParOnlyClientId
            + "&redirect_uri=" + Uri.EscapeDataString("https://par.example.in/signin-sangam") + "&response_type=code&scope=openid&code_challenge=" + challenge + "&code_challenge_method=S256");
        Assert.True(refused == HttpStatusCode.BadRequest || location.Contains("error=", StringComparison.Ordinal), $"{(int)refused} {location} {page}");
    }

    [PostgresFact]
    public async Task TokenExchange_GivesAShortTokenForAnotherApplication_OnlyWithConsentAndPermission()
    {
        using BrowserSession s = new(_factory);
        (Guid userId, _) = await RegisterAsync(s);
        using HttpClient api = _factory.CreateClient();
        JsonElement tokens = await AuthorizeAsync(s, api, SangamServerFactory.ExchangerClientId, "https://exchanger.example.in/signin-sangam", SangamServerFactory.ExchangerSecret, "openid profile email");
        string access = tokens.GetProperty("access_token").GetString()!;
        Dictionary<string, string> exchange = new()
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["client_id"] = SangamServerFactory.ExchangerClientId,
            ["client_secret"] = SangamServerFactory.ExchangerSecret,
            ["subject_token"] = access,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["audience"] = DevelopmentSeeder.ImagiqaClientId,
            ["scope"] = "profile",
        };

        // The person has not allowed imagiQa yet.
        Assert.Equal("invalid_grant", (await PostAsync(api, "/connect/token", exchange, expectError: true)).GetProperty("error").GetString());

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppSummary imagiqa = (await scope.ServiceProvider.GetRequiredService<IAppDirectory>().FindByClientIdAsync(DevelopmentSeeder.ImagiqaClientId))!;
            await scope.ServiceProvider.GetRequiredService<IConsentService>().GrantAsync(userId, imagiqa.Id, ["openid", "profile", "email"], "198.51.100.1", "test");
        }

        JsonElement exchanged = await PostAsync(api, "/connect/token", exchange);
        Assert.False(exchanged.TryGetProperty("refresh_token", out _));
        Assert.InRange(exchanged.GetProperty("expires_in").GetInt32(), 1, 600);
        JsonElement claims = Claims(exchanged.GetProperty("access_token").GetString()!);
        Assert.Equal(userId.ToString("D"), claims.GetProperty("sub").GetString());
        Assert.Contains(DevelopmentSeeder.ImagiqaClientId, claims.GetProperty("aud").ToString(), StringComparison.Ordinal);
        Assert.Equal(SangamServerFactory.ExchangerClientId, claims.GetProperty("act").GetProperty("sub").GetString());

        // Never more than the original, never an audience it has no permission for.
        exchange["scope"] = "profile orgs.read";
        Assert.Equal("invalid_scope", (await PostAsync(api, "/connect/token", exchange, expectError: true)).GetProperty("error").GetString());
        exchange["scope"] = "profile";
        exchange["audience"] = "sangam-portal";
        Assert.NotEqual(HttpStatusCode.OK, (await RawPostAsync(api, "/connect/token", exchange)).Status);

        using IServiceScope after = _factory.Services.CreateScope();
        Assert.True(await after.ServiceProvider.GetRequiredService<SangamDbContext>().AuditEvents.AnyAsync(e => e.Action == AuditActions.TokenExchange && e.ActorUserId == userId));
    }

    private static async Task<JsonElement> AuthorizeAsync(BrowserSession s, HttpClient api, string clientId, string redirectUri, string? secret, string scope)
    {
        (string verifier, string challenge) = Pkce();
        string code = await CodeFromAsync(s, "/connect/authorize?client_id=" + clientId + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
            + "&response_type=code&scope=" + Uri.EscapeDataString(scope) + "&state=xyz&code_challenge=" + challenge + "&code_challenge_method=S256");
        Dictionary<string, string> form = new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        };
        if (secret is not null)
        {
            form["client_secret"] = secret;
        }

        return await PostAsync(api, "/connect/token", form);
    }

    private static async Task<string> CodeFromAsync(BrowserSession s, string authorize)
    {
        (_, string path, _) = await s.FollowAsync(authorize);
        string redirect = path;
        if (path.StartsWith("/consent", StringComparison.Ordinal))
        {
            (_, string? allow, _) = await s.PostFormAsync(path, [], handler: "Allow");
            (_, redirect, _) = allow!.StartsWith('/') ? await s.FollowAsync(allow) : (HttpStatusCode.Found, allow, string.Empty);
        }

        string? code = HttpUtility.ParseQueryString(new Uri(redirect).Query)["code"];
        Assert.True(code is not null, "No code in " + redirect);
        return code;
    }

    private static (string Verifier, string Challenge) Pkce()
    {
        string verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
    }

    private static async Task<JsonElement> PostAsync(HttpClient api, string path, Dictionary<string, string> form, bool expectError = false)
    {
        (HttpStatusCode status, string body) = await RawPostAsync(api, path, form);
        Assert.True(expectError != (status is HttpStatusCode.OK or HttpStatusCode.Created), $"{path} answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<(HttpStatusCode Status, string Body)> RawPostAsync(HttpClient api, string path, Dictionary<string, string> form)
    {
        using FormUrlEncodedContent content = new(form);
        using HttpResponseMessage response = await api.PostAsync(new Uri(path, UriKind.Relative), content);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Claims(string jwt)
    {
        string payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement.Clone();
    }

    private async Task<(Guid UserId, string Email)> RegisterAsync(BrowserSession s)
    {
        string email = $"native-{Guid.NewGuid():N}@example.in";
        (HttpStatusCode st, string? loc, string html) = await s.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["FirstName"] = "Kavya",
            ["LastName"] = "Menon",
            ["Email"] = email,
            ["Country"] = "IN",
            ["MobileNumber"] = Random.Shared.NextInt64(7000000000, 9999999999).ToString(CultureInfo.InvariantCulture),
            ["BirthDay"] = "12",
            ["BirthMonth"] = "3",
            ["BirthYear"] = "1985",
            ["Gender"] = "female",
            ["Password"] = Password,
            ["AcceptTerms"] = "true",
        });
        Assert.True(st == HttpStatusCode.Found && loc == "/verify", html);
        string code = CodeRegex().Match(_factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message.TextBody).Value;
        (_, string? ok, _) = await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = code });
        Assert.Equal("/verified", ok);

        using IServiceScope scope = _factory.Services.CreateScope();
        Guid id = await scope.ServiceProvider.GetRequiredService<SangamDbContext>().Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        return (id, email);
    }

    [GeneratedRegex(@"\b\d{6}\b")]
    private static partial Regex CodeRegex();
}
