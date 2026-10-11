using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace Sangam.Identity.Infrastructure.Security;

/// <summary>
/// rc.9 (ASVS V1.6.3): the key-compromise tool, run through the identity server's own image on the server, with the same
/// settings and secrets as the running host (<c>docker compose run --rm identity key-compromise …</c>). Every command needs
/// the incident's reference from the register (SGM-802) and writes it to the audit log.
/// <list type="bullet">
/// <item><c>key-compromise revoke-tokens --incident &lt;ref&gt;</c>: every token and authorization revoked.</item>
/// <item><c>key-compromise revoke-key-ring --incident &lt;ref&gt;</c>: data-protection keys revoked and a new one made;
/// authenticator secrets protected again; everyone signed out.</item>
/// <item><c>key-compromise reverify --incident &lt;ref&gt;</c>: everyone with a verified identity asked to verify again.</item>
/// </list>
/// The archive's own step is <c>audit-archive reencrypt</c>.
/// </summary>
public static class KeyCompromiseCommand
{
    /// <summary>The first argument that selects this tool.</summary>
    public const string Verb = "key-compromise";

    /// <summary>Runs the tool; returns the process exit code.</summary>
    /// <param name="services">The host's services.</param>
    /// <param name="args">The arguments after <see cref="Verb"/>.</param>
    /// <param name="output">Where to write what happened.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<int> RunAsync(IServiceProvider services, IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        string command = args.Count > 0 ? args[0] : string.Empty;
        string? incident = null;
        for (int i = 1; i + 1 < args.Count; i += 2)
        {
            if (string.Equals(args[i], "--incident", StringComparison.OrdinalIgnoreCase))
            {
                incident = args[i + 1].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(incident) || command is not ("revoke-tokens" or "revoke-key-ring" or "reverify"))
        {
            await output.WriteLineAsync("Usage: key-compromise revoke-tokens --incident <reference>").ConfigureAwait(false);
            await output.WriteLineAsync("       key-compromise revoke-key-ring --incident <reference>").ConfigureAwait(false);
            await output.WriteLineAsync("       key-compromise reverify --incident <reference>").ConfigureAwait(false);
            await output.WriteLineAsync("See docs/security/key-compromise.md for when to run each.").ConfigureAwait(false); // i18n-ignore: the operator's console tool is English only
            return 2;
        }

        using IServiceScope scope = services.CreateScope();
        KeyCompromiseService keys = scope.ServiceProvider.GetRequiredService<KeyCompromiseService>();
        try
        {
            string done = command switch
            {
                "revoke-tokens" => await RevokeTokensAsync(keys, incident, cancellationToken).ConfigureAwait(false),
                "revoke-key-ring" => await RevokeKeyRingAsync(keys, incident, cancellationToken).ConfigureAwait(false),
                _ => await ReverifyAsync(keys, incident, cancellationToken).ConfigureAwait(false),
            };
            await output.WriteLineAsync(done).ConfigureAwait(false);
            await output.WriteLineAsync("Written to the audit log. Record this step in the incident.").ConfigureAwait(false); // i18n-ignore: the operator's console tool is English only
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            await output.WriteLineAsync("Failed: " + ex.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<string> RevokeTokensAsync(KeyCompromiseService keys, string incident, CancellationToken cancellationToken)
    {
        (int tokens, int authorizations) = await keys.RevokeAllTokensAsync(incident, cancellationToken).ConfigureAwait(false);
        return string.Create(CultureInfo.InvariantCulture, $"{tokens} tokens and {authorizations} authorizations revoked. Every application signs its people in again."); // i18n-ignore: the operator's console tool is English only
    }

    private static async Task<string> ReverifyAsync(KeyCompromiseService keys, string incident, CancellationToken cancellationToken)
    {
        int asked = await keys.RequestReverificationAsync(incident, cancellationToken).ConfigureAwait(false);
        return string.Create(CultureInfo.InvariantCulture, $"{asked} people asked to verify their identity again. Keep the old subject key under PreviousSubjectKeys until they have."); // i18n-ignore: the operator's console tool is English only
    }

    private static async Task<string> RevokeKeyRingAsync(KeyCompromiseService keys, string incident, CancellationToken cancellationToken)
    {
        (int secrets, int sessions) = await keys.RevokeKeyRingAsync(incident, cancellationToken).ConfigureAwait(false);
        return string.Create(CultureInfo.InvariantCulture, $"Key ring replaced. {secrets} authenticator secrets protected under the new key; {sessions} sessions ended. Restart every Sangam host so none keeps the old key in memory."); // i18n-ignore: the operator's console tool is English only
    }
}
