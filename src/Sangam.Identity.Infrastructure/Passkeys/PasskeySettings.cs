using Fido2NetLib;
using Microsoft.Extensions.Configuration;

namespace Sangam.Identity.Infrastructure.Passkeys;

/// <summary>
/// Passkey settings (SGM-205 §6). <c>Sangam:Passkeys:RpId</c> and <c>:Origins</c> (comma-separated)
/// default to the issuer's host and origin, so passkeys stay bound to Sangam's own domain (D-017):
/// a passkey made for sangamid.in will never sign anyone in on a look-alike site.
/// </summary>
public static class PasskeySettings
{
    /// <summary>Builds the Fido2NetLib configuration.</summary>
    /// <param name="configuration">Configuration.</param>
    public static Fido2Configuration ToFido2Configuration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string issuer = configuration["Sangam:Issuer"] is { Length: > 0 } i ? i : "http://localhost:5100";
        Uri origin = new(issuer, UriKind.Absolute);
        string rpId = configuration["Sangam:Passkeys:RpId"] is { Length: > 0 } r ? r : origin.Host;
        string originsSetting = configuration["Sangam:Passkeys:Origins"] ?? string.Empty;
        HashSet<string> origins = [.. originsSetting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        if (origins.Count == 0)
        {
            origins.Add(origin.GetLeftPart(UriPartial.Authority));
        }

        return new Fido2Configuration
        {
            RPID = rpId,
            RPName = configuration["Sangam:Passkeys:RpName"] ?? "Sangam",
            Origins = origins,
            Timeout = (uint)EfPasskeyService.ChallengeLifetime.TotalMilliseconds,
        };
    }
}
