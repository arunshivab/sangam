using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Accounts;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Accounts;

/// <summary>
/// rc.8 (ASVS V2.6.2, V2.6.3): recovery codes are 120-bit random strings, grouped for reading; with the pepper they are
/// stored as keyed hashes, so a copy of the database alone cannot test guesses against them; typing them in capitals or
/// without dashes still works; and codes issued under an older pepper keep working after the pepper is changed.
/// </summary>
[Collection("postgres")]
public sealed partial class RecoveryCodeTests : IAsyncLifetime
{
    private static readonly string OldPepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string NewPepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly PostgresFixture _pg;
    private Guid _userId;

    public RecoveryCodeTests(PostgresFixture pg)
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
        await using ServiceProvider provider = Provider(1, OldPepper, null);
        using IServiceScope scope = provider.CreateScope();
        _userId = (await scope.ServiceProvider.GetRequiredService<IAccountService>().RegisterAsync(new RegisterUserCommand(
            "Asha", "Menon", "asha.codes@example.in", "+919876500077", new DateOnly(1988, 4, 12), Gender.Female, "Kaveri-River-2026!", "v1", null, null))).UserId!.Value;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void ANewCode_Is24CharactersInSixGroups_FromAnAlphabetWithoutLookalikes()
    {
        HashSet<string> seen = [];
        for (int i = 0; i < 200; i++)
        {
            string code = SangamUserManager.NewRecoveryCode();
            Assert.Matches("^[a-km-np-z2-9]{4}(-[a-km-np-z2-9]{4}){5}$", code);
            Assert.True(seen.Add(code));
        }
    }

    [PostgresFact]
    public async Task WithAPepper_CodesAreKeyed_TypedLooselyStillWork_AndSurviveAPepperChange()
    {
        IEnumerable<string>? codes;
        await using (ServiceProvider first = Provider(1, OldPepper, null))
        {
            using IServiceScope scope = first.CreateScope();
            UserManager<SangamUser> users = scope.ServiceProvider.GetRequiredService<UserManager<SangamUser>>();
            Assert.IsType<SangamUserManager>(users);
            SangamUser user = (await users.FindByIdAsync(_userId.ToString("D")))!;
            codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        }

        List<string> issued = [.. codes!];
        Assert.Equal(10, issued.Count);
        Assert.All(Stored(await StoredAsync()), c => Assert.StartsWith(SangamUserStore.KeyedPrefix + "1:", c, StringComparison.Ordinal));

        // The pepper is changed (version 2), the old one kept as a previous pepper: old codes still work, in any typing.
        await using ServiceProvider second = Provider(2, NewPepper, OldPepper);
        using IServiceScope after = second.CreateScope();
        UserManager<SangamUser> manager = after.ServiceProvider.GetRequiredService<UserManager<SangamUser>>();
        SangamUser person = (await manager.FindByIdAsync(_userId.ToString("D")))!;
        Assert.True((await manager.RedeemTwoFactorRecoveryCodeAsync(person, issued[0].ToUpperInvariant().Replace("-", string.Empty, StringComparison.Ordinal))).Succeeded);
        Assert.False((await manager.RedeemTwoFactorRecoveryCodeAsync(person, issued[0])).Succeeded);
        Assert.True((await manager.RedeemTwoFactorRecoveryCodeAsync(person, issued[1].Replace("-", " ", StringComparison.Ordinal))).Succeeded);
        Assert.False((await manager.RedeemTwoFactorRecoveryCodeAsync(person, "abcd-efgh-ijkm-npqr-stuv-wxyz")).Succeeded);
        Assert.Equal(8, Stored(await StoredAsync()).Length);

        // New codes are keyed with the new pepper.
        await manager.GenerateNewTwoFactorRecoveryCodesAsync(person, 10);
        Assert.All(Stored(await StoredAsync()), c => Assert.StartsWith(SangamUserStore.KeyedPrefix + "2:", c, StringComparison.Ordinal));
    }

    private static string[] Stored(string value) => value.Split(';', StringSplitOptions.RemoveEmptyEntries);

    private async Task<string> StoredAsync()
    {
        await using SangamDbContext db = _pg.CreateContext();
        return await db.Database.SqlQueryRaw<string>("SELECT value AS \"Value\" FROM user_tokens WHERE user_id = {0} AND name = 'RecoveryCodes'", _userId).SingleAsync();
    }

    private ServiceProvider Provider(int version, string pepper, string? previous)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();
        Dictionary<string, string?> settings = new()
        {
            ["ConnectionStrings:Sangam"] = _pg.ConnectionString,
            ["Sangam:Email:UseOutbox"] = "true",
            ["Sangam:PasswordHashing:MemoryKiB"] = "8192",
            ["Sangam:PasswordHashing:Iterations"] = "2",
            ["Sangam:PasswordHashing:Pepper"] = pepper,
            ["Sangam:PasswordHashing:PepperVersion"] = version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Sangam:Maintenance:Enabled"] = "false",
        };
        if (previous is not null)
        {
            settings["Sangam:PasswordHashing:PreviousPeppers:1"] = previous;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        return services.BuildServiceProvider();
    }
}
