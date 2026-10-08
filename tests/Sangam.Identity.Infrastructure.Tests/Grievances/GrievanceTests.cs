using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Application.Grievances;
using Sangam.Identity.Application.Monitoring;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Grievances;
using Sangam.Identity.Infrastructure.Monitoring;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Grievances;

/// <summary>
/// D-D: the grievance log — every grievance logged with its deadlines (two working days to acknowledge, thirty days
/// to resolve), acknowledged and answered by e-mail, every step kept and audited, overdue ones alerted.
/// </summary>
[Collection("postgres")]
public sealed class GrievanceTests : IAsyncLifetime
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private readonly List<IServiceScope> _scopes = [];
    private Guid _support;
    private Guid _viewer;
    private Guid _person;

    public GrievanceTests(PostgresFixture pg)
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
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sangam"] = _pg.ConnectionString,
            ["Sangam:Email:UseOutbox"] = "true",
            ["Sangam:Maintenance:Enabled"] = "false",
            ["Sangam:DataProtection:PersistKeys"] = "false",
        }).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();

        await using SangamDbContext db = _pg.CreateContext();
        SangamUser support = TestUsers.New("support@example.in", "+919000000601");
        SangamUser viewer = TestUsers.New("viewer@example.in", "+919000000602");
        SangamUser person = TestUsers.New("meera@example.in", "+919000000603");
        person.Locale = "hi-IN";
        db.Users.AddRange(support, viewer, person);
        db.PlatformOperators.AddRange(
            new PlatformOperator { Id = Guid.NewGuid(), UserId = support.Id, Role = PlatformRole.Support, GrantedAt = DateTimeOffset.UtcNow },
            new PlatformOperator { Id = Guid.NewGuid(), UserId = viewer.Id, Role = PlatformRole.Viewer, GrantedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        (_support, _viewer, _person) = (support.Id, viewer.Id, person.Id);
    }

    public async Task DisposeAsync()
    {
        foreach (IServiceScope scope in _scopes)
        {
            scope.Dispose();
        }

        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    [Fact]
    public void TheDeadlines_CountWorkingDaysInIndia_AndThirtyDays()
    {
        GrievanceClock plain = new(new ConfigurationBuilder().Build());
        DateTimeOffset friday = new(2026, 10, 9, 15, 0, 0, Ist);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 23, 59, 59, Ist), plain.AcknowledgeBy(friday));
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 23, 59, 59, Ist), plain.ResolveBy(friday));

        // Late on a Thursday evening in India is already Friday in India, though still Thursday in UTC.
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 23, 59, 59, Ist), plain.AcknowledgeBy(new DateTimeOffset(2026, 10, 8, 20, 0, 0, TimeSpan.Zero)));

        GrievanceClock withHoliday = new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Sangam:Grievance:Holidays"] = "2026-10-12, 2026-10-20" }).Build());
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 23, 59, 59, Ist), withHoliday.AcknowledgeBy(friday));
    }

    [PostgresFact]
    public async Task AGrievance_IsLogged_WithItsReferenceAndDeadlines_AndLinkedToTheAccount()
    {
        DateTimeOffset received = DateTimeOffset.UtcNow.AddHours(-1);
        (AdminResult result, Guid? id) = await Service().LogAsync(_support, Input(received, "MEERA@example.in"), "198.51.100.7");
        Assert.True(result.Succeeded, result.Message);
        Assert.StartsWith("Grievance GRV-", result.Message, StringComparison.Ordinal);

        GrievanceDetail detail = (await Service().GetAsync(_viewer, id!.Value))!;
        Assert.Matches(@"^GRV-\d{4}-0001$", detail.Row.Reference);
        Assert.Equal(GrievanceStatus.Received, detail.Row.Status);
        Assert.Equal("meera@example.in", detail.AccountEmail);
        Assert.False(detail.CanAct);
        Assert.InRange((detail.Row.ResolveBy - received).TotalDays, 30, 31);
        Assert.Equal("logged", Assert.Single(detail.History).Kind);

        (_, Guid? second) = await Service().LogAsync(_support, Input(received, "+919812345678"), null);
        Assert.EndsWith("-0002", (await Service().GetAsync(_support, second!.Value))!.Row.Reference, StringComparison.Ordinal);

        await using SangamDbContext db = _pg.CreateContext();
        AuditEvent audit = await db.AuditEvents.Where(e => e.Action == AuditActions.GrievanceLog).OrderBy(e => e.Id).FirstAsync();
        Assert.Equal(("grievance", id), (audit.TargetType, audit.TargetId));
    }

    [PostgresFact]
    public async Task OnlySupportAndOwner_MayAct_AViewerMayRead_OthersSeeNothing()
    {
        Assert.False((await Service().LogAsync(_viewer, Input(DateTimeOffset.UtcNow, "x@example.in"), null)).Result.Succeeded);
        Assert.Null(await Service().ListAsync(_person, openOnly: false));
        Assert.Empty((await Service().ListAsync(_viewer, openOnly: false))!);
        Assert.False((await Service().LogAsync(_support, Input(DateTimeOffset.UtcNow.AddDays(-90), "x@example.in"), null)).Result.Succeeded);
        Assert.False((await Service().LogAsync(_support, Input(DateTimeOffset.UtcNow, "x@example.in") with { Summary = "short" }, null)).Result.Succeeded);
    }

    [PostgresFact]
    public async Task Acknowledging_AndAnswering_EmailThePerson_InTheirLanguage_AndKeepEveryStep()
    {
        (_, Guid? id) = await Service().LogAsync(_support, Input(DateTimeOffset.UtcNow, "meera@example.in"), null);
        Assert.True((await Service().AcknowledgeAsync(_support, id!.Value, sendEmail: true, null)).Succeeded);
        Assert.False((await Service().AcknowledgeAsync(_support, id.Value, sendEmail: true, null)).Succeeded);
        InMemoryEmailOutbox outbox = _provider.GetRequiredService<InMemoryEmailOutbox>();
        Application.Abstractions.EmailMessage ack = outbox.LatestFor("meera@example.in")!.Message;
        Assert.Contains("GRV-", ack.Subject, StringComparison.Ordinal);
        Assert.Contains("शिकायत", ack.Subject, StringComparison.Ordinal);

        Assert.True((await Service().AddNoteAsync(_support, id.Value, "Called her back; she wants her old mobile number removed.", null)).Succeeded);
        Assert.False((await Service().CloseAsync(_support, id.Value, GrievanceStatus.Resolved, "Done.", sendEmail: true, null)).Succeeded);
        Assert.True((await Service().CloseAsync(_support, id.Value, GrievanceStatus.Resolved, "We removed the old mobile number from your account on 7 October.", sendEmail: true, null)).Succeeded);
        Assert.Contains("We removed the old mobile number", outbox.LatestFor("meera@example.in")!.Message.TextBody, StringComparison.Ordinal);

        GrievanceDetail detail = (await Service().GetAsync(_support, id.Value))!;
        Assert.Equal(GrievanceStatus.Resolved, detail.Row.Status);
        Assert.Equal(["logged", "acknowledged", "note", "resolved"], detail.History.Select(h => h.Kind));
        Assert.False((await Service().AcknowledgeAsync(_support, id.Value, false, null)).Succeeded);
        Assert.True((await Service().AddNoteAsync(_support, id.Value, "She wrote to thank us.", null)).Succeeded);

        await using SangamDbContext db = _pg.CreateContext();
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.GrievanceAcknowledge));
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.GrievanceClose));
        Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.GrievanceNote));

        // The history cannot be rewritten, and a grievance cannot be deleted.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("UPDATE grievance_entries SET text = 'nothing happened'"));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM grievances"));
    }

    [PostgresFact]
    public async Task AnOverdueGrievance_IsCounted_AndAlerted()
    {
        await Service().LogAsync(_support, Input(DateTimeOffset.UtcNow.AddDays(-40), "late@example.in"), null);
        await using (SangamDbContext db = _pg.CreateContext())
        {
            // Logged within 60 days, but its deadlines put back as if received long ago.
            Grievance g = await db.Grievances.SingleAsync();
            g.AcknowledgeBy = DateTimeOffset.UtcNow.AddDays(-35);
            g.ResolveBy = DateTimeOffset.UtcNow.AddDays(-5);
            await db.SaveChangesAsync();
        }

        using IServiceScope scope = _provider.CreateScope();
        MonitoringSnapshot snapshot = await scope.ServiceProvider.GetRequiredService<EfMonitoringService>().BuildAsync();
        Assert.Equal(new GrievanceCounts(1, 1, 1), snapshot.Grievances);
        Assert.Contains(AlertRules.Evaluate(snapshot, new AlertThresholds(), [], anjalConfigured: false), c => c.Key == "grievance_overdue");
        Assert.True((await Service().ListAsync(_support, openOnly: true))!.Single().ResolveOverdue);
    }

    private static GrievanceInput Input(DateTimeOffset received, string contact)
        => new(received, "email", "correction", "Meera Nair", contact, "My old mobile number is still on my account and I want it removed.");

    private IGrievanceService Service()
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<IGrievanceService>();
    }
}
