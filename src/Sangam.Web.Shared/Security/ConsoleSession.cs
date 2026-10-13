using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Sangam.Identity.Domain;
using Sangam.Web.Shared.Hosting;

namespace Sangam.Web.Shared.Security;

/// <summary>
/// rc.10 (founder decision, 12 Oct 2026): the operator and partner consoles open only for a session that was itself
/// signed in with two-step sign-in (or a passkey), not merely for an account that has an authenticator. The consoles
/// ask the identity server for <c>acr_values=urn:sangam:acr:2</c> at every sign-in and keep the <c>acr</c> claim; this
/// reads it, and the facts the consoles' session line shows.
/// </summary>
public static class ConsoleSession
{
    /// <summary>The level a console session must have been signed in at (two factors; a passkey is 3).</summary>
    public const int RequiredLevel = 2;

    /// <summary>The claim that carries the level the identity server says the person signed in at.</summary>
    public const string LevelClaim = "acr";

    /// <summary>The level this session was signed in at: 0 when unknown, 1 one factor, 2 two-step, 3 a passkey.</summary>
    /// <param name="principal">The signed-in person.</param>
    /// <returns>The level.</returns>
    public static int Level(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return AuthenticationAssurance.Level(principal.FindFirstValue(LevelClaim));
    }

    /// <summary>Whether the session was signed in strongly enough for a console.</summary>
    /// <param name="principal">The signed-in person.</param>
    /// <returns><see langword="true"/> for two-step sign-in or a passkey.</returns>
    public static bool IsTwoStep(ClaimsPrincipal principal) => Level(principal) >= RequiredLevel;

    /// <summary>When the session ends at the latest (its absolute lifetime), or <see langword="null"/> when it does not say.</summary>
    /// <param name="principal">The signed-in person.</param>
    /// <returns>The time.</returns>
    public static DateTimeOffset? EndsAt(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return long.TryParse(principal.FindFirstValue(SessionLifetime.SinceClaim), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) + SessionLifetime.Absolute
            : null;
    }

    /// <summary>
    /// The environment as the strip names it: the host environment in capitals and where it runs
    /// (<c>Sangam:Region</c>, India unless set), for example <c>PRODUCTION · India</c>.
    /// </summary>
    /// <param name="environmentName">The host environment's name.</param>
    /// <param name="configuration">Configuration.</param>
    /// <returns>The label.</returns>
    public static string EnvironmentLabel(string environmentName, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(configuration);
        string region = configuration["Sangam:Region"] is { Length: > 0 } r ? r : "India";
        return environmentName.ToUpperInvariant() + " · " + region;
    }

    /// <summary>Whether this is the production environment (the strip is coloured differently everywhere else).</summary>
    /// <param name="environmentName">The host environment's name.</param>
    /// <returns><see langword="true"/> for Production.</returns>
    public static bool IsProduction(string environmentName) => string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase);
}
