using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Admin;

[Collection("postgres")]
public sealed class MfaAndBootstrapTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private Guid _userId;

    public MfaAndBootstrapTests(PostgresFixture pg)
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
            ["Sangam:PasswordHashing:MemoryKiB"] = "8192",
            ["Sangam:PasswordHashing:Iterations"] = "2",
            ["Sangam:Otp:ResendCooldown"] = "00:00:00",
            ["Sangam:Maintenance:Enabled"] = "false",
        }).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();

        using IServiceScope scope = _provider.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        InMemoryEmailOutbox outbox = scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>();
        _userId = (await accounts.RegisterAsync(new RegisterUserCommand(
            "Arun", "Shiva", "arun@example.in", "+919876500010", new DateOnly(1980, 1, 1), Gender.Male, "Kaveri-River-2026!", "v1", null, null))).UserId!.Value;
        string code = System.Text.RegularExpressions.Regex.Match(outbox.LatestFor("arun@example.in")!.Message.TextBody, "[0-9]{6}").Value;
        await accounts.VerifyCodeAsync(_userId, OneTimeCodePurpose.EmailVerification, code, null);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task Enrolment_GivesAKeyAQrCodeAndAnOtpauthUri_AndIsNotOnUntilConfirmed()
    {
        using IServiceScope scope = _provider.CreateScope();
        IMfaService mfa = scope.ServiceProvider.GetRequiredService<IMfaService>();

        MfaEnrolment enrolment = await mfa.BeginEnrolmentAsync(_userId);

        Assert.StartsWith("otpauth://totp/Sangam:", enrolment.AuthenticatorUri, StringComparison.Ordinal);
        Assert.Contains("issuer=Sangam", enrolment.AuthenticatorUri, StringComparison.Ordinal);
        Assert.StartsWith("<svg", enrolment.QrCodeSvg.TrimStart(), StringComparison.Ordinal);
        Assert.DoesNotContain("http", enrolment.QrCodeSvg.Replace("http://www.w3.org", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        // Lowercase, grouped in fours, and only the base32 alphabet — never 0, 1, 8 or 9.
        Assert.Matches("^([a-z2-7]{4} ?)+$", enrolment.SharedKey);
        Assert.DoesNotContain('0', enrolment.SharedKey);
        Assert.DoesNotContain('1', enrolment.SharedKey);
        Assert.False(await mfa.IsEnrolledAsync(_userId));
    }

    [PostgresFact]
    public async Task ConfirmingWithTheRightCode_TurnsItOn_AndIssuesTenRecoveryCodes()
    {
        using IServiceScope scope = _provider.CreateScope();
        IMfaService mfa = scope.ServiceProvider.GetRequiredService<IMfaService>();
        await mfa.BeginEnrolmentAsync(_userId);

        Assert.False((await mfa.ConfirmEnrolmentAsync(_userId, "000000", null)).Succeeded);

        MfaConfirmation ok = await mfa.ConfirmEnrolmentAsync(_userId, await CurrentCodeAsync(), null);
        Assert.True(ok.Succeeded);
        Assert.Equal(10, ok.RecoveryCodes.Count);
        Assert.True(await mfa.IsEnrolledAsync(_userId));

        // R7 (ASVS V2.5.5): the person is told an authenticator app now protects their account.
        InMemoryEmailOutbox outbox = scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>();
        Assert.Contains("authenticator app was added", outbox.LatestFor("arun@example.in")!.Message.Subject, StringComparison.Ordinal);

        using IServiceScope check = _provider.CreateScope();
        Assert.True((await check.ServiceProvider.GetRequiredService<IAccountService>().FindByIdAsync(_userId))!.MfaEnrolled);
    }

    [PostgresFact]
    public async Task Verify_AcceptsTheCurrentCodeAndARecoveryCodeOnce_AndCountsWrongOnesTowardsLockout()
    {
        using IServiceScope scope = _provider.CreateScope();
        IMfaService mfa = scope.ServiceProvider.GetRequiredService<IMfaService>();
        await mfa.BeginEnrolmentAsync(_userId);
        string first = await CurrentCodeAsync();
        MfaConfirmation confirmation = await mfa.ConfirmEnrolmentAsync(_userId, first, null);

        // R7 (ASVS V2.8.4): a code works once — the one that confirmed the app is refused; the next one is accepted.
        Assert.NotEqual(MfaResult.Valid, await mfa.VerifyAsync(_userId, first, null));
        Assert.Equal(MfaResult.Valid, await mfa.VerifyAsync(_userId, await CurrentCodeAsync(TimeSpan.FromSeconds(30)), null));

        // Recovery codes keep their dash; a person reading one off paper may add spaces.
        string recovery = confirmation.RecoveryCodes[0];
        Assert.Contains('-', recovery);
        Assert.Equal(MfaResult.Valid, await mfa.VerifyAsync(_userId, " " + recovery + " ", null));
        Assert.NotEqual(MfaResult.Valid, await mfa.VerifyAsync(_userId, recovery, null));
        Assert.Equal(MfaResult.Valid, await mfa.VerifyAsync(_userId, confirmation.RecoveryCodes[1], null));

        MfaResult last = MfaResult.Invalid;
        for (int i = 0; i < 6; i++)
        {
            last = await mfa.VerifyAsync(_userId, "000000", null);
        }

        Assert.Equal(MfaResult.LockedOut, last);
        await using SangamDbContext db = _pg.CreateContext();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.UserMfaFail));
    }

    [PostgresFact]
    public async Task AnOperator_CannotRemoveTheirAuthenticator_ButAnOrdinaryUserCan()
    {
        using IServiceScope scope = _provider.CreateScope();
        IMfaService mfa = scope.ServiceProvider.GetRequiredService<IMfaService>();
        await mfa.BeginEnrolmentAsync(_userId);
        await mfa.ConfirmEnrolmentAsync(_userId, await CurrentCodeAsync(), null);

        await using (SangamDbContext db = _pg.CreateContext())
        {
            db.PlatformOperators.Add(new PlatformOperator { Id = Guid.NewGuid(), UserId = _userId, Role = PlatformRole.Viewer, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.False(await mfa.DisableAsync(_userId, null));
        Assert.True(await mfa.IsEnrolledAsync(_userId));

        await using (SangamDbContext db = _pg.CreateContext())
        {
            await db.PlatformOperators.Where(o => o.UserId == _userId).ExecuteUpdateAsync(u => u.SetProperty(o => o.RevokedAt, DateTimeOffset.UtcNow));
        }

        Assert.True(await mfa.DisableAsync(_userId, null));
        Assert.False(await mfa.IsEnrolledAsync(_userId));
        Assert.Contains("authenticator app was removed", scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>().LatestFor("arun@example.in")!.Message.Subject, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Bootstrap_MakesTheFirstOwner_ThenRefusesForever()
    {
        TextWriter stdout = Console.Out;
        TextWriter stderr = Console.Error;
        using StringWriter captured = new();
        Console.SetOut(captured);
        Console.SetError(captured);
        try
        {
            Assert.Equal(2, await OperatorBootstrapper.RunAsync(_provider, ["create-operator"]));
            Assert.Equal(1, await OperatorBootstrapper.RunAsync(_provider, ["create-operator", "nobody@example.in"]));
            Assert.Equal(0, await OperatorBootstrapper.RunAsync(_provider, ["create-operator", "arun@example.in"]));
            Assert.Equal(1, await OperatorBootstrapper.RunAsync(_provider, ["create-operator", "arun@example.in"]));
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }

        Assert.Contains("already has at least one operator", captured.ToString(), StringComparison.Ordinal);
        await using SangamDbContext db = _pg.CreateContext();
        PlatformOperator owner = await db.PlatformOperators.SingleAsync();
        Assert.Equal(PlatformRole.Owner, owner.Role);
        Assert.Null(owner.GrantedByUserId);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.AdminOperatorGrant && e.ActorType == AuditActorType.System));
    }

    [PostgresFact]
    public async Task RanksAreStoredAsTheirNewNames()
    {
        await using SangamDbContext db = _pg.CreateContext();
        foreach (PlatformRole role in PlatformRanks.All)
        {
            db.PlatformOperators.Add(new PlatformOperator { Id = Guid.NewGuid(), UserId = _userId, Role = role, GrantedAt = DateTimeOffset.UtcNow, RevokedAt = DateTimeOffset.UtcNow });
        }

        await db.SaveChangesAsync();
        List<string> stored = await db.Database.SqlQueryRaw<string>("SELECT role AS \"Value\" FROM platform_operators ORDER BY role").ToListAsync();
        Assert.Equal(["app_manager", "owner", "support", "viewer"], stored);
    }

    [PostgresFact]
    public async Task TheAuthenticatorSecret_IsStoredEncrypted_AndRecoveryCodesHashed_AndAPlainSecretStillWorks()
    {
        // R7 (ASVS V2.8.2, V2.6.2).
        using (IServiceScope scope = _provider.CreateScope())
        {
            IMfaService mfa = scope.ServiceProvider.GetRequiredService<IMfaService>();
            MfaEnrolment enrolment = await mfa.BeginEnrolmentAsync(_userId);
            MfaConfirmation confirmation = await mfa.ConfirmEnrolmentAsync(_userId, await CurrentCodeAsync(), null);

            await using SangamDbContext db = _pg.CreateContext();
            string stored = await db.Database.SqlQueryRaw<string>("SELECT value AS \"Value\" FROM user_tokens WHERE user_id = {0} AND name = 'AuthenticatorKey'", _userId).SingleAsync();
            Assert.StartsWith(Sangam.Identity.Infrastructure.Accounts.SangamUserStore.ProtectedPrefix, stored, StringComparison.Ordinal);
            Assert.DoesNotContain(enrolment.SharedKey.Replace(" ", string.Empty, StringComparison.Ordinal), stored, StringComparison.OrdinalIgnoreCase);
            string codes = await db.Database.SqlQueryRaw<string>("SELECT value AS \"Value\" FROM user_tokens WHERE user_id = {0} AND name = 'RecoveryCodes'", _userId).SingleAsync();
            Assert.All(codes.Split(';'), c => Assert.StartsWith(Sangam.Identity.Infrastructure.Accounts.SangamUserStore.HashedPrefix, c, StringComparison.Ordinal));
            Assert.DoesNotContain(confirmation.RecoveryCodes[0], codes, StringComparison.Ordinal);
            Assert.Equal(MfaResult.Valid, await mfa.VerifyAsync(_userId, confirmation.RecoveryCodes[0], null));
        }

        // A secret written before R7, in the clear, is still read.
        await using (SangamDbContext db = _pg.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE user_tokens SET value = 'JBSWY3DPEHPK3PXP' WHERE user_id = {0} AND name = 'AuthenticatorKey'", _userId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM user_tokens WHERE user_id = {0} AND name = 'LastTotpStep'", _userId);
        }

        using IServiceScope after = _provider.CreateScope();
        string code = Totp(Base32("JBSWY3DPEHPK3PXP"), DateTimeOffset.UtcNow);
        Assert.Equal(MfaResult.Valid, await after.ServiceProvider.GetRequiredService<IMfaService>().VerifyAsync(_userId, code, null));
    }

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1234567890L, "005924")]
    [InlineData(20000000000L, "353130")]
    public void TheCode_MatchesTheRfc6238TestVectors(long unixSeconds, string expected)
        => Assert.Equal(expected, Sangam.Identity.Infrastructure.Accounts.SangamAuthenticatorTokenProvider.Code(System.Text.Encoding.ASCII.GetBytes("12345678901234567890"), unixSeconds / 30));

    /// <summary>Computes the current TOTP from the user's stored key, the way an authenticator app would.</summary>
    private async Task<string> CurrentCodeAsync(TimeSpan ahead = default)
    {
        using IServiceScope scope = _provider.CreateScope();
        UserManager<SangamUser> users = scope.ServiceProvider.GetRequiredService<UserManager<SangamUser>>();
        SangamUser user = (await users.FindByIdAsync(_userId.ToString("D")))!;
        string key = (await users.GetAuthenticatorKeyAsync(user))!;
        return Totp(Base32(key), DateTimeOffset.UtcNow + ahead);
    }

    private static string Totp(byte[] secret, DateTimeOffset at)
    {
        long step = at.ToUnixTimeSeconds() / 30;
        byte[] counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

#pragma warning disable CA5350 // RFC 6238 and every authenticator app use HMAC-SHA1; the test must compute what a phone computes.
        byte[] hash = HMACSHA1.HashData(secret, counter);
#pragma warning restore CA5350
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Base32(string input)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        string clean = input.Trim().TrimEnd('=').ToUpperInvariant();
        List<byte> bytes = [];
        int buffer = 0;
        int bits = 0;
        foreach (char c in clean)
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
