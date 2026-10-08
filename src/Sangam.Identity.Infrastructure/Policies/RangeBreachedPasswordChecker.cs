using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Security;

namespace Sangam.Identity.Infrastructure.Policies;

/// <summary>
/// Breached-password check by k-anonymity range lookup (PR-16, CAP-024): only the first five hexadecimal
/// characters of the password's SHA-1 leave Sangam; the service answers with every known suffix under that
/// prefix (padded with decoys of count 0), and the match is made here. A service that does not answer in time
/// never blocks anyone: the result is "unknown".
/// </summary>
public sealed partial class RangeBreachedPasswordChecker : IBreachedPasswordChecker
{
    private readonly HttpClient _http;
    private readonly PolicySettings _settings;
    private readonly ILogger<RangeBreachedPasswordChecker> _logger;

    /// <summary>Initialises the checker.</summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="settings">Settings (endpoint, timeout).</param>
    /// <param name="logger">Logger.</param>
    public RangeBreachedPasswordChecker(HttpClient http, PolicySettings settings, ILogger<RangeBreachedPasswordChecker> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool Available => _settings.BreachCheckEnabled;

    /// <inheritdoc />
    public async Task<bool?> IsBreachedAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!Available)
        {
            return null;
        }

#pragma warning disable CA5350 // SHA-1 is what the range service indexes by; it is a lookup key here, not a protection.
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
#pragma warning restore CA5350
        string prefix = hash[..5];
        string suffix = hash[5..];

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_settings.BreachCheckTimeout);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri(_settings.BreachCheckEndpoint, prefix));
            request.Headers.Add("Add-Padding", "true");
            using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogUnavailable((int)response.StatusCode);
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return Contains(body, suffix);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            LogUnavailable(0);
            return null;
        }
    }

    /// <summary>Whether a range answer lists <paramref name="suffix"/> with a count above zero.</summary>
    /// <param name="body">The answer: one <c>SUFFIX:COUNT</c> per line.</param>
    /// <param name="suffix">The 35-character suffix.</param>
    public static bool Contains(string body, string suffix)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(suffix);
        foreach (string line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0
                && string.Equals(line[..colon], suffix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int count)
                && count > 0)
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(EventId = 1201, Level = LogLevel.Warning, Message = "The breached-password service did not answer (status {Status}); the password was not checked")]
    private partial void LogUnavailable(int status);
}
