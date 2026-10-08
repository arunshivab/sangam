using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Sangam.Identity.Infrastructure.Messaging;

/// <summary>What Anjal said about one message after every attempt.</summary>
/// <param name="Accepted">Whether Anjal accepted it.</param>
/// <param name="MessageId">Anjal's id for the message, when accepted.</param>
/// <param name="Error">Why it was not accepted (an HTTP status or an exception type; never personal data).</param>
/// <param name="Attempts">How many attempts were made.</param>
public sealed record AnjalResult(bool Accepted, string? MessageId, string? Error, int Attempts);

/// <summary>
/// Posts messages to Anjal's API (D-B, D-M): the API key in the configured header, an idempotency key that stays
/// the same across retries (so a retry after a lost answer cannot send twice), and retries with growing, jittered
/// pauses on timeouts, 408, 429 and 5xx — honouring <c>Retry-After</c>. Other 4xx answers are final. Logs carry a
/// masked recipient and the outcome only; never a subject, body or text, which carry one-time codes (OI-038).
/// </summary>
public sealed partial class AnjalClient
{
    /// <summary>The header that carries the idempotency key.</summary>
    public const string IdempotencyHeader = "Idempotency-Key";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly AnjalOptions _options;
    private readonly ILogger<AnjalClient> _logger;

    /// <summary>Initialises the client.</summary>
    /// <param name="http">An HTTP client whose base address is Anjal's.</param>
    /// <param name="options">Anjal settings.</param>
    /// <param name="logger">Logger.</param>
    public AnjalClient(HttpClient http, AnjalOptions options, ILogger<AnjalClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The pause between attempts; replaced in tests so they need not wait.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>Posts one message, retrying as described on the class.</summary>
    /// <param name="path">The endpoint, relative to the base address.</param>
    /// <param name="body">The request body (serialised as JSON).</param>
    /// <param name="attempts">How many attempts at most.</param>
    /// <param name="maskedRecipient">The recipient, already masked, for the log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<AnjalResult> PostAsync<TBody>(string path, TBody body, int attempts, string maskedRecipient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(maskedRecipient);
        string idempotencyKey = Guid.NewGuid().ToString("N");
        string error = "not attempted";
        int made = 0;
        for (int attempt = 1; attempt <= Math.Max(1, attempts); attempt++)
        {
            made = attempt;
            TimeSpan? askedToWait = null;
            using HttpRequestMessage request = new(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };
            request.Headers.TryAddWithoutValidation(_options.ApiKeyHeader, string.IsNullOrEmpty(_options.ApiKeyScheme) ? _options.ApiKey : _options.ApiKeyScheme + " " + _options.ApiKey);
            request.Headers.TryAddWithoutValidation(IdempotencyHeader, idempotencyKey);
            try
            {
                using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    string? id = await ReadIdAsync(response, cancellationToken).ConfigureAwait(false);
                    LogAccepted(maskedRecipient, id ?? string.Empty, attempt);
                    return new AnjalResult(true, id, null, attempt);
                }

                error = "HTTP " + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!IsTransient(response.StatusCode))
                {
                    LogRefused(maskedRecipient, error);
                    return new AnjalResult(false, null, error, attempt);
                }

                askedToWait = RetryAfter(response.Headers.RetryAfter);
            }
            catch (HttpRequestException ex)
            {
                error = ex.GetType().Name;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                error = "timeout";
            }

            LogAttemptFailed(maskedRecipient, attempt, error);
            if (attempt < attempts)
            {
                await Delay(PauseBefore(attempt + 1, askedToWait), cancellationToken).ConfigureAwait(false);
            }
        }

        LogGaveUp(maskedRecipient, made, error);
        return new AnjalResult(false, null, error, made);
    }

    /// <summary>Whether an answer is worth retrying.</summary>
    /// <param name="status">The status code.</param>
    public static bool IsTransient(HttpStatusCode status)
        => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private TimeSpan PauseBefore(int attempt, TimeSpan? askedToWait)
    {
        if (askedToWait is TimeSpan asked)
        {
            return asked > _options.MaxRetryDelay ? _options.MaxRetryDelay : asked;
        }

        double factor = Math.Pow(2, attempt - 2) * (0.8 + (Random.Shared.NextDouble() * 0.4));
        TimeSpan pause = TimeSpan.FromMilliseconds(_options.RetryDelay.TotalMilliseconds * factor);
        return pause > _options.MaxRetryDelay ? _options.MaxRetryDelay : pause;
    }

    private static TimeSpan? RetryAfter(RetryConditionHeaderValue? header)
    {
        if (header?.Delta is TimeSpan delta)
        {
            return delta;
        }

        return header?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : null;
    }

    private static async Task<string?> ReadIdAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            AnjalAccepted? accepted = await response.Content.ReadFromJsonAsync<AnjalAccepted>(Json, cancellationToken).ConfigureAwait(false);
            return accepted?.Id;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(EventId = 1301, Level = LogLevel.Information, Message = "Anjal accepted a message to {To} (id {MessageId}, attempt {Attempt}); content is not logged")]
    private partial void LogAccepted(string to, string messageId, int attempt);

    [LoggerMessage(EventId = 1302, Level = LogLevel.Warning, Message = "Anjal refused a message to {To}: {Error}; not retried")]
    private partial void LogRefused(string to, string error);

    [LoggerMessage(EventId = 1303, Level = LogLevel.Information, Message = "Attempt {Attempt} to hand a message to {To} to Anjal failed: {Error}")]
    private partial void LogAttemptFailed(string to, int attempt, string error);

    [LoggerMessage(EventId = 1304, Level = LogLevel.Warning, Message = "Gave up handing a message to {To} to Anjal after {Attempts} attempts: {Error}")]
    private partial void LogGaveUp(string to, int attempts, string error);

    private sealed record AnjalAccepted(string? Id);
}
