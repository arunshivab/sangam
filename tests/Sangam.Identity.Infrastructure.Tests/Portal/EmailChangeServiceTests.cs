using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Accounts;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Portal;

/// <summary>OI-022: change of e-mail — password, code to the new address, notice to the old one.</summary>
[Collection("postgres")]
public sealed partial class EmailChangeServiceTests : IAsyncLifetime
{
    private const string Password = "Kaveri-River-2026!";
    private static readonly RegisterUserCommand Ravi = new("Ravi", "Menon", "ravi@example.in", "+919876500077", new DateOnly(1986, 6, 12), Gender.Male, Password, "v1", "203.0.113.7", "xunit");
    private static readonly RegisterUserCommand Asha = new("Asha", "Pillai", "asha@example.in", "+919876500088", new DateOnly(1990, 2, 1), Gender.Female, Password, "v1", "203.0.113.8", "xunit");
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private Guid _userId;

    public EmailChangeServiceTests(PostgresFixture pg)
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
            ["Sangam:DataProtection:PersistKeys"] = "false",
        }).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();
        _userId = await RegisterAsync(Ravi);
        await RegisterAsync(Asha);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task Change_SendsTheCodeToTheNewAddress_ChangesTheAccount_AndTellsTheOldAddress()
    {
        using IServiceScope scope = _provider.CreateScope();
        IEmailChangeService changes = scope.ServiceProvider.GetRequiredService<IEmailChangeService>();
        InMemoryEmailOutbox outbox = scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>();

        EmailChangeResult requested = await changes.RequestAsync(_userId, "ravi.menon@example.org", Password);
        Assert.Equal(EmailChangeStatus.CodeSent, requested.Status);
        Assert.Equal("ravi.menon@example.org", (await changes.GetPendingAsync(_userId))!.NewEmail);

        string code = CodeIn(outbox.LatestFor("ravi.menon@example.org")!.Message.TextBody);
        EmailChangeResult confirmed = await changes.ConfirmAsync(_userId, code);

        Assert.Equal(EmailChangeStatus.Changed, confirmed.Status);
        await using SangamDbContext db = _pg.CreateContext();
        SangamUser user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == _userId);
        Assert.Equal("ravi.menon@example.org", user.Email);
        Assert.Equal("RAVI.MENON@EXAMPLE.ORG", user.NormalizedEmail);
        Assert.Contains("was changed", outbox.LatestFor("ravi@example.in")!.Message.Subject, StringComparison.Ordinal);
        Assert.Null(await changes.GetPendingAsync(_userId));
        List<string> actions = await db.AuditEvents.AsNoTracking().Where(e => e.TargetId == _userId).Select(e => e.Action).ToListAsync();
        Assert.Contains(AuditActions.UserEmailChangeRequest, actions);
        Assert.Contains(AuditActions.UserEmailChange, actions);
    }

    [PostgresFact]
    public async Task Change_IsRefused_ForAWrongPassword_TheSameAddress_AnAddressInUse_OrAWrongCode()
    {
        using IServiceScope scope = _provider.CreateScope();
        IEmailChangeService changes = scope.ServiceProvider.GetRequiredService<IEmailChangeService>();

        Assert.Equal(EmailChangeStatus.WrongPassword, (await changes.RequestAsync(_userId, "new@example.org", "not-my-password")).Status);
        Assert.Equal(EmailChangeStatus.SameAsCurrent, (await changes.RequestAsync(_userId, "RAVI@example.in", Password)).Status);
        Assert.Equal(EmailChangeStatus.Unavailable, (await changes.RequestAsync(_userId, "asha@example.in", Password)).Status);
        Assert.Equal(EmailChangeStatus.InvalidEmail, (await changes.RequestAsync(_userId, "not an address", Password)).Status);
        Assert.Equal(EmailChangeStatus.NothingPending, (await changes.ConfirmAsync(_userId, "123456")).Status);

        await changes.RequestAsync(_userId, "new@example.org", Password);
        Assert.Equal(EmailChangeStatus.WrongCode, (await changes.ConfirmAsync(_userId, "000000")).Status);
    }

    [PostgresFact]
    public async Task ASignInCode_CannotConfirmAChange()
    {
        using IServiceScope scope = _provider.CreateScope();
        IEmailChangeService changes = scope.ServiceProvider.GetRequiredService<IEmailChangeService>();
        OneTimeCodeService codes = scope.ServiceProvider.GetRequiredService<OneTimeCodeService>();

        await changes.RequestAsync(_userId, "new@example.org", Password);
        (_, string? signInCode) = await codes.IssueAsync(_userId, OneTimeCodePurpose.SignIn);

        Assert.Equal(EmailChangeStatus.WrongCode, (await changes.ConfirmAsync(_userId, signInCode!)).Status);
    }

    private static string CodeIn(string body) => SixDigits().Match(body).Value;

    [GeneratedRegex("[0-9]{6}")]
    private static partial Regex SixDigits();

    private async Task<Guid> RegisterAsync(RegisterUserCommand command)
    {
        using IServiceScope scope = _provider.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        InMemoryEmailOutbox outbox = scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>();
        Guid id = (await accounts.RegisterAsync(command)).UserId!.Value;
        await accounts.VerifyCodeAsync(id, OneTimeCodePurpose.EmailVerification, CodeIn(outbox.LatestFor(command.Email)!.Message.TextBody), null);
        return id;
    }
}
