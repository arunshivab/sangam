using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Server.Logout;

namespace Sangam.Identity.Server.Tests.Oidc;

/// <summary>
/// PR-20: applications learn that a Sangam session has ended — in the browser (front-channel) and server to server
/// (back-channel) — and their back ends can introspect and revoke tokens.
/// </summary>
[Collection("server")]
public sealed partial class LogoutChannelTests
{
    private const string Password = "Correct-Horse-2026!";
    private const string BackChannel = "https://sample.example.in/signout/backchannel";
    private const string FrontChannel = "https://sample.example.in/signout/frontchannel?app=1";
    private readonly SangamServerFactory _factory;

    public LogoutChannelTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Discovery_AdvertisesBothLogoutChannels_IntrospectionAndRevocation()
    {
        using HttpClient client = _factory.CreateClient();
        JsonElement doc = JsonDocument.Parse(await client.GetStringAsync(new Uri("/.well-known/openid-configuration", UriKind.Relative))).RootElement;
        Assert.True(doc.GetProperty("frontchannel_logout_supported").GetBoolean());
        Assert.True(doc.GetProperty("frontchannel_logout_session_supported").GetBoolean());
        Assert.True(doc.GetProperty("backchannel_logout_supported").GetBoolean());
        Assert.True(doc.GetProperty("backchannel_logout_session_supported").GetBoolean());
        Assert.EndsWith("/connect/introspect", doc.GetProperty("introspection_endpoint").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("/connect/revoke", doc.GetProperty("revocation_endpoint").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://app.example.in/a?b=c", "https://id.sangamid.in/", "https://app.example.in/a?b=c&iss=https%3A%2F%2Fid.sangamid.in%2F&sid=")]
    [InlineData("https://app.example.in/a", "http://localhost/", "https://app.example.in/a?iss=http%3A%2F%2Flocalhost%2F&sid=")]
    public void FrontChannelAddresses_CarryTheIssuerAndSession(string uri, string issuer, string expectedPrefix)
    {
        Guid sid = Guid.NewGuid();
        Assert.Equal(expectedPrefix + sid.ToString("D"), Pages.Account.LogoutModel.WithLogoutParameters(uri, issuer, sid));
    }

    [PostgresFact]
    public async Task SigningOut_FramesTheFrontChannelPage_AndQueuesASignedLogoutToken()
    {
        await SetSampleLogoutUrisAsync(BackChannel, FrontChannel);
        try
        {
            using BrowserSession s = new(_factory);
            (Guid userId, _) = await RegisterAsync(s);
            (JsonElement idToken, _) = await SignInToSampleAsync(s);
            string sid = idToken.GetProperty("sid").GetString()!;

            (_, string? location, string html) = await s.PostFormAsync("/logout", []);
            Assert.Null(location);
            Assert.Contains("You're signed out of Sangam", WebUtility.HtmlDecode(html), StringComparison.Ordinal);
            string expectedFrame = HttpUtility.HtmlAttributeEncode(FrontChannel + "&iss=" + Uri.EscapeDataString("http://localhost/") + "&sid=" + sid);
            Assert.Contains("src=\"" + expectedFrame + "\"", html, StringComparison.Ordinal);
            Assert.Contains("Sangam development sample", html, StringComparison.Ordinal);

            // The browser session is over.
            (_, string account, _) = await s.FollowAsync("/account");
            Assert.Contains("/login", account, StringComparison.Ordinal);

            // The back channel: one queued notification, delivered as a logout token signed with the published keys.
            List<(Uri Uri, string Token)> posted = [];
            using RecordingHandler handler = new(HttpStatusCode.OK, posted);
            using BackChannelLogoutSender sender = Sender(handler);
            Assert.Equal(1, await sender.DeliverDueAsync(CancellationToken.None));
            (Uri postedTo, string token) = Assert.Single(posted);
            Assert.Equal(new Uri(BackChannel), postedTo);

            using HttpClient api = _factory.CreateClient();
            JsonElement discovery = JsonDocument.Parse(await api.GetStringAsync(new Uri("/.well-known/openid-configuration", UriKind.Relative))).RootElement;
            JsonWebKeySet keys = new(await api.GetStringAsync(new Uri(discovery.GetProperty("jwks_uri").GetString()!)));
            TokenValidationResult validated = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidIssuer = discovery.GetProperty("issuer").GetString(),
                ValidAudience = DevelopmentSeeder.SampleClientId,
                IssuerSigningKeys = keys.GetSigningKeys(),
                ValidTypes = ["logout+jwt"],
            });
            Assert.True(validated.IsValid, validated.Exception?.Message);
            Assert.Equal(userId.ToString("D"), validated.Claims["sub"]);
            Assert.Equal(sid, validated.Claims["sid"]);
            Assert.False(validated.Claims.ContainsKey("nonce"));
            Assert.Contains(BackChannelLogoutSender.LogoutEvent, JsonSerializer.Serialize(validated.Claims["events"]), StringComparison.Ordinal);

            // Delivered once only.
            Assert.Equal(0, await sender.DeliverDueAsync(CancellationToken.None));
        }
        finally
        {
            await SetSampleLogoutUrisAsync(null, null);
        }
    }

    [PostgresFact]
    public async Task AnApplicationThatDoesNotAnswer_IsTriedAgainLater_NotForever()
    {
        await SetSampleLogoutUrisAsync(BackChannel, null);
        try
        {
            using BrowserSession s = new(_factory);
            await RegisterAsync(s);
            await SignInToSampleAsync(s);
            (_, string? location, _) = await s.PostFormAsync("/logout", []);
            Assert.StartsWith("/login?signedout=", location, StringComparison.Ordinal);

            List<(Uri Uri, string Token)> posted = [];
            using RecordingHandler handler = new(HttpStatusCode.InternalServerError, posted);
            using BackChannelLogoutSender sender = Sender(handler);
            Assert.Equal(0, await sender.DeliverDueAsync(CancellationToken.None));
            Assert.NotEmpty(posted);

            using IServiceScope scope = _factory.Services.CreateScope();
            SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
            LogoutNotification n = await db.LogoutNotifications.OrderByDescending(x => x.CreatedAt).FirstAsync();
            Assert.Null(n.SentAt);
            Assert.Equal(1, n.Attempts);
            Assert.Equal("HTTP 500", n.LastError);
            Assert.True(n.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(20));
        }
        finally
        {
            await SetSampleLogoutUrisAsync(null, null);
        }
    }

    [PostgresFact]
    public async Task AnAppInitiatedSignOut_FramesFirst_ThenReturnsToTheApplication()
    {
        await SetSampleLogoutUrisAsync(null, FrontChannel);
        try
        {
            using BrowserSession s = new(_factory);
            await RegisterAsync(s);
            (_, string rawIdToken) = await SignInToSampleAsync(s);
            string endSession = "/connect/endsession?id_token_hint=" + Uri.EscapeDataString(rawIdToken)
                + "&post_logout_redirect_uri=" + Uri.EscapeDataString(DevelopmentSeeder.SamplePostLogoutRedirectUri) + "&state=bye";

            Dictionary<string, string> fields = new()
            {
                ["id_token_hint"] = rawIdToken,
                ["post_logout_redirect_uri"] = DevelopmentSeeder.SamplePostLogoutRedirectUri,
                ["state"] = "bye",
            };
            (_, string? first, string framesPage) = await s.PostFormAsync(endSession, fields);
            Assert.Null(first);
            Assert.Contains("class=\"sg-frontchannel\"", framesPage, StringComparison.Ordinal);
            Assert.Contains("name=\"frames_done\" value=\"1\"", framesPage, StringComparison.Ordinal);
            Assert.Contains("name=\"post_logout_redirect_uri\"", framesPage, StringComparison.Ordinal);

            // The page's form re-posts the request; OpenIddict then sends the browser back to the application.
            (_, string? back, _) = await s.SubmitAsync(endSession, framesPage, new Dictionary<string, string>(fields) { ["frames_done"] = "1" });
            Assert.StartsWith(DevelopmentSeeder.SamplePostLogoutRedirectUri, back, StringComparison.Ordinal);
            Assert.Contains("state=bye", back, StringComparison.Ordinal);
        }
        finally
        {
            await SetSampleLogoutUrisAsync(null, null);
        }
    }

    [PostgresFact]
    public async Task ABackEnd_CanIntrospectAToken_AndRevokeIt()
    {
        using HttpClient api = _factory.CreateClient();
        using FormUrlEncodedContent tokenForm = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_secret"] = DevelopmentSeeder.SampleClientSecret,
            ["scope"] = "sangam.manage",
        });
        using HttpResponseMessage issued = await api.PostAsync(new Uri("/connect/token", UriKind.Relative), tokenForm);
        string accessToken = JsonDocument.Parse(await issued.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString()!;

        JsonElement before = await IntrospectAsync(api, accessToken);
        Assert.True(before.GetProperty("active").GetBoolean(), before.ToString());
        Assert.Equal(DevelopmentSeeder.SampleClientId, before.GetProperty("client_id").GetString());

        using FormUrlEncodedContent revokeForm = new(Credentials(accessToken));
        using HttpResponseMessage revoked = await api.PostAsync(new Uri("/connect/revoke", UriKind.Relative), revokeForm);
        Assert.True(revoked.IsSuccessStatusCode, await revoked.Content.ReadAsStringAsync());

        JsonElement after = await IntrospectAsync(api, accessToken);
        Assert.False(after.GetProperty("active").GetBoolean());

        // Without the client's secret, introspection is refused.
        using FormUrlEncodedContent anonymous = new(new Dictionary<string, string> { ["token"] = accessToken, ["client_id"] = DevelopmentSeeder.SampleClientId });
        using HttpResponseMessage refused = await api.PostAsync(new Uri("/connect/introspect", UriKind.Relative), anonymous);
        Assert.False(refused.IsSuccessStatusCode);
    }

