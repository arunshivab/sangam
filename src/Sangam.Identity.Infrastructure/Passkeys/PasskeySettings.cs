using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;

namespace Sangam.Identity.Infrastructure.Passkeys;

/// <summary>
/// Passkey settings (SGM-205 §6) for ASP.NET Core Identity's built-in passkey support (D-G).
/// <c>Sangam:Passkeys:RpId</c> and <c>:Origins</c> (comma-separated) default to the issuer's host and origin, so
/// passkeys stay bound to Sangam's own domain (D-017): a passkey made for sangamid.in will never sign anyone in on a
/// look-alike site. User verification is required for every ceremony and attestation is not collected (SGM-205 §7).
/// </summary>
public static class PasskeySettings
{
    /// <summary>How long a ceremony may take.</summary>
    public static readonly TimeSpan CeremonyLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The relying-party id (the domain passkeys are bound to).</summary>
    /// <param name="configuration">Configuration.</param>
    public static string RpId(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration["Sangam:Passkeys:RpId"] is { Length: > 0 } r ? r : Issuer(configuration).Host;
    }

    /// <summary>The origins a passkey answer may come from.</summary>
    /// <param name="configuration">Configuration.</param>
    public static IReadOnlySet<string> Origins(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        HashSet<string> origins = new(
            (configuration["Sangam:Passkeys:Origins"] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);
        if (origins.Count == 0)
        {
            origins.Add(Issuer(configuration).GetLeftPart(UriPartial.Authority));
        }

        return origins;
    }

    /// <summary>Applies Sangam's rules to Identity's passkey options.</summary>
    /// <param name="options">Identity's passkey options.</param>
    /// <param name="configuration">Configuration.</param>
    public static void Apply(IdentityPasskeyOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        IReadOnlySet<string> origins = Origins(configuration);
        options.ServerDomain = RpId(configuration);
        options.UserVerificationRequirement = "required";
        options.ResidentKeyRequirement = "preferred";
        options.AttestationConveyancePreference = "none";
        options.AuthenticatorTimeout = CeremonyLifetime;
        options.ValidateOrigin = context => ValueTask.FromResult(!context.CrossOrigin && origins.Contains(context.Origin));
    }

    private static Uri Issuer(IConfiguration configuration)
        => new(configuration["Sangam:Issuer"] is { Length: > 0 } i ? i : "http://localhost:5100", UriKind.Absolute);
}
