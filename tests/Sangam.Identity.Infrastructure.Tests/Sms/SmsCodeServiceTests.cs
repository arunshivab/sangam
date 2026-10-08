using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Sms;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Sms;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Sms;

/// <summary>PR-15: SMS codes through the development outbox — verification, sign-in, every limit and the records kept.</summary>
[Collection("postgres")]
public sealed partial class SmsCodeServiceTests : IAsyncLifetime
{
    private const string Mobile = "+919000000051";
    private const string Ip = "203.0.113.7";
    private readonly PostgresFixture _pg;
    private readonly List<IServiceScope> _scopes = [];
    private readonly List<ServiceProvider> _providers = [];
    private readonly MutableClock _clock = new(DateTimeOffset.UtcNow);
    private Guid _userId;

    public SmsCodeServiceTests(PostgresFixture pg)
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
        _userId = await AddUserAsync("sms-owner@example.in", Mobile);
    }

    public async Task DisposeAsync()
    {
        foreach (IServiceScope scope in _scopes)
        {
            scope.Dispose();
        }

        foreach (ServiceProvider provider in _providers)
        {
            await provider.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task AVerificationCode_IsTexted_AndProvesTheMobile_WithoutKeepingTheNumberOrTheCode()
    {
        (ISmsCodeService sms, InMemorySmsOutbox outbox, IServiceProvider sp) = Build();

        SmsIssueResult sent = await sms.SendMobileVerificationAsync(_userId, Ip);
        Assert.Equal(SmsIssueStatus.Sent, sent.Status);

        SentSms message = outbox.LatestFor(Mobile)!;
        Assert.Equal(SmsSettings.MobileVerificationTemplate, message.Message.TemplateKey);
        Assert.Equal("SANGAM", message.Message.SenderHeader);
        string code = CodeRegex().Match(message.Message.Text).Value;
        Assert.Equal(code + " is your code to verify this mobile for SangamID. - SANGAM", message.Message.Text);

        await using (SangamDbContext db = _pg.CreateContext())
        {
            SmsMessage row = await db.SmsMessages.SingleAsync();
            Assert.Equal(SmsStatus.Sent, row.Status);
            Assert.Equal(message.ProviderMessageId, row.ProviderMessageId);
            Assert.DoesNotContain("9000000051", row.ToHash, StringComparison.Ordinal);
            Assert.NotNull(row.IpHash);
            Assert.DoesNotContain(code, row.ToHash + row.IpHash + row.Template, StringComparison.Ordinal);
        }

        Assert.Equal(OtpVerifyStatus.Valid, await sms.VerifyMobileAsync(_userId, code, Ip));

        await using SangamDbContext check = _pg.CreateContext();
        Assert.True((await check.Users.SingleAsync(u => u.Id == _userId)).PhoneNumberConfirmed);
        Assert.Equal(1, await check.AuditEvents.CountAsync(e => e.Action == AuditActions.UserMobileVerify && e.TargetId == _userId));
        Assert.Contains(await check.AuditEvents.Where(e => e.Action == AuditActions.UserSmsSend).Select(e => e.Metadata).ToListAsync(),
            m => m.Contains("\"outcome\": \"sent\"", StringComparison.Ordinal) && !m.Contains("9000000051", StringComparison.Ordinal));
        _ = sp;
    }

    [PostgresFact]
    public async Task SignInCodes_GoOnlyToAVerifiedMobile()
    {
        (ISmsCodeService sms, InMemorySmsOutbox outbox, _) = Build();

        Assert.Equal(SmsIssueStatus.MobileNotVerified, (await sms.SendSignInCodeAsync(_userId, Ip)).Status);
        Assert.Null(outbox.LatestFor(Mobile));

        await MarkVerifiedAsync(_userId);
        Assert.Equal(SmsIssueStatus.Sent, (await sms.SendSignInCodeAsync(_userId, Ip)).Status);
        SentSms message = outbox.LatestFor(Mobile)!;
        Assert.Equal(SmsSettings.SignInTemplate, message.Message.TemplateKey);
        Assert.EndsWith("is your SangamID sign-in code. It expires in 10 minutes. Do not share it. - SANGAM", message.Message.Text, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ACodeSentToTheOldNumber_DoesNotVerifyANewOne()
    {
        (ISmsCodeService sms, InMemorySmsOutbox outbox, _) = Build();
        await sms.SendMobileVerificationAsync(_userId, Ip);
        string code = CodeRegex().Match(outbox.LatestFor(Mobile)!.Message.Text).Value;

        await using (SangamDbContext db = _pg.CreateContext())
        {
            SangamUser user = await db.Users.SingleAsync(u => u.Id == _userId);
            user.PhoneNumber = "+919000000052";
            await db.SaveChangesAsync();
        }

        Assert.Equal(OtpVerifyStatus.Expired, await sms.VerifyMobileAsync(_userId, code, Ip));
        await using SangamDbContext check = _pg.CreateContext();
        Assert.False((await check.Users.SingleAsync(u => u.Id == _userId)).PhoneNumberConfirmed);
    }

    [PostgresFact]
    public async Task TheAccountRules_StillApply_CooldownAndWrongCodes()
    {
        (ISmsCodeService sms, InMemorySmsOutbox outbox, _) = Build();
        Assert.Equal(SmsIssueStatus.Sent, (await sms.SendMobileVerificationAsync(_userId, Ip)).Status);
        SmsIssueResult again = await sms.SendMobileVerificationAsync(_userId, Ip);
        Assert.Equal(SmsIssueStatus.TooSoon, again.Status);
        Assert.NotNull(await sms.NextSendAllowedAtAsync(_userId, OneTimeCodePurpose.MobileVerification));

        string code = CodeRegex().Match(outbox.LatestFor(Mobile)!.Message.Text).Value;
        string wrong = code == "000000" ? "111111" : "000000";
        Assert.Equal(OtpVerifyStatus.Invalid, await sms.VerifyMobileAsync(_userId, wrong, Ip));
        Assert.Equal(OtpVerifyStatus.Valid, await sms.VerifyMobileAsync(_userId, code, Ip));
    }

    [PostgresFact]
    public async Task OneNumber_GetsAtMostTheHourlyLimit()
    {
        (ISmsCodeService sms, _, _) = Build(new() { ["Sangam:Sms:PerNumberPerHour"] = "2" });
        Assert.Equal(SmsIssueStatus.Sent, (await sms.SendMobileVerificationAsync(_userId, null)).Status);
        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(SmsIssueStatus.Sent, (await sms.SendMobileVerificationAsync(_userId, null)).Status);
        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(SmsIssueStatus.RateLimited, (await sms.SendMobileVerificationAsync(_userId, null)).Status);

        await using SangamDbContext db = _pg.CreateContext();
        Assert.Contains(await db.AuditEvents.Where(e => e.Action == AuditActions.UserSmsSend).Select(e => e.Metadata).ToListAsync(),
            m => m.Contains("\"outcome\": \"limited\"", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task OneNetwork_GetsAtMostTheHourlyLimit_AcrossAccounts()
    {
        Guid other = await AddUserAsync("sms-other@example.in", "+919000000053");
        (ISmsCodeService sms, _, _) = Build(new() { ["Sangam:Sms:PerIpPerHour"] = "2" });
        Assert.Equal(SmsIssueStatus.Sent, (await sms.SendMobileVerificationAsync(_userId, Ip)).Status);
        Assert.Equal(SmsIssueStatus.Sent, (await sms.SendMobileVerificationAsync(other, Ip)).Status);
        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(SmsIssueStatus.RateLimited, (await sms.SendMobileVerificationAsync(_userId, Ip)).Status);
        Assert.Equal(SmsIssueStatus.Sent, (await sms.SendMobileVerificationAsync(_userId, "198.51.100.9")).Status);
    }

    [PostgresFact]
    public async Task NumbersOutsideTheAllowedCountries_AreNeverTexted()
    {
        Guid abroad = await AddUserAsync("sms-abroad@example.in", "+14155550123");
        (ISmsCodeService sms, InMemorySmsOutbox outbox, _) = Build();
        Assert.False(sms.IsCountryAllowed("+14155550123"));
        Assert.True(sms.IsCountryAllowed(Mobile));
        Assert.Equal(SmsIssueStatus.CountryNotAllowed, (await sms.SendMobileVerificationAsync(abroad, Ip)).Status);
        Assert.Empty(outbox.Recent);
    }

    [PostgresFact]
    public async Task ARefusingProvider_IsRecordedAsFailed()
    {
        (ISmsCodeService sms, InMemorySmsOutbox outbox, _) = Build();
        outbox.Refuse = true;
        Assert.Equal(SmsIssueStatus.ProviderFailed, (await sms.SendMobileVerificationAsync(_userId, Ip)).Status);

        await using SangamDbContext db = _pg.CreateContext();
        SmsMessage row = await db.SmsMessages.SingleAsync();
        Assert.Equal(SmsStatus.Failed, row.Status);
        Assert.Equal("refused by the test outbox", row.Error);
    }

    [PostgresFact]
    public async Task DeliveryReports_MarkTheMessage()
    {
        (ISmsCodeService sms, InMemorySmsOutbox outbox, _) = Build();
        await sms.SendMobileVerificationAsync(_userId, Ip);
        string id = outbox.LatestFor(Mobile)!.ProviderMessageId;
        DateTimeOffset at = _clock.UtcNow.AddSeconds(4);

        Assert.True(await sms.RecordDeliveryAsync(new SmsDeliveryReport(SmsSettings.OutboxProvider, id, true, at)));
        Assert.False(await sms.RecordDeliveryAsync(new SmsDeliveryReport(SmsSettings.OutboxProvider, "unknown", true, at)));

        await using SangamDbContext db = _pg.CreateContext();
        SmsMessage row = await db.SmsMessages.SingleAsync();
        Assert.Equal(SmsStatus.Delivered, row.Status);
        Assert.NotNull(row.DeliveredAt);
    }

    [PostgresFact]
    public async Task TheDailyThreshold_RaisesOneAlert()
    {
        Guid other = await AddUserAsync("sms-alert@example.in", "+919000000054");
        (ISmsCodeService sms, _, _) = Build(new() { ["Sangam:Sms:DailyAlertThreshold"] = "2" });
        await sms.SendMobileVerificationAsync(_userId, Ip);
        await sms.SendMobileVerificationAsync(other, Ip);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await sms.SendMobileVerificationAsync(_userId, Ip);

        await using SangamDbContext db = _pg.CreateContext();
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.SmsVolumeAlert));
    }

    [PostgresFact]
    public async Task WhenSmsIsOff_NothingIsSent()
    {
        (ISmsCodeService sms, _, _) = Build(new() { ["Sangam:Sms:Enabled"] = "false" });
        Assert.False(sms.Enabled);
        Assert.Equal(SmsIssueStatus.Disabled, (await sms.SendMobileVerificationAsync(_userId, Ip)).Status);
        await using SangamDbContext db = _pg.CreateContext();
        Assert.Equal(0, await db.SmsMessages.CountAsync());
    }

    private (ISmsCodeService Sms, InMemorySmsOutbox Outbox, IServiceProvider Services) Build(Dictionary<string, string?>? overrides = null)
    {
        Dictionary<string, string?> values = new(StringComparer.Ordinal)
        {
            ["ConnectionStrings:Sangam"] = _pg.ConnectionString,
            ["Sangam:Email:UseOutbox"] = "true",
            ["Sangam:Maintenance:Enabled"] = "false",
            ["Sangam:DataProtection:PersistKeys"] = "false",
            ["Sangam:Sms:Enabled"] = "true",
            ["Sangam:Sms:Provider"] = "outbox",
        };
        foreach ((string key, string? value) in overrides ?? [])
        {
            values[key] = value;
        }

        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        services.AddSingleton<IClock>(_clock);
        ServiceProvider provider = services.BuildServiceProvider();
        _providers.Add(provider);
        IServiceScope scope = provider.CreateScope();
        _scopes.Add(scope);
        return (scope.ServiceProvider.GetRequiredService<ISmsCodeService>(), provider.GetRequiredService<InMemorySmsOutbox>(), scope.ServiceProvider);
    }

    private async Task<Guid> AddUserAsync(string email, string mobile)
    {
        await using SangamDbContext db = _pg.CreateContext();
        SangamUser user = TestUsers.New(email, mobile);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task MarkVerifiedAsync(Guid userId)
    {
        await using SangamDbContext db = _pg.CreateContext();
        SangamUser user = await db.Users.SingleAsync(u => u.Id == userId);
        user.PhoneNumberConfirmed = true;
        await db.SaveChangesAsync();
    }

    [System.Text.RegularExpressions.GeneratedRegex("[0-9]{6}")]
    private static partial System.Text.RegularExpressions.Regex CodeRegex();

    private sealed class MutableClock : IClock
    {
        public MutableClock(DateTimeOffset now)
        {
            UtcNow = now;
        }

        public DateTimeOffset UtcNow { get; private set; }

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
