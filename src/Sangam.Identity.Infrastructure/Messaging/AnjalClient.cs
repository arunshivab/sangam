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
/// pauses on timeouts, 408, 409, 429 and 5xx — honouring <c>Retry-After</c>. Other 4xx answers are final. Anjal's
/// 409 means "the first try with this idempotency key is still sending; ask again shortly" (rc.17, M2). With a signing
/// key (rc.17, M1 (b)) every attempt is signed afresh over the same body bytes, because Anjal refuses a repeated
/// signature. Logs carry a masked recipient and the outcome only; never a subject, body or text, which carry one-time
/// codes (OI-038).
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
    private readonly AnjalRequestSigner? _signer;

    /// <summary>Initialises the client.</summary>
    /// <param name="http">An HTTP client whose base address is Anjal's.</param>
    /// <param name="options">Anjal settings.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="signer">Signs every request (rc.17, M1 (b)); <see langword="null"/> sends the API key only.</param>
    public AnjalClient(HttpClient http, AnjalOptions options, ILogger<AnjalClient> logger, AnjalRequestSigner? signer = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _signer = signer;
        if (_http.BaseAddress is null)
        {
            throw new InvalidOperationException("The Anjal HTTP client needs a base address.");
        }

        if (_signer is not null)
        {
            LogSigning(_signer.KeyId);
        }
    }

    /// <summary>The pause between attempts; replaced in tests so they need not wait.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>The clock for signature timestamps; replaced in tests.</summary>
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>The key id requests are signed with, or <see langword="null"/> when they are not signed.</summary>
    public string? SigningKeyId => _signer?.KeyId;

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
        Uri target = new(_http.BaseAddress!, path);

        // Serialised once: the bytes hashed for the signature are the bytes sent, on every attempt.
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        string error = "not attempted";
        int made = 0;
        for (int attempt = 1; attempt <= Math.Max(1, attempts); attempt++)
        {
            made = attempt;
            TimeSpan? askedToWait = null;
            using ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            using HttpRequestMessage request = new(HttpMethod.Post, target) { Content = content };
            AddKey(request);
            request.Headers.TryAddWithoutValidation(IdempotencyHeader, idempotencyKey);
            _signer?.Sign(request, bytes, Now());
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
                    if (response.StatusCode == HttpStatusCode.Unauthorized && _signer is not null)
                    {
                        LogClockSkew(SkewSeconds(response));
                    }

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

    /// <summary>
    /// rc.17 (M7 (a)): asks Anjal's health endpoint whether Sangam can send — with the API key and, when set, a
    /// signature over an empty body. The key goes only to Anjal's own host; any other address is called without it.
    /// </summary>
    /// <param name="url">The absolute health URL (for example <c>https://mail.anjal.co.in/api/v1/health</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether Anjal answered with success.</returns>
    public async Task<bool> CheckHealthAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri)
        {
            throw new ArgumentException("The health URL must be absolute.", nameof(url));
        }

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        if (IsAnjalHost(url))
        {
            AddKey(request);
            _signer?.Sign(request, ReadOnlySpan<byte>.Empty, Now());
        }

        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Whether an answer is worth retrying.</summary>
    /// <param name="status">The status code.</param>
    public static bool IsTransient(HttpStatusCode status)
        => status is HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private void AddKey(HttpRequestMessage request)
        => request.Headers.TryAddWithoutValidation(_options.ApiKeyHeader, string.IsNullOrEmpty(_options.ApiKeyScheme) ? _options.ApiKey : _options.ApiKeyScheme + " " + _options.ApiKey);

    private bool IsAnjalHost(Uri url)
        => string.Equals(url.Scheme, _http.BaseAddress!.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(url.IdnHost, _http.BaseAddress.IdnHost, StringComparison.OrdinalIgnoreCase)
            && url.Port == _http.BaseAddress.Port;

    private long SkewSeconds(HttpResponseMessage response)
        => response.Headers.Date is DateTimeOffset server ? (long)Math.Round((Now() - server).TotalSeconds) : 0;

    private TimeSpan PauseBefore(int attempt, TimeSpan? askedToWait)
    {
        if (askedToWait is TimeSpan asked)
        {
            if (asked < TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

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

        if (header?.Date is DateTimeOffset date)
        {
            TimeSpan wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
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

    [LoggerMessage(EventId = 1305, Level = LogLevel.Information, Message = "Requests to Anjal are signed with key {KeyId}; register this key's public half in Anjal under Sending systems")]
    private partial void LogSigning(string keyId);

    [LoggerMessage(EventId = 1306, Level = LogLevel.Warning, Message = "Anjal refused a signed request (401). Check the key id and the key's status in Anjal, and the clock: this server's clock differs from the time in Anjal's answer by {SkewSeconds} s (Anjal allows 300; 0 when Anjal sent no Date)")]
    private partial void LogClockSkew(long skewSeconds);

    private sealed record AnjalAccepted(string? Id);
}
