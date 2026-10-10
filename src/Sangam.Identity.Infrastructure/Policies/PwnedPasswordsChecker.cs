using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Security;

namespace Sangam.Identity.Infrastructure.Policies;

/// <summary>
/// The breached-password check (rc.5, D-J revised; ASVS V2.1.7). First the built-in list of the most common passwords
/// (<see cref="PasswordStrength.IsCommon"/>), which needs no network. Then the Pwned Passwords range service by
/// k-anonymity: only the first five hexadecimal characters of the password's SHA-1 leave Sangam, the answer is padded
/// with decoys, and the match is made here. When the service does not answer in time, the built-in list is the
/// check — nobody is ever blocked by an outage — and the monitoring page says how long the service has been
/// unreachable. The full downloadable list (more than 50 GB) is not kept on the server.
/// </summary>
public sealed partial class PwnedPasswordsChecker : IBreachedPasswordChecker, IBreachListStatus
{
    /// <summary>The named HTTP client for the range service.</summary>
    public const string HttpClientName = "pwned-passwords";

    private readonly IHttpClientFactory _http;
    private readonly PolicySettings _settings;
    private readonly IClock _clock;
    private readonly ILogger<PwnedPasswordsChecker> _logger;
    private readonly Lock _gate = new();
    private DateTimeOffset? _lastSuccess;
    private DateTimeOffset? _failingSince;
    private string? _lastProblem;

    /// <summary>Initialises the checker.</summary>
    /// <param name="http">HTTP clients.</param>
    /// <param name="settings">Settings (switch, endpoint, timeout).</param>
    /// <param name="clock">Clock.</param>
    /// <param name="logger">Logger.</param>
    public PwnedPasswordsChecker(IHttpClientFactory http, PolicySettings settings, IClock clock, ILogger<PwnedPasswordsChecker> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool Available => _settings.BreachCheckEnabled;

    /// <inheritdoc />
    public BreachListStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new BreachListStatus(
                    _settings.BreachCheckEnabled,
                    _settings.BreachCheckEndpoint is not null,
                    PasswordStrength.CommonPasswordCount,
                    _lastSuccess,
                    _failingSince,
                    _failingSince is null ? null : _lastProblem);
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool?> IsBreachedAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!Available)
        {
            return null;
        }

        if (password.Length == 0 || PasswordStrength.IsCommon(password))
        {
            return password.Length > 0;
        }

        if (_settings.BreachCheckEndpoint is not Uri endpoint)
        {
            return false;
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
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri(endpoint, prefix));
            request.Headers.Add("Add-Padding", "true");
            using HttpResponseMessage response = await _http.CreateClient(HttpClientName).SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Failed(string.Create(CultureInfo.InvariantCulture, $"The service answered {(int)response.StatusCode}."));
                return false;
            }

            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            Succeeded();
            return Contains(body, suffix);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            Failed(ex is HttpRequestException ? "The service could not be reached." : "The service did not answer in time.");
            return false;
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

    private void Succeeded()
    {
        lock (_gate)
        {
            _lastSuccess = _clock.UtcNow;
            _failingSince = null;
            _lastProblem = null;
        }
    }

    private void Failed(string problem)
    {
        lock (_gate)
        {
            _failingSince ??= _clock.UtcNow;
            _lastProblem = problem;
        }

        LogUnavailable(problem);
    }

    [LoggerMessage(EventId = 1201, Level = LogLevel.Warning, Message = "The breached-password service is unavailable ({Problem}); the password was checked against the built-in list only")]
    private partial void LogUnavailable(string problem);
}
