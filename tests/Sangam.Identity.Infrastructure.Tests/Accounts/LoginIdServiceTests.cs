using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Accounts;

/// <summary>
/// rc.11: a login ID is unique, suggested as first.last, held for its owner for 90 days after they give it up, and an
/// email, a verified mobile number or a login ID each find the one account they name.
/// </summary>
[Collection("postgres")]
public sealed class LoginIdServiceTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private IServiceScope _scope = null!;
    private Guid _devi;
    private Guid _ravi;

    public LoginIdServiceTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    private ILoginIdService Service => _scope.ServiceProvider.GetRequiredService<ILoginIdService>();

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
        }).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();

        await using SangamDbContext db = _pg.CreateContext();
        SangamUser devi = TestUsers.New("devi@ids.test", "+919876543210");
        devi.FirstName = "Devi";
        devi.LastName = "Nair";
        SangamUser ravi = TestUsers.New("ravi@ids.test", "+919811122233");
        ravi.FirstName = "Ravi";
        ravi.LastName = "Menon";
        ravi.PhoneNumberConfirmed = true;
        db.Users.AddRange(devi, ravi);
        await db.SaveChangesAsync();
        (_devi, _ravi) = (devi.Id, ravi.Id);
    }

    public async Task DisposeAsync()
    {
        _scope?.Dispose();
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task TheSuggestion_IsFirstDotLast_AndANumberedOneWhenTaken()
    {
        Assert.Equal("devi.nair", (await Service.GetAsync(_devi)).Suggestion);
        await using (SangamDbContext db = _pg.CreateContext())
        {
            await db.Users.Where(u => u.Id == _ravi).ExecuteUpdateAsync(u => u.SetProperty(x => x.LoginId, "devi.nair"));
        }

        Assert.Equal("devi.nair2", (await Service.GetAsync(_devi)).Suggestion);
        LoginIdCheck taken = await Service.CheckAsync(_devi, "Devi.Nair");
        Assert.Equal(LoginIdCheck.ProblemTaken, taken.Problem);
        Assert.Equal("devi.nair2", taken.Alternative);
    }

    [PostgresFact]
    public async Task Setting_IsAudited_AndAGivenUpLoginId_IsHeldForItsOwnerOnly()
    {
        Assert.True((await Service.SetAsync(_devi, "drdevi", "10.0.0.1")).Ok);
        Assert.True((await Service.SetAsync(_devi, "devi.nair", "10.0.0.1")).Ok);

        // "drdevi" was given up: Ravi cannot take it for 90 days; Devi can take it back.
        Assert.Equal(LoginIdCheck.ProblemTaken, (await Service.CheckAsync(_ravi, "drdevi")).Problem);
        Assert.True((await Service.CheckAsync(_devi, "drdevi")).Ok);

        await using SangamDbContext db = _pg.CreateContext();
        Assert.Equal("devi.nair", (await db.Users.SingleAsync(u => u.Id == _devi)).LoginId);
        Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.UserLoginIdSet && e.ActorUserId == _devi));

        // After the hold, anyone may take it.
        await db.LoginIdReleases.ExecuteUpdateAsync(r => r.SetProperty(x => x.ReleasedAt, DateTimeOffset.UtcNow - LoginIds.ReleaseHold - TimeSpan.FromDays(1)));
        Assert.True((await Service.CheckAsync(_ravi, "drdevi")).Ok);
    }

    [PostgresFact]
    public async Task Removing_IsAudited_AndHeldLikeAChange()
    {
        await Service.SetAsync(_devi, "devi.nair", null);
        await Service.RemoveAsync(_devi, null);
        Assert.Null((await Service.GetAsync(_devi)).Current);
        Assert.Equal(LoginIdCheck.ProblemTaken, (await Service.CheckAsync(_ravi, "devi.nair")).Problem);
        await using SangamDbContext db = _pg.CreateContext();
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.UserLoginIdRemove && e.ActorUserId == _devi));
    }

    [PostgresFact]
    public async Task Resolving_FindsTheOneAccount_AnIdentifierNames()
    {
        await Service.SetAsync(_devi, "devi.nair", null);
        Assert.Equal("devi@ids.test", await Service.ResolveEmailAsync("DEVI.NAIR"));
        Assert.Equal("someone@else.test", await Service.ResolveEmailAsync("someone@else.test"));
        // A verified mobile signs in; an unverified one does not.
        Assert.Equal("ravi@ids.test", await Service.ResolveEmailAsync("98111 22233"));
        Assert.Null(await Service.ResolveEmailAsync("+91 98765 43210"));
        Assert.Null(await Service.ResolveEmailAsync("nobody.here"));
        Assert.Null(await Service.ResolveEmailAsync("12"));
    }

    [PostgresFact]
    public async Task ABadLoginId_IsRefused_WithItsReason()
    {
        Assert.Equal(LoginIds.ProblemReserved, (await Service.SetAsync(_devi, "support", null)).Problem);
        Assert.Equal(LoginIds.ProblemCharacters, (await Service.SetAsync(_devi, "devi nair", null)).Problem);
        Assert.Null((await Service.GetAsync(_devi)).Current);
    }
}