    private static Dictionary<string, string> Credentials(string token) => new()
    {
        ["token"] = token,
        ["client_id"] = DevelopmentSeeder.SampleClientId,
        ["client_secret"] = DevelopmentSeeder.SampleClientSecret,
    };

    private static async Task<JsonElement> IntrospectAsync(HttpClient api, string token)
    {
        using FormUrlEncodedContent form = new(Credentials(token));
        using HttpResponseMessage response = await api.PostAsync(new Uri("/connect/introspect", UriKind.Relative), form);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private BackChannelLogoutSender Sender(HttpMessageHandler handler)
        => new(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new SingleClientFactory(handler),
            _factory.Services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>(),
            _factory.Services.GetRequiredService<IConfiguration>(),
            NullLogger<BackChannelLogoutSender>.Instance);

    private async Task SetSampleLogoutUrisAsync(string? back, string? front)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        App app = await db.Apps.FirstAsync(a => a.ClientId == DevelopmentSeeder.SampleClientId);
        app.BackChannelLogoutUri = back;
        app.FrontChannelLogoutUri = front;
        await db.SaveChangesAsync();

        // Nothing left over from another test may be delivered by this one.
        await db.LogoutNotifications.Where(n => n.SentAt == null).ExecuteUpdateAsync(u => u.SetProperty(n => n.SentAt, DateTimeOffset.UtcNow));
    }

