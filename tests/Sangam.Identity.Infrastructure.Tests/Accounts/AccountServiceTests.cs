using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Accounts;

/// <summary>The account flows through the real DI graph (Identity, Argon2id, OTP, outbox, audit) against PostgreSQL.</summary>
[Collection("postgres")]
public sealed class AccountServiceTests : IAsyncLifetime
{
    private static readonly RegisterUserCommand Rajesh = new("Rajesh", "Kumar", "Rajesh@Example.in", "98765 43210", new DateOnly(1984, 3, 14), Gender.Male, "Correct-Horse-2026!", "v1", "203.0.113.9", "xunit");
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;

    public AccountServiceTests(PostgresFixture pg)
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
        }).Build();
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task Register_CreatesUnverifiedUser_EmailsCode_AndAudits()
    {
        using IServiceScope scope = _provider.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        InMemoryEmailOutbox outbox = scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>();

        RegistrationOutcome outcome = await accounts.RegisterAsync(Rajesh);

        Assert.True(outcome.Result.Succeeded, string.Join("; ", outcome.Result.Errors.Select(e => e.Message)));
        UserSummary user = (await accounts.FindByEmailAsync("rajesh@example.in"))!;
        Assert.Equal("Rajesh", user.FirstName);
        Assert.Equal("Rajesh@Example.in", user.Email);
        Assert.Equal("+919876543210", user.Mobile);
        Assert.False(user.EmailVerified);
        Assert.Equal(SignInMode.Password, user.SignInPreference);

        SentEmail mail = outbox.LatestFor("Rajesh@Example.in")!;
        Assert.Contains("verification code", mail.Message.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("[0-9]{6}", mail.Message.TextBody);

        await using SangamDbContext db = _pg.CreateContext();
        SangamUser row = await db.Users.SingleAsync();
        Assert.StartsWith("$argon2id$", row.PasswordHash, StringComparison.Ordinal);
        Assert.Contains(await db.AuditEvents.Select(e => e.Action).ToListAsync(), a => a == "user.register");
    }

    [PostgresFact]
    public async Task Register_RejectsWeakPassword_BadMobile_AndDuplicates()
    {
        using IServiceScope scope = _provider.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();

        RegistrationOutcome weak = await accounts.RegisterAsync(Rajesh with { Password = "weakpass", Mobile = "12" });
        Assert.False(weak.Result.Succeeded);
        Assert.Contains(weak.Result.Errors, e => e.Field == "Password");
        Assert.Contains(weak.Result.Errors, e => e.Field == "Mobile");

        Assert.True((await accounts.RegisterAsync(Rajesh)).Result.Succeeded);
        RegistrationOutcome dupEmail = await accounts.RegisterAsync(Rajesh with { Mobile = "+919000000001" });
        RegistrationOutcome dupMobile = await accounts.RegisterAsync(Rajesh with { Email = "other@example.in" });
        Assert.False(dupEmail.Result.Succeeded);
        Assert.False(dupMobile.Result.Succeeded);
        Assert.Equal(dupEmail.Result.Errors[0].Message, dupMobile.Result.Errors[0].Message);
        Assert.Null(dupEmail.Result.Errors[0].Field);
    }

    [PostgresFact]
    public async Task Register_RefusesAnyoneUnderEighteen()
    {
        using IServiceScope scope = _provider.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        RegistrationOutcome child = await accounts.RegisterAsync(Rajesh with { DateOfBirth = today.AddYears(-17) });
        Assert.False(child.Result.Succeeded);
        AccountError error = Assert.Single(child.Result.Errors, e => e.Field == "DateOfBirth");
        Assert.Contains("18 or older", error.Message, StringComparison.Ordinal);

        // A healthcare trainee of 16 is still a child under the DPDP Act, so the answer is the same.
        Assert.False((await accounts.RegisterAsync(Rajesh with { DateOfBirth = today.AddYears(-16) })).Result.Succeeded);

        // Exactly eighteen today is allowed.
        Assert.True((await accounts.RegisterAsync(Rajesh with { DateOfBirth = today.AddYears(-18) })).Result.Succeeded);
    }

    [PostgresFact]
    public async Task Register_StillRejectsImplausibleDatesOfBirth()
    {
        using IServiceScope scope = _provider.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (DateOnly nonsense in new[] { today.AddDays(1), today.AddYears(-130) })
        {
            RegistrationOutcome outcome = await accounts.RegisterAsync(Rajesh with { DateOfBirth = nonsense });
            Assert.False(outcome.Result.Succeeded);
            Assert.Contains(outcome.Result.Errors, e => e.Field == "DateOfBirth" && e.Message == "Enter a valid date of birth.");
        }
    }

    [PostgresFact]
    public async Task VerifyCode_MarksEmailVerified_ThenPasswordSignInSucceeds()
    {
        using IServiceScope scope = _provider.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        InMemoryEmailOutbox outbox = scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>();
        Guid userId = (await accounts.RegisterAsync(Rajesh)).UserId!.Value;

        SignInCheck before = await accounts.CheckPasswordAsync(Rajesh.Email, Rajesh.Password, null, null, null);
        Assert.Equal(SignInStatus.EmailNotVerified, before.Status);

        string code = Code(outbox.LatestFor(Rajesh.Email)!);
        Assert.Equal(OtpVerifyStatus.Invalid, await accounts.VerifyCodeAsync(userId, OneTimeCodePurpose.EmailVerification, "000000", null));
        Assert.Equal(OtpVerifyStatus.Valid, await accounts.VerifyCodeAsync(userId, OneTimeCodePurpose.EmailVerification, code, null));

        SignInCheck after = await accounts.CheckPasswordAsync(Rajesh.Email, Rajesh.Password, null, null, null);
        Assert.Equal(SignInStatus.Succeeded, after.Status);
        Assert.Equal(SignInMode.Password, after.Mode);
        Assert.True(after.User!.EmailVerified);
    }

    [PostgresFact]
    public async Task CheckPassword_WrongPassword_LocksOutAfterFiveAndAudits()
    {
        using IServiceScope scope = _provider.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        await RegisterVerifiedAsync(scope, Rajesh);

        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(SignInStatus.InvalidCredentials, (await accounts.CheckPasswordAsync(Rajesh.Email, "wrong", null, null, null)).Status);
        }

        Assert.Equal(SignInStatus.LockedOut, (await accounts.CheckPasswordAsync(Rajesh.Email, "wrong", null, null, null)).Status);
        Assert.Equal(SignInStatus.LockedOut, (await accounts.CheckPasswordAsync(Rajesh.Email, Rajesh.Password, null, null, null)).Status);
        Assert.Equal(SignInStatus.InvalidCredentials, (await accounts.CheckPasswordAsync("nobody@example.in", "x", null, null, null)).Status);

        await using SangamDbContext db = _pg.CreateContext();
        List<AuditEvent> fails = await db.AuditEvents.Where(e => e.Action == "user.login.fail").ToListAsync();
        Assert.Equal(7, fails.Count);
        Assert.Contains(fails, f => f.ActorType == AuditActorType.Anonymous && f.Metadata.Contains("unknown_email", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task SignInModes_UserPreferenceAndAppPolicyDriveTheFlow()
    {
        using IServiceScope scope = _provider.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        InMemoryEmailOutbox outbox = scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>();
        Guid userId = await RegisterVerifiedAsync(scope, Rajesh);

        await accounts.SetSignInPreferenceAsync(userId, SignInMode.PasswordAndOtp);
        SignInCheck twoStep = await accounts.CheckPasswordAsync(Rajesh.Email, Rajesh.Password, null, null, null);
        Assert.Equal(SignInStatus.RequiresOtp, twoStep.Status);
        Assert.Equal(OtpVerifyStatus.Valid, await accounts.VerifyCodeAsync(userId, OneTimeCodePurpose.SignIn, Code(outbox.LatestFor(Rajesh.Email)!), null));

        // An app that mandates password-only overrides the user's two-step preference.
        Assert.Equal(SignInStatus.Succeeded, (await accounts.CheckPasswordAsync(Rajesh.Email, Rajesh.Password, SignInPolicy.Password, null, null)).Status);

        // Passwordless is only offered when the effective mode is otp_only; unknown addresses look the same.
        Assert.Equal(SignInStatus.InvalidCredentials, (await accounts.BeginOtpSignInAsync(Rajesh.Email, null)).Status);
        SignInCheck forced = await accounts.BeginOtpSignInAsync(Rajesh.Email, SignInPolicy.OtpOnly);
        Assert.Equal(SignInStatus.RequiresOtp, forced.Status);
        Assert.Equal(SignInStatus.InvalidCredentials, (await accounts.BeginOtpSignInAsync("nobody@example.in", SignInPolicy.OtpOnly)).Status);
    }

    [PostgresFact]
    public async Task PasswordReset_RotatesStampAndAcceptsNewPassword_WithoutEnumeration()
    {
        using IServiceScope scope = _provider.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        InMemoryEmailOutbox outbox = scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>();
        await RegisterVerifiedAsync(scope, Rajesh);
        string stampBefore = (await accounts.FindByEmailAsync(Rajesh.Email))!.SecurityStamp;

        Assert.Null(await accounts.RequestPasswordResetAsync("nobody@example.in", null));
        Assert.NotNull(await accounts.RequestPasswordResetAsync(Rajesh.Email, null));
        string code = Code(outbox.LatestFor(Rajesh.Email)!);

        AccountResult wrongCode = await accounts.ResetPasswordAsync(Rajesh.Email, "000000", "Brand-New-Password-2026!", null);
        AccountResult unknown = await accounts.ResetPasswordAsync("nobody@example.in", code, "Brand-New-Password-2026!", null);
        Assert.False(wrongCode.Succeeded);
        Assert.False(unknown.Succeeded);
        Assert.Equal("Code", unknown.Errors[0].Field);

        AccountResult ok = await accounts.ResetPasswordAsync(Rajesh.Email, code, "Brand-New-Password-2026!", null);
        Assert.True(ok.Succeeded);
        Assert.NotEqual(stampBefore, (await accounts.FindByEmailAsync(Rajesh.Email))!.SecurityStamp);
        Assert.Equal(SignInStatus.InvalidCredentials, (await accounts.CheckPasswordAsync(Rajesh.Email, Rajesh.Password, null, null, null)).Status);
        Assert.Equal(SignInStatus.Succeeded, (await accounts.CheckPasswordAsync(Rajesh.Email, "Brand-New-Password-2026!", null, null, null)).Status);
    }

    private static async Task<Guid> RegisterVerifiedAsync(IServiceScope scope, RegisterUserCommand command)
    {
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        InMemoryEmailOutbox outbox = scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>();
        Guid userId = (await accounts.RegisterAsync(command)).UserId!.Value;
        Assert.Equal(OtpVerifyStatus.Valid, await accounts.VerifyCodeAsync(userId, OneTimeCodePurpose.EmailVerification, Code(outbox.LatestFor(command.Email)!), null));
        return userId;
    }

    private static string Code(SentEmail mail) => System.Text.RegularExpressions.Regex.Match(mail.Message.TextBody, "[0-9]{6}").Value;
}
