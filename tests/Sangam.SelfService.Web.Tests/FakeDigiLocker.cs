using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Sangam.SelfService.Web.Tests;

/// <summary>
/// A stand-in for DigiLocker's token and user-details endpoints on a real port, as its Requester API Specification
/// describes them: the code is exchanged only with the client's secret, the registered redirect address and a verifier
/// whose S256 hash is the challenge the authorisation request carried.
/// </summary>
public sealed class FakeDigiLocker
{
    public const string ClientId = "sangam-test";
    public const string ClientSecret = "digilocker-test-secret";

    private static readonly Lazy<FakeDigiLocker> Instance = new(() => StartAsync().GetAwaiter().GetResult());
    private readonly ConcurrentDictionary<string, Grant> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Grant> _tokens = new(StringComparer.Ordinal);

    private FakeDigiLocker()
    {
    }

    public static FakeDigiLocker Shared => Instance.Value;

    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>Token requests that carried a wrong verifier, secret or redirect address.</summary>
    public int Refused { get; private set; }

    /// <summary>Issues a code for an authorisation request with this challenge.</summary>
    public string Issue(string challenge, string redirectUri, string digiLockerId, string name, string dob, string gender, bool identityInToken = true)
    {
        string code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        _codes[code] = new Grant(challenge, redirectUri, digiLockerId, name, dob, gender, identityInToken);
        return code;
    }

    private static async Task<FakeDigiLocker> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        FakeDigiLocker fake = new();
        app.MapPost("/public/oauth2/1/token", async (HttpContext context) =>
        {
            IFormCollection form = await context.Request.ReadFormAsync();
            string verifier = form["code_verifier"].ToString();
            string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (form["grant_type"] != "authorization_code" || form["client_id"] != ClientId || form["client_secret"] != ClientSecret
                || !fake._codes.TryRemove(form["code"].ToString(), out Grant? grant) || grant.Challenge != challenge || grant.RedirectUri != form["redirect_uri"])
            {
                fake.Refused++;
                return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
            }

            string token = "at-" + Guid.NewGuid().ToString("N");
            fake._tokens[token] = grant;
            return grant.IdentityInToken
                ? Results.Json(new Dictionary<string, object> { ["access_token"] = token, ["token_type"] = "Bearer", ["expires_in"] = 3600, ["digilockerid"] = grant.DigiLockerId, ["name"] = grant.Name, ["dob"] = grant.Dob, ["gender"] = grant.Gender, ["eaadhaar"] = "Y", ["reference_key"] = "ref-1" })
                : Results.Json(new Dictionary<string, object> { ["access_token"] = token, ["token_type"] = "Bearer", ["expires_in"] = 3600 });
        });
        app.MapGet("/public/oauth2/1/user", (HttpContext context) =>
        {
            string header = context.Request.Headers.Authorization.ToString();
            return header.StartsWith("Bearer ", StringComparison.Ordinal) && fake._tokens.TryGetValue(header["Bearer ".Length..], out Grant? grant)
                ? Results.Json(new Dictionary<string, object> { ["digilockerid"] = grant.DigiLockerId, ["name"] = grant.Name, ["dob"] = grant.Dob, ["gender"] = grant.Gender, ["eaadhaar"] = "Y" })
                : Results.StatusCode(401);
        });
        await app.StartAsync();
        fake.BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        return fake;
    }

    private sealed record Grant(string Challenge, string RedirectUri, string DigiLockerId, string Name, string Dob, string Gender, bool IdentityInToken);
}
