using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Monitoring;

/// <summary>
/// Listens to Sangam's own meters (every meter whose name starts with <c>Sangam.</c>) with .NET's built-in
/// <see cref="MeterListener"/>, adds the measurements up a minute at a time, and writes them to <c>metric_points</c>
/// (D-H). Several hosts write side by side; each row is keyed by its host. No exporter, no third party. Each flush
/// also reports what only this host can see — its breached-password list (V-10) — to <c>host_reports</c>.
/// </summary>
public sealed partial class MetricsRecorder : BackgroundService
{
    private readonly ConcurrentDictionary<(string Name, string Tag), Aggregate> _current = new();
    private readonly IDbContextFactory<SangamDbContext> _contexts;
    private readonly ILogger<MetricsRecorder> _logger;
    private readonly MeterListener _listener = new();
    private readonly IBreachListStatus? _breaches;
    private readonly TimeSpan _interval;
    private readonly bool _enabled;

    /// <summary>Initialises the recorder and starts listening.</summary>
    /// <param name="contexts">Database contexts.</param>
    /// <param name="probe">The host's gauges (created here so they exist before the first collection).</param>
    /// <param name="configuration">Configuration (<c>Sangam:Monitoring</c>).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="breaches">This host's breached-password list, reported every flush (V-10).</param>
    public MetricsRecorder(IDbContextFactory<SangamDbContext> contexts, HostProbe probe, IConfiguration configuration, ILogger<MetricsRecorder> logger, IBreachListStatus? breaches = null)
    {
        _breaches = breaches;
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(configuration);
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enabled = configuration.GetValue("Sangam:Monitoring:Record", true);
        _interval = configuration.GetValue("Sangam:Monitoring:FlushInterval", TimeSpan.FromMinutes(1));
        HostName = HostNameFrom(configuration);
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name.StartsWith("Sangam.", StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Add(instrument, value, tags));
        if (_enabled)
        {
            _listener.Start();
        }
    }

    /// <summary>This host's name in <c>metric_points.host</c>.</summary>
    public string HostName { get; }

    /// <summary>The host name: <c>Sangam:Monitoring:HostName</c>, or the program's name without "Sangam.".</summary>
    /// <param name="configuration">Configuration.</param>
    public static string HostNameFrom(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string name = configuration["Sangam:Monitoring:HostName"] is { Length: > 0 } configured
            ? configured
            : (Assembly.GetEntryAssembly()?.GetName().Name ?? "sangam").Replace("Sangam.", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return name.Length > 40 ? name[..40] : name;
    }

    /// <summary>The <c>host_reports</c> subject of the breached-password list.</summary>
    public const string BreachListSubject = "breach_list";

    /// <summary>Takes the gauges' readings and writes the minute so far; returns how many rows were written.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> FlushAsync(CancellationToken cancellationToken = default)
    {
        await ReportAsync(cancellationToken).ConfigureAwait(false);
        _listener.RecordObservableInstruments();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset minute = new(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
        List<((string Name, string Tag) Key, Aggregate Value)> taken = [];
        foreach ((string Name, string Tag) key in _current.Keys)
        {
            if (_current.TryRemove(key, out Aggregate? value))
            {
                taken.Add((key, value));
            }
        }

        if (taken.Count == 0)
        {
            return 0;
        }

        SangamDbContext db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            foreach (((string name, string tag), Aggregate value) in taken)
            {
                (long count, double sum, double max, bool gauge) = value.Read();
                NpgsqlParameter[] parameters =
                [
                    new NpgsqlParameter("minute", minute),
                    new NpgsqlParameter("host", HostName),
                    new NpgsqlParameter("name", name),
                    new NpgsqlParameter("tag", tag),
                    new NpgsqlParameter("count", count),
                    new NpgsqlParameter("sum", sum),
                    new NpgsqlParameter("max", max),
                ];
                if (gauge)
                {
                    // A gauge keeps its latest reading.
                    await db.Database.ExecuteSqlRawAsync(
                        "INSERT INTO metric_points (minute, host, name, tag, count, sum, max) VALUES (@minute, @host, @name, @tag, @count, @sum, @max) " +
                        "ON CONFLICT (minute, host, name, tag) DO UPDATE SET count = 1, sum = excluded.sum, max = excluded.max",
                        parameters,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "INSERT INTO metric_points (minute, host, name, tag, count, sum, max) VALUES (@minute, @host, @name, @tag, @count, @sum, @max) " +
                        "ON CONFLICT (minute, host, name, tag) DO UPDATE SET count = metric_points.count + excluded.count, sum = metric_points.sum + excluded.sum, max = GREATEST(metric_points.max, excluded.max)",
                        parameters,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return taken.Count;
    }

    /// <summary>Writes this host's reports (V-10): the breached-password list as this host sees it.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ReportAsync(CancellationToken cancellationToken = default)
    {
        if (_breaches is null)
        {
            return;
        }

        string payload = JsonSerializer.Serialize(_breaches.Status);
        SangamDbContext db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO host_reports (host, subject, payload, reported_at) VALUES (@host, @subject, @payload, @at) " +
                "ON CONFLICT (host, subject) DO UPDATE SET payload = excluded.payload, reported_at = excluded.reported_at",
                [
                    new NpgsqlParameter("host", HostName),
                    new NpgsqlParameter("subject", BreachListSubject),
                    new NpgsqlParameter("payload", payload),
                    new NpgsqlParameter("at", DateTimeOffset.UtcNow),
                ],
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _listener.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            return;
        }

        using PeriodicTimer timer = new(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await FlushAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFlushFailed(ex);
            }
        }
    }

    private void Add<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct
    {
        string tag = string.Empty;
        foreach (KeyValuePair<string, object?> pair in tags)
        {
            if (pair.Value is not null)
            {
                tag = pair.Value.ToString() ?? string.Empty;
                break;
            }
        }

        double number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        bool gauge = instrument.GetType().Name.StartsWith("ObservableGauge", StringComparison.Ordinal);
        _current.GetOrAdd((instrument.Name, tag.Length > 80 ? tag[..80] : tag), _ => new Aggregate(gauge)).Add(number);
    }

    [LoggerMessage(EventId = 1601, Level = LogLevel.Warning, Message = "Writing this minute's metrics failed")]
    private partial void LogFlushFailed(Exception exception);

    /// <summary>One series' minute so far.</summary>
    private sealed class Aggregate
    {
        private readonly Lock _gate = new();
        private readonly bool _gauge;
        private long _count;
        private double _sum;
        private double _max = double.MinValue;

        public Aggregate(bool gauge)
        {
            _gauge = gauge;
        }

        public void Add(double value)
        {
            lock (_gate)
            {
                if (_gauge)
                {
                    (_count, _sum, _max) = (1, value, value);
                    return;
                }

                _count++;
                _sum += value;
                _max = Math.Max(_max, value);
            }
        }

        public (long Count, double Sum, double Max, bool Gauge) Read()
        {
            lock (_gate)
            {
                return (_count, _sum, _max == double.MinValue ? 0 : _max, _gauge);
            }
        }
    }
}
