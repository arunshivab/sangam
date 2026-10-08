using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Partners;

/// <summary>PR-13: invitations by e-mail — accepted only by the invited address, once, in time.</summary>
[Collection("postgres")]
public sealed partial class InvitationServiceTests : IAsyncLifetime
{
    private const string Password = "Kaveri-River-2026!";
    private static readonly RegisterUserCommand Admin = new("Ravi", "Menon", "ravi@example.in", "+919876500077", new DateOnly(1986, 6, 12), Gender.Male, Password, "v1", "203.0.113.7", "xunit");
    private static readonly RegisterUserCommand Invitee = new("Asha", "Pillai", "asha@example.in", "+919876500088", new DateOnly(1990, 2, 1), Gender.Female, Password, "v1", "203.0.113.8", "xunit");
    private static readonly RegisterUserCommand Stranger = new("Kiran", "Rao", "kiran@example.in", "+919876500099", new DateOnly(1992, 3, 4), Gender.Male, Password, "v1", "203.0.113.9", "xunit");
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private Guid _adminId;
    private Guid _inviteeId;
    private Guid _strangerId;
    private Guid _appId;
    private Guid _orgId;

    public InvitationServiceTests(PostgresFixture pg)
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
            ["Sangam:Issuer"] = "https://id.example.test",
            ["Sangam:PasswordHashing:MemoryKiB"] = "8192",
            ["Sangam:PasswordHashing:Iterations"] = "2",
            ["Sangam:Otp:ResendCooldown"] = "00:00:00",
            ["Sangam:Maintenance:Enabled"] = "false",
            ["Sangam:DataProtection:PersistKeys"] = "false",
        }).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();
        _adminId = await RegisterAsync(Admin);
        _inviteeId = await RegisterAsync(Invitee);
        _strangerId = await RegisterAsync(Stranger);

        await using SangamDbContext db = _pg.CreateContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        App app = new() { Id = Guid.NewGuid(), ClientId = "lipi-his", Slug = "lipi-his", DisplayName = "LiPi HIS", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        db.Apps.Add(app);
        db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = app.Id, UserId = _adminId, Role = AppAdminRole.Owner, GrantedAt = now });
        await db.SaveChangesAsync();
        _appId = app.Id;
        _orgId = Guid.NewGuid();

        using IServiceScope scope = _provider.CreateScope();
        IManagementService management = scope.ServiceProvider.GetRequiredService<IManagementService>();
        await management.UpsertRoleAsync(_appId, "nurse", new RoleUpsert("Nurse", null, ["patient:read"], null), ManagementActor.Api);
        await management.UpsertOrganisationAsync(_appId, _orgId, new OrganisationUpsert("Apulki", "hospital", null, null), ManagementActor.Api);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task TheInvitedPerson_AcceptsOnce_AndGetsTheRole_WhileOthersCannot()
    {
        using IServiceScope scope = _provider.CreateScope();
        IInvitationService invitations = scope.ServiceProvider.GetRequiredService<IInvitationService>();
        InMemoryEmailOutbox outbox = scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>();

        PartnerResult sent = await invitations.CreateAsync(_adminId, _appId, _orgId, "Asha@Example.in", "nurse", appliesToDescendants: false);
        Assert.True(sent.Succeeded, sent.Message);
        string body = outbox.LatestFor("Asha@Example.in")!.Message.TextBody;
        Assert.Contains("https://id.example.test/invite/", body, StringComparison.Ordinal);
        string token = TokenIn(body);

        InvitationView? view = await invitations.GetAsync(token);
        Assert.Equal(("LiPi HIS", "Apulki", "Nurse", InvitationState.Open), (view!.AppName, view.OrgName, view.RoleName, view.State));

        PartnerResult wrongPerson = await invitations.AcceptAsync(token, _strangerId);
        Assert.False(wrongPerson.Succeeded);
        Assert.Equal(InvitationState.Open, (await invitations.GetAsync(token))!.State);

        PartnerResult accepted = await invitations.AcceptAsync(token, _inviteeId);
        Assert.True(accepted.Succeeded, accepted.Message);
        Assert.False((await invitations.AcceptAsync(token, _inviteeId)).Succeeded);

        await using SangamDbContext db = _pg.CreateContext();
        Assert.True(await db.OrgMemberships.AnyAsync(m => m.UserId == _inviteeId && m.OrgId == _orgId && m.RevokedAt == null));
        Assert.True(await db.AppGrants.AnyAsync(g => g.UserId == _inviteeId && g.AppId == _appId && g.RevokedAt == null));
        Assert.False(await db.AppGrants.AnyAsync(g => g.UserId == _strangerId && g.AppId == _appId));
    }

    [PostgresFact]
    public async Task Invitations_AreRefused_FromNonAdministrators_ForRetiredRoles_AndOnceExpired()
    {
        using IServiceScope scope = _provider.CreateScope();
        IInvitationService invitations = scope.ServiceProvider.GetRequiredService<IInvitationService>();
        InMemoryEmailOutbox outbox = scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>();

        Assert.False((await invitations.CreateAsync(_strangerId, _appId, _orgId, "asha@example.in", "nurse", false)).Succeeded);
        Assert.False((await invitations.CreateAsync(_adminId, _appId, _orgId, "asha@example.in", "surgeon", false)).Succeeded);
        Assert.False((await invitations.CreateAsync(_adminId, _appId, _orgId, "not an address", "nurse", false)).Succeeded);
        Assert.Null(await invitations.GetAsync("no-such-token"));

        await invitations.CreateAsync(_adminId, _appId, _orgId, "asha@example.in", "nurse", false);
        string token = TokenIn(outbox.LatestFor("asha@example.in")!.Message.TextBody);
        await using (SangamDbContext db = _pg.CreateContext())
        {
            await db.Invitations.ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        // A later request, in its own scope, sees the expiry (the first scope still tracks the old row).
        using IServiceScope later = _provider.CreateScope();
        IInvitationService fresh = later.ServiceProvider.GetRequiredService<IInvitationService>();
        Assert.Equal(InvitationState.Expired, (await fresh.GetAsync(token))!.State);
        Assert.False((await fresh.AcceptAsync(token, _inviteeId)).Succeeded);
    }

    private static string TokenIn(string body) => InviteLink().Match(body).Groups[1].Value;

    [GeneratedRegex("/invite/([A-Za-z0-9_-]+)")]
    private static partial Regex InviteLink();

    [GeneratedRegex("[0-9]{6}")]
    private static partial Regex SixDigits();

    private async Task<Guid> RegisterAsync(RegisterUserCommand command)
    {
        using IServiceScope scope = _provider.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        InMemoryEmailOutbox outbox = scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>();
        Guid id = (await accounts.RegisterAsync(command)).UserId!.Value;
        await accounts.VerifyCodeAsync(id, OneTimeCodePurpose.EmailVerification, SixDigits().Match(outbox.LatestFor(command.Email)!.Message.TextBody).Value, null);
        return id;
    }
}