    private async Task<(JsonElement IdToken, string Raw)> SignInToSampleAsync(BrowserSession s)
    {
        string verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string authorize = "/connect/authorize?client_id=" + DevelopmentSeeder.SampleClientId
            + "&redirect_uri=" + Uri.EscapeDataString(DevelopmentSeeder.SampleRedirectUri)
            + "&response_type=code&scope=" + Uri.EscapeDataString("openid profile email")
            + "&state=xyz&code_challenge=" + challenge + "&code_challenge_method=S256";

        (_, string path, _) = await s.FollowAsync(authorize);
        string redirect = path;
        if (path.StartsWith("/consent", StringComparison.Ordinal))
        {
            (_, string? allow, _) = await s.PostFormAsync(path, [], handler: "Allow");
            (_, redirect, _) = allow!.StartsWith('/') ? await s.FollowAsync(allow) : (HttpStatusCode.Found, allow, string.Empty);
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
        string jwt = JsonDocument.Parse(body).RootElement.GetProperty("id_token").GetString()!;
        string payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return (JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement, jwt);
    }

    private async Task<(Guid UserId, string Email)> RegisterAsync(BrowserSession s)
    {
        string email = $"logout-{Guid.NewGuid():N}@example.in";
        (HttpStatusCode st, string? loc, string html) = await s.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["FirstName"] = "Lakshmi",
            ["LastName"] = "Pillai",
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

    [GeneratedRegex("[0-9]{6}")]
    private static partial Regex CodeRegex();

    private sealed class RecordingHandler(HttpStatusCode status, List<(Uri Uri, string Token)> posted) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            posted.Add((request.RequestUri!, HttpUtility.ParseQueryString(body)["logout_token"]!));
            return new HttpResponseMessage(status);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
