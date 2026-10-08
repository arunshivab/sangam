using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Monitoring;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Monitoring;

/// <summary>
/// Every minute, on the one host that evaluates (D-H): calls Anjal's health endpoint, takes the monitoring snapshot,
/// and alerts the founder by e-mail and SMS through Anjal when a threshold is crossed — once when a condition opens,
/// then every few hours while it stays open. Prunes old metric rows once an hour.
/// </summary>
public sealed partial class AlertEvaluator : BackgroundService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly IServiceScopeFactory _scopes;
    private readonly MonitoringOptions _options;
    private readonly ILogger<AlertEvaluator> _logger;
    private DateTimeOffset _prunedAt = DateTimeOffset.MinValue;

    /// <summary>Initialises the evaluator.</summary>
    /// <param name="scopes">Scope factory.</param>
    /// <param name="options">Monitoring settings.</param>
    /// <param name="logger">Logger.</param>
    public AlertEvaluator(IServiceScopeFactory scopes, MonitoringOptions options, ILogger<AlertEvaluator> logger)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>One round: watch Anjal, evaluate, alert, prune. Returns the conditions open now.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<AlertCondition>> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await CheckAnjalAsync(cancellationToken).ConfigureAwait(false);
        using IServiceScope scope = _scopes.CreateScope();
        MetricsRecorder recorder = scope.ServiceProvider.GetRequiredService<MetricsRecorder>();
        try
        {
            await recorder.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
        {
            // The database is down: nothing can be recorded, but the founder must still be told (below).
            LogFlushFailed(ex);
        }

        EfMonitoringService monitoring = scope.ServiceProvider.GetRequiredService<EfMonitoringService>();
        MonitoringSnapshot snapshot = await monitoring.BuildAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AlertCondition> open = AlertRules.Evaluate(snapshot, _options.Alerts, _options.ExpectedHostList, !string.IsNullOrWhiteSpace(_options.AnjalHealthUrl));
        if (!snapshot.Database.Up)
        {
            // Nothing can be recorded without the database; tell the founder directly.
            await scope.ServiceProvider.GetRequiredService<IPlatformAlerts>().SendAsync(open[0].Summary, open[0].Details, cancellationToken).ConfigureAwait(false);
            return open;
        }

        IDbContextFactory<SangamDbContext> contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SangamDbContext>>();
        IPlatformAlerts alerts = scope.ServiceProvider.GetRequiredService<IPlatformAlerts>();
        SangamDbContext db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            DateTimeOffset now = snapshot.At;
            Dictionary<string, MonitoringAlert> known = await db.MonitoringAlerts.ToDictionaryAsync(a => a.Key, cancellationToken).ConfigureAwait(false);
            List<AlertCondition> toSend = [];
            foreach (AlertCondition condition in open)
            {
                if (!known.TryGetValue(condition.Key, out MonitoringAlert? row))
                {
                    db.MonitoringAlerts.Add(new MonitoringAlert { Key = condition.Key, Summary = condition.Summary, OpenedAt = now, LastSentAt = now });
                    toSend.Add(condition);
                }
                else if (row.ResolvedAt is not null)
                {
                    (row.OpenedAt, row.ResolvedAt, row.LastSentAt, row.Summary) = (now, null, now, condition.Summary);
                    toSend.Add(condition);
                }
                else if (now - row.LastSentAt >= TimeSpan.FromHours(Math.Max(1, _options.Alerts.RepeatHours)))
                {
                    (row.LastSentAt, row.Summary) = (now, condition.Summary);
                    toSend.Add(condition);
                }
            }

            HashSet<string> openKeys = [.. open.Select(o => o.Key)];
            foreach (MonitoringAlert row in known.Values.Where(r => r.ResolvedAt is null && !openKeys.Contains(r.Key)))
            {
                row.ResolvedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            foreach (AlertCondition condition in toSend)
            {
                await alerts.SendAsync(condition.Summary, condition.Details, cancellationToken).ConfigureAwait(false);
            }

            if (now - _prunedAt > TimeSpan.FromHours(1))
            {
                _prunedAt = now;
                DateTimeOffset cutoff = now.AddDays(-Math.Max(7, _options.RetentionDays));
                await db.MetricPoints.Where(p => p.Minute < cutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return open;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Evaluate)
        {
            return;
        }

        using PeriodicTimer timer = new(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(ex);
            }
        }
    }

    private async Task CheckAnjalAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AnjalHealthUrl))
        {
            return;
        }

        bool up;
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(new Uri(_options.AnjalHealthUrl, UriKind.Absolute), cancellationToken).ConfigureAwait(false);
            up = response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            up = false;
        }

        SangamMetrics.AnjalCheckCount.Add(1, new KeyValuePair<string, object?>("status", up ? "up" : "down"));
    }

    [LoggerMessage(EventId = 1603, Level = LogLevel.Warning, Message = "Writing this minute's metrics before evaluating failed")]
    private partial void LogFlushFailed(Exception exception);

    [LoggerMessage(EventId = 1602, Level = LogLevel.Error, Message = "Evaluating the monitoring thresholds failed")]
    private partial void LogFailed(Exception exception);
}
