using System.Globalization;

namespace Sangam.Identity.Infrastructure.Policies;

/// <summary>
/// The breached-password list's import and refresh tool (D-J), run through the identity server's own image:
/// <c>breach-list import --source &lt;download&gt; --output &lt;list file&gt; --date yyyy-MM-dd [--min-count N]</c>, and
/// <c>breach-list status --list &lt;list file&gt;</c>. Refresh every few months: download a new copy with the official
/// Pwned Passwords downloader, import it to the same output path, and the running hosts pick it up within a minute.
/// </summary>
public static class BreachListCommand
{
    /// <summary>The first argument that selects this tool.</summary>
    public const string Verb = "breach-list";

    /// <summary>Runs the tool; returns the process exit code.</summary>
    /// <param name="args">The arguments after <see cref="Verb"/>.</param>
    /// <param name="output">Where to write what happened.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i + 1 < args.Count; i += 2)
        {
            options[args[i].TrimStart('-')] = args[i + 1];
        }

        string command = args.Count > 0 ? args[0] : string.Empty;
        if (command == "import" && options.TryGetValue("source", out string? source) && options.TryGetValue("output", out string? destination)
            && options.TryGetValue("date", out string? dateText) && DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
        {
            int minimum = options.TryGetValue("min-count", out string? m) && int.TryParse(m, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 1;
            try
            {
                long count = PwnedPasswordList.Build(source, destination, date, minimum, cancellationToken);
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Imported {count:N0} hashes (seen at least {minimum} time(s)), list dated {date:yyyy-MM-dd}, into {destination}.")).ConfigureAwait(false); // i18n-ignore (a command-line tool)
                return 0;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                await output.WriteLineAsync("Import failed: " + ex.Message).ConfigureAwait(false);
                return 1;
            }
        }

        if (command == "status" && options.TryGetValue("list", out string? list))
        {
            (PwnedPasswordList.Header? header, string? problem) = PwnedPasswordList.ReadHeader(list);
            await output.WriteLineAsync(header is null
                ? "Not usable: " + problem
                : string.Create(CultureInfo.InvariantCulture, $"{header.Entries:N0} hashes, list dated {header.ListDate:yyyy-MM-dd}, breach count at least {header.MinimumCount}.")).ConfigureAwait(false); // i18n-ignore (a command-line tool)
            return header is null ? 1 : 0;
        }

        await output.WriteLineAsync("Usage: breach-list import --source <file or directory> --output <list file> --date yyyy-MM-dd [--min-count N]").ConfigureAwait(false);
        await output.WriteLineAsync("       breach-list status --list <list file>").ConfigureAwait(false);
        return 2;
    }
}
