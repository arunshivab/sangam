using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Monitoring;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Messaging;
using Sangam.Identity.Infrastructure.Monitoring;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Monitoring;

/// <summary>D-H: metrics into Sangam's own database, the monitoring snapshot, and alerts to the founder through Anjal.</summary>
[Collection("postgres")]
public sealed class MonitoringTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private Guid _owner;
    private Guid _nobody;
    private readonly string _status = Directory.CreateTempSubdirectory("sangam-status-").FullName;

    public MonitoringTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public async Task InitializeAsync()
    {
        if (!_pg.IsAvailable)
        {
            return;
        }

        await _pg.ResetAsync();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sangam"] = _pg.ConnectionString,
            ["Sangam:Email:UseOutbox"] = "true",
            ["Sangam:Maintenance:Enabled"] = "false",
            ["Sangam:DataProtection:PersistKeys"] = "false",
            ["Sangam:Monitoring:HostName"] = "identity",
            ["Sangam:Monitoring:ExpectedHosts"] = "identity,ghost",
            ["Sangam:Monitoring:Alerts:HostSilentMinutes"] = "3",
            ["Sangam:Monitoring:StatusDirectory"] = _status,
        }).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();

        await using SangamDbContext db = _pg.CreateContext();
        SangamUser owner = TestUsers.New("founder@example.in", "+919000000501");
        SangamUser nobody = TestUsers.New("nobody@example.in", "+919000000502");
        db.Users.AddRange(owner, nobody);
        db.PlatformOperators.Add(new PlatformOperator { Id = Guid.NewGuid(), UserId = owner.Id, Role = PlatformRole.Owner, GrantedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        (_owner, _nobody) = (owner.Id, nobody.Id);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        Directory.Delete(_status, recursive: true);
    }

    [PostgresFact]
    public async Task Measurements_AreAddedUpAMinuteAtATime_AndGaugesKeepTheirLatestReading()
    {
        MetricsRecorder recorder = _provider.GetRequiredService<MetricsRecorder>();
        for (int i = 0; i < 4; i++)
        {
            SangamMetrics.RequestCount.Add(1, new KeyValuePair<string, object?>("status", "5xx"));
        }

        SangamMetrics.RequestMilliseconds.Record(120);
        SangamMetrics.RequestMilliseconds.Record(480);
        await recorder.FlushAsync();
        SangamMetrics.RequestCount.Add(1, new KeyValuePair<string, object?>("status", "5xx"));
        await recorder.FlushAsync();

        await using SangamDbContext db = _pg.CreateContext();
        MetricPoint errors = await db.MetricPoints.Where(p => p.Name == SangamMetrics.Requests && p.Tag == "5xx").OrderByDescending(p => p.Minute).FirstAsync();
        Assert.True(errors.Sum >= 5);
        Assert.Equal("identity", errors.Host);
        Assert.True(await db.MetricPoints.AnyAsync(p => p.Name == SangamMetrics.RequestDuration && p.Max >= 480));
        MetricPoint up = await db.MetricPoints.Where(p => p.Name == SangamMetrics.HostUp).OrderByDescending(p => p.Minute).FirstAsync();
        Assert.Equal((1L, 1.0), (up.Count, up.Sum));
        Assert.True(await db.MetricPoints.AnyAsync(p => p.Name == SangamMetrics.DiskFreePercent && p.Tag == HostProbe.DefaultDiskPath));
    }

    [PostgresFact]
    public async Task TheSnapshot_IsForOperatorsOnly_AndShowsTheLastHour()
    {
        MetricsRecorder recorder = _provider.GetRequiredService<MetricsRecorder>();
        SangamMetrics.SignInCount.Add(3);
        SangamMetrics.SignInFailureCount.Add(2);
        MessagingMetrics.EmailsFailed.Add(1);
        await recorder.FlushAsync();
        await File.WriteAllTextAsync(Path.Combine(_status, "backup.json"), "{\"at\":\"2026-10-07T02:15:00Z\",\"ok\":true,\"detail\":\"sangam_identity.dump, 1024 bytes\"}\n");

        using IServiceScope scope = _provider.CreateScope();
        IMonitoringService monitoring = scope.ServiceProvider.GetRequiredService<IMonitoringService>();
        Assert.Null(await monitoring.SnapshotAsync(_nobody));
        MonitoringSnapshot snapshot = (await monitoring.SnapshotAsync(_owner))!;
        Assert.True(snapshot.Database.Up);
        Assert.True(snapshot.LastHour.SignIns >= 3);
        Assert.True(snapshot.LastHour.SignInFailures >= 2);
        Assert.True(snapshot.LastHour.EmailsFailed >= 1);
        Assert.Contains(snapshot.Hosts, h => h.Host == "identity" && h.Up);
        Assert.Contains(snapshot.Hosts, h => h.Host == "ghost" && !h.Up);
        Assert.False(snapshot.BreachList.Enabled);
        Assert.Equal(new JobStatus(new DateTimeOffset(2026, 10, 7, 2, 15, 0, TimeSpan.Zero), true, "sangam_identity.dump, 1024 bytes"), snapshot.Backup);
        Assert.Equal(new JobStatus(null, null, null), snapshot.RestoreDrill);
    }

    [PostgresFact]
    public async Task AnAlert_IsSentOnce_ThenResolved_WhenTheConditionClears()
    {
        AlertEvaluator evaluator = _provider.GetRequiredService<AlertEvaluator>();
        InMemoryEmailOutbox outbox = _provider.GetRequiredService<InMemoryEmailOutbox>();

        IReadOnlyList<AlertCondition> open = await evaluator.RunOnceAsync();
        Assert.Contains(open, c => c.Key == "host_down:ghost");
        Assert.DoesNotContain(open, c => c.Key == "host_down:identity");
        int alerts = outbox.Recent.Count(m => m.Message.ToEmail == "founder@example.in");
        Assert.True(alerts >= 1);
        Assert.Contains(outbox.Recent, m => m.Message.ToEmail == "founder@example.in" && m.Message.Subject == "SangamID alert: ghost host silent");

        await evaluator.RunOnceAsync();
        Assert.Equal(alerts, outbox.Recent.Count(m => m.Message.ToEmail == "founder@example.in"));

        await using (SangamDbContext db = _pg.CreateContext())
        {
            MonitoringAlert row = await db.MonitoringAlerts.SingleAsync(a => a.Key == "host_down:ghost");
            Assert.Null(row.ResolvedAt);
            db.MetricPoints.Add(new MetricPoint { Minute = DateTimeOffset.UtcNow, Host = "ghost", Name = SangamMetrics.HostUp, Count = 1, Sum = 1, Max = 1 });
            await db.SaveChangesAsync();
        }

        Assert.DoesNotContain(await evaluator.RunOnceAsync(), c => c.Key == "host_down:ghost");
        await using SangamDbContext after = _pg.CreateContext();
        Assert.NotNull((await after.MonitoringAlerts.SingleAsync(a => a.Key == "host_down:ghost")).ResolvedAt);
        Assert.True(await after.AuditEvents.AnyAsync(e => e.Action == AuditActions.PlatformAlert));
    }

    [Fact]
    public void TheRules_CoverEveryThreshold()
    {
        DateTimeOffset now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);
        MonitoringSnapshot s = new(
            now,
            [new HostStatus("identity", now, true)],
            new DatabaseStatus(true, 900, 1, 1, "PostgreSQL 18.6"),
            new HourBucket(now, 10, 300, 25, 600, 1000, 80, 2000, 9000, 4, 4),
            [],
            [new GaugeReading("identity", "/", 6, now)],
            [new GaugeReading("identity", "signing", 10, now)],
            [new TlsStatus("id.sangamid.in", now.AddDays(5), null)],
            new JobStatus(now.AddHours(-40), true, null),
            new JobStatus(now.AddDays(-1), false, "pg_restore failed"),
            new Application.Security.BreachListStatus(true, false, null, 0, "The list file is not there yet."),
            new AnjalStatus(true, now.AddMinutes(-1), false, 0, 4, 0, 4),
            []);

        HashSet<string> keys = [.. AlertRules.Evaluate(s, new AlertThresholds(), ["identity"], anjalConfigured: true).Select(c => c.Key)];
        string[] expected =
        [
            "database_slow", "signin_failures", "lockouts", "rate_limited", "email_failures", "sms_failures", "error_rate",
            "response_time", "disk:identity:/", "cert:signing", "tls:id.sangamid.in", "backup", "restore_drill", "anjal_down", "breach_list",
        ];
        foreach (string key in expected)
        {
            Assert.Contains(key, keys);
        }

        MonitoringSnapshot quiet = s with
        {
            Database = new DatabaseStatus(true, 3, 1, 1, null),
            LastHour = new HourBucket(now, 10, 1, 0, 0, 1000, 0, 80, 300, 0, 0),
            Disks = [],
            Certificates = [],
            Tls = [],
            Backup = new JobStatus(now.AddHours(-2), true, null),
            RestoreDrill = new JobStatus(now.AddDays(-3), true, null),
            BreachList = new Application.Security.BreachListStatus(false, false, null, 0, null),
            Anjal = new AnjalStatus(true, now, true, 10, 0, 0, 0),
        };
        Assert.Empty(AlertRules.Evaluate(quiet, new AlertThresholds(), ["identity"], anjalConfigured: true));

        // D-E: a backup that stayed on the server; D-A: an archive that stopped running; D-D: an overdue grievance.
        MonitoringSnapshot gaps = quiet with
        {
            Backup = new JobStatus(now.AddHours(-2), true, "sangam_identity.dump, 1024 bytes; no off-site target configured"),
            AuditArchive = new AuditArchiveStatus(true, now.AddHours(-30), 3, 900, now.AddYears(-3), now.AddDays(-200), 365, 7),
            Grievances = new GrievanceCounts(2, 1, 0),
        };
        Assert.Equal(["backup_offsite", "grievance_overdue", "audit_archive"], AlertRules.Evaluate(gaps, new AlertThresholds(), ["identity"], anjalConfigured: true).Select(c => c.Key));
    }

    [PostgresFact]
    public async Task TheBreachList_IsShownAsTheIdentityServerReportsIt_NotFromTheConsolesOwnSettings()
    {
        // V-10: this test's host has the check off; the identity server reports it on and loaded.
        await _provider.GetRequiredService<MetricsRecorder>().ReportAsync();
        await using (SangamDbContext db = _pg.CreateContext())
        {
            Assert.Contains("\"Enabled\":false", (await db.HostReports.SingleAsync(r => r.Host == "identity" && r.Subject == MetricsRecorder.BreachListSubject)).Payload, StringComparison.Ordinal);
            HostReport identity = await db.HostReports.SingleAsync(r => r.Host == "identity");
            identity.Payload = System.Text.Json.JsonSerializer.Serialize(new Application.Security.BreachListStatus(true, true, new DateOnly(2026, 10, 1), 900_000_000, null));
            db.HostReports.Add(new HostReport
            {
                Host = "admin",
                Subject = MetricsRecorder.BreachListSubject,
                Payload = System.Text.Json.JsonSerializer.Serialize(new Application.Security.BreachListStatus(false, false, null, 0, null)),
                ReportedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using IServiceScope scope = _provider.CreateScope();
        MonitoringSnapshot snapshot = (await scope.ServiceProvider.GetRequiredService<IMonitoringService>().SnapshotAsync(_owner))!;
        Assert.True(snapshot.BreachList.Enabled && snapshot.BreachList.Loaded);
        Assert.Equal(900_000_000, snapshot.BreachList.Entries);
        Assert.Equal("identity", snapshot.BreachListSource!.Host);
        Assert.Equal(2, snapshot.BreachListReports.Count);
        Assert.Contains(snapshot.BreachListReports, r => r.Host == "admin" && !r.Status.Enabled);
        Assert.DoesNotContain(AlertRules.Evaluate(snapshot, new AlertThresholds(), ["identity"], anjalConfigured: false), c => c.Key == "breach_list");
    }

    [Fact]
    public void ADatabaseThatCannotBeReached_IsRecognised_SoTheFounderIsStillAlerted()
    {
        Assert.True(PlatformAlerts.IsDatabaseFailure(new Npgsql.NpgsqlException("Failed to connect to 127.0.0.1:5432")));
        Assert.True(PlatformAlerts.IsDatabaseFailure(new InvalidOperationException("An exception has been raised that is likely due to a transient failure.", new Npgsql.NpgsqlException("refused"))));
        Assert.False(PlatformAlerts.IsDatabaseFailure(new ArgumentException("not a database problem")));
    }
}
