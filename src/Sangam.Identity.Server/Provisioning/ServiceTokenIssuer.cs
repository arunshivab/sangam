using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using Sangam.Identity.Infrastructure.Provisioning;

namespace Sangam.Identity.Server.Provisioning;

/// <summary>
/// The tokens Sangam presents to an application's SCIM server in the <c>sangam</c> mode (PR-23, SGM-216 §4): a JWT
/// (<c>typ</c> <c>sangam-service+jwt</c>) signed with Sangam's published token keys, for exactly that SCIM address
/// (<c>aud</c>), lasting five minutes. The application checks it against Sangam's JWKS, as it checks access tokens, and
/// needs no secret shared with Sangam.
/// </summary>
public sealed class ServiceTokenIssuer : IServiceTokenIssuer
{
    /// <summary>The token type.</summary>
    public const string TokenType = "sangam-service+jwt";

    /// <summary>The subject: Sangam itself.</summary>
    public const string Subject = "sangam";

    private readonly IOptionsMonitor<OpenIddictServerOptions> _server;
    private readonly IConfiguration _configuration;

    /// <summary>Initialises the issuer.</summary>
    /// <param name="server">OpenIddict options, for the signing key and the issuer.</param>
    /// <param name="configuration">Configuration (<c>Sangam:Issuer</c>).</param>
    public ServiceTokenIssuer(IOptionsMonitor<OpenIddictServerOptions> server, IConfiguration configuration)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <inheritdoc />
    public string Issue(string audience, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(audience);
        SigningCredentials credentials = _server.CurrentValue.SigningCredentials.FirstOrDefault()
            ?? throw new InvalidOperationException("No token-signing credentials are configured.");
        string issuer = _server.CurrentValue.Issuer?.AbsoluteUri
            ?? (_configuration["Sangam:Issuer"] is { Length: > 0 } configured ? new Uri(configured, UriKind.Absolute).AbsoluteUri : "http://localhost/");
        DateTime now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            TokenType = TokenType,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(lifetime),
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object>
            {
                ["jti"] = Guid.NewGuid().ToString("N"),
                ["sub"] = Subject,
                ["scope"] = "scim",
            },
        });
    }
}
