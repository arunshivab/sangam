using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sangam.Client.Audit;

/// <summary>Records shared audit events for the current request (SGM-208 §7).</summary>
public interface ISangamAudit
{
    /// <summary>Builds, checks and buffers one event; returns it.</summary>
    /// <param name="entry">What happened.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<JsonObject> RecordAsync(SangamAuditEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>
/// The audit helper: fills in the signed-in person and the request, refuses an event that breaks the schema, and appends
/// it to a durable JSON Lines buffer. <see cref="SangamAuditForwarder"/> sends the buffer to the audit service once it
/// exists (<see cref="SangamAuditOptions.Endpoint"/>); until then the buffer is the record.
/// </summary>
public sealed class SangamAuditRecorder : ISangamAudit
{
    private static readonly SemaphoreSlim FileLock = new(1, 1);
    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly IHttpContextAccessor _http;
    private readonly IOptions<SangamAuditOptions> _options;

    /// <summary>Initialises the recorder.</summary>
    /// <param name="http">The current request.</param>
    /// <param name="options">Audit settings.</param>
    public SangamAuditRecorder(IHttpContextAccessor http, IOptions<SangamAuditOptions> options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<JsonObject> RecordAsync(SangamAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        HttpContext? context = _http.HttpContext;
        JsonObject e = SangamAudit.Build(_options.Value, context?.User, context?.Connection.RemoteIpAddress?.ToString(), context?.Request.Headers.UserAgent.ToString(), entry);
        IReadOnlyList<string> problems = SangamAudit.Validate(e);
        if (problems.Count > 0)
        {
            throw new ArgumentException("The audit event breaks the shared schema: " + string.Join("; ", problems), nameof(entry));
        }

        await AppendAsync(_options.Value.BufferPath, e, cancellationToken).ConfigureAwait(false);
        return e;
    }

    internal static async Task AppendAsync(string path, JsonObject e, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        await FileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(path, e.ToJsonString() + "\n", NoBom, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            FileLock.Release();
        }
    }

    /// <summary>Takes up to <paramref name="max"/> buffered events and the rest, for the forwarder.</summary>
    internal static async Task<(List<string> Batch, List<string> Remaining)> TakeAsync(string path, int max, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return ([], []);
        }

        string[] lines = [.. (await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false)).Where(l => l.Length > 0)];
        return ([.. lines.Take(max)], [.. lines.Skip(max)]);
    }

    /// <summary>Rewrites the buffer with what is still to send.</summary>
    internal static async Task KeepAsync(string path, IEnumerable<string> rest, CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string temporary = path + ".tmp";
            await File.WriteAllLinesAsync(temporary, rest, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            FileLock.Release();
        }
    }
}

/// <summary>
/// Sends buffered events to the audit service in batches of up to 500 (SGM-208 §4), authenticated with a Sangam
/// client-credentials token for <c>audit.write</c>; idempotent by event_id, so a batch sent twice does no harm. Does
/// nothing until <see cref="SangamAuditOptions.Endpoint"/> is set.
/// </summary>
public sealed partial class SangamAuditForwarder : BackgroundService
{
    private readonly IOptions<SangamAuditOptions> _options;
    private readonly SangamManagementClient _tokens;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<SangamAuditForwarder> _logger;

    /// <summary>Initialises the forwarder.</summary>
    /// <param name="options">Audit settings.</param>
    /// <param name="tokens">Gets client-credentials tokens.</param>
    /// <param name="http">HTTP clients.</param>
    /// <param name="logger">Logger.</param>
    public SangamAuditForwarder(IOptions<SangamAuditOptions> options, SangamManagementClient tokens, IHttpClientFactory http, ILogger<SangamAuditForwarder> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Sends one batch; returns how many events were accepted.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> FlushAsync(CancellationToken cancellationToken = default)
    {
        SangamAuditOptions options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            return 0;
        }

        (List<string> batch, List<string> rest) = await SangamAuditRecorder.TakeAsync(options.BufferPath, 500, cancellationToken).ConfigureAwait(false);
        if (batch.Count == 0)
        {
            return 0;
        }

        string token = await _tokens.GetTokenAsync("audit.write", cancellationToken).ConfigureAwait(false);
        JsonArray events = [.. batch.Select(l => JsonNode.Parse(l))];
        using HttpRequestMessage request = new(HttpMethod.Post, options.Endpoint) { Content = JsonContent.Create(new JsonObject { ["events"] = events }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await _http.CreateClient(SangamManagementClient.HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return 0;
        }

        await SangamAuditRecorder.KeepAsync(options.BufferPath, rest, cancellationToken).ConfigureAwait(false);
        return batch.Count;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await FlushAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or InvalidOperationException)
            {
                LogFailed(exception);
            }
        }
    }

    [LoggerMessage(EventId = 7001, Level = LogLevel.Warning, Message = "Sending audit events failed; they stay buffered and are tried again.")]
    private partial void LogFailed(Exception exception);
}
