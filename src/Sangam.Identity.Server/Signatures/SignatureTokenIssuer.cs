using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using Sangam.Identity.Application.Signatures;

namespace Sangam.Identity.Server.Signatures;

/// <summary>
/// Signs signature tokens with the identity server's current token-signing key (PR-17, SGM-207 §5.1), so an
/// application verifies them with the keys it already trusts from <c>/.well-known/jwks</c>. The token has no
/// expiry: it is evidence of a signature, not a credential.
/// </summary>
public sealed class SignatureTokenIssuer : ISignatureTokenIssuer
{
    /// <summary>The token's <c>typ</c> header, so it can never be mistaken for an ID or access token.</summary>
    public const string TokenType = "sangam-signature+jwt";

    private readonly IOptionsMonitor<OpenIddictServerOptions> _options;

    /// <summary>Initialises the issuer.</summary>
    /// <param name="options">The OpenIddict server options, which hold the signing credentials.</param>
    public SignatureTokenIssuer(IOptionsMonitor<OpenIddictServerOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string Issue(SignatureClaims claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        SigningCredentials credentials = _options.CurrentValue.SigningCredentials.FirstOrDefault()
            ?? throw new InvalidOperationException("No token-signing credentials are configured.");

        JsonWebTokenHandler handler = new() { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = claims.Issuer,
            Audience = claims.Audience,
            TokenType = TokenType,
            IssuedAt = claims.SignedAt.UtcDateTime,
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object>
            {
                ["jti"] = claims.RequestId.ToString("D"),
                ["sub"] = claims.Subject.ToString("D"),
                ["name"] = claims.Name,
                ["sig_record_id"] = claims.RecordId,
                ["sig_record_hash"] = claims.RecordHash,
                ["sig_meaning"] = claims.Meaning,
                ["sig_time"] = claims.SignedAt.ToUnixTimeSeconds(),
                ["acr"] = claims.Acr,
                ["amr"] = claims.Methods.ToArray(),
                ["auth_time"] = claims.AuthenticatedAt.ToUnixTimeSeconds(),
                ["sig_time_iso"] = claims.SignedAt.ToString("O", CultureInfo.InvariantCulture),
            },
        });
    }
}
