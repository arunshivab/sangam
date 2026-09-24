using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Accounts;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Accounts;

[Collection("postgres")]
public sealed class OneTimeCodeServiceTests : IAsyncLifetime
{
    private static readonly OtpOptions Options = new() { Lifetime = TimeSpan.FromMinutes(10), MaxAttempts = 3, ResendCooldown = TimeSpan.FromSeconds(60), MaxPerWindow = 3, RateWindow = TimeSpan.FromHours(1) };
    private readonly PostgresFixture _pg;
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
    private Guid _userId;

    public OneTimeCodeServiceTests(PostgresFixture pg)
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
        await using SangamDbContext db = _pg.CreateContext();
        SangamUser user = TestUsers.New("otp@example.in", "+919876500001");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        _userId = user.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task Issue_ReturnsSixDigitsAndStoresOnlyAHash()
    {
        await using SangamDbContext db = _pg.CreateContext();
        OneTimeCodeService svc = new(db, _clock, Options);

        (OtpIssueResult result, string? code) = await svc.IssueAsync(_userId, OneTimeCodePurpose.EmailVerification);

        Assert.Equal(OtpIssueStatus.Sent, result.Status);
        Assert.Matches("^[0-9]{6}$", code);
        OneTimeCode row = await db.OneTimeCodes.SingleAsync();
        Assert.NotEqual(code, row.CodeHash);
        Assert.Equal(64, row.CodeHash.Length);
        Assert.Equal(_clock.UtcNow + Options.Lifetime, row.ExpiresAt);
    }

    [PostgresFact]
    public async Task Verify_AcceptsOnceThenRefuses()
    {
        await using SangamDbContext db = _pg.CreateContext();
        OneTimeCodeService svc = new(db, _clock, Options);
        (_, string? code) = await svc.IssueAsync(_userId, OneTimeCodePurpose.SignIn);

        Assert.Equal(OtpVerifyStatus.Valid, await svc.VerifyAsync(_userId, OneTimeCodePurpose.SignIn, code!));
        Assert.Equal(OtpVerifyStatus.Expired, await svc.VerifyAsync(_userId, OneTimeCodePurpose.SignIn, code!));
    }

    [PostgresFact]
    public async Task Verify_IgnoresSpacesAndIsPurposeBound()
    {
        await using SangamDbContext db = _pg.CreateContext();
        OneTimeCodeService svc = new(db, _clock, Options);
        (_, string? code) = await svc.IssueAsync(_userId, OneTimeCodePurpose.PasswordReset);

        Assert.Equal(OtpVerifyStatus.Expired, await svc.VerifyAsync(_userId, OneTimeCodePurpose.SignIn, code!));
        Assert.Equal(OtpVerifyStatus.Valid, await svc.VerifyAsync(_userId, OneTimeCodePurpose.PasswordReset, code![..3] + " " + code[3..]));
    }

    [PostgresFact]
    public async Task Verify_VoidsTheCodeAfterMaxAttempts()
    {
        await using SangamDbContext db = _pg.CreateContext();
        OneTimeCodeService svc = new(db, _clock, Options);
        (_, string? code) = await svc.IssueAsync(_userId, OneTimeCodePurpose.SignIn);
        string wrong = code == "000000" ? "111111" : "000000";

        Assert.Equal(OtpVerifyStatus.Invalid, await svc.VerifyAsync(_userId, OneTimeCodePurpose.SignIn, wrong));
        Assert.Equal(OtpVerifyStatus.Invalid, await svc.VerifyAsync(_userId, OneTimeCodePurpose.SignIn, wrong));
        Assert.Equal(OtpVerifyStatus.Expired, await svc.VerifyAsync(_userId, OneTimeCodePurpose.SignIn, wrong));
        Assert.Equal(OtpVerifyStatus.Expired, await svc.VerifyAsync(_userId, OneTimeCodePurpose.SignIn, code!));
    }

    [PostgresFact]
    public async Task Verify_RefusesAfterExpiry()
    {
        await using SangamDbContext db = _pg.CreateContext();
        OneTimeCodeService svc = new(db, _clock, Options);
        (_, string? code) = await svc.IssueAsync(_userId, OneTimeCodePurpose.SignIn);

        _clock.Advance(Options.Lifetime + TimeSpan.FromSeconds(1));

        Assert.Equal(OtpVerifyStatus.Expired, await svc.VerifyAsync(_userId, OneTimeCodePurpose.SignIn, code!));
    }

    [PostgresFact]
    public async Task Issue_EnforcesCooldownThenVoidsThePreviousCode()
    {
        await using SangamDbContext db = _pg.CreateContext();
        OneTimeCodeService svc = new(db, _clock, Options);
        (_, string? first) = await svc.IssueAsync(_userId, OneTimeCodePurpose.SignIn);

        (OtpIssueResult tooSoon, string? none) = await svc.IssueAsync(_userId, OneTimeCodePurpose.SignIn);
        Assert.Equal(OtpIssueStatus.TooSoon, tooSoon.Status);
        Assert.Null(none);
        Assert.Equal(_clock.UtcNow + Options.ResendCooldown, tooSoon.RetryAfter);
        Assert.Equal(_clock.UtcNow + Options.ResendCooldown, await svc.NextIssueAllowedAtAsync(_userId, OneTimeCodePurpose.SignIn));

        _clock.Advance(Options.ResendCooldown);
        (OtpIssueResult sent, string? second) = await svc.IssueAsync(_userId, OneTimeCodePurpose.SignIn);
        Assert.Equal(OtpIssueStatus.Sent, sent.Status);
        // The first code no longer matches the live (second) code: it is a wrong guess, not a stale one.
        Assert.Equal(OtpVerifyStatus.Invalid, await svc.VerifyAsync(_userId, OneTimeCodePurpose.SignIn, first!));
        Assert.Equal(OtpVerifyStatus.Valid, await svc.VerifyAsync(_userId, OneTimeCodePurpose.SignIn, second!));
    }

    [PostgresFact]
    public async Task Issue_RateLimitsPerWindow()
    {
        await using SangamDbContext db = _pg.CreateContext();
        OneTimeCodeService svc = new(db, _clock, Options);

        for (int i = 0; i < Options.MaxPerWindow; i++)
        {
            (OtpIssueResult r, _) = await svc.IssueAsync(_userId, OneTimeCodePurpose.EmailVerification);
            Assert.Equal(OtpIssueStatus.Sent, r.Status);
            _clock.Advance(Options.ResendCooldown);
        }

        (OtpIssueResult limited, _) = await svc.IssueAsync(_userId, OneTimeCodePurpose.EmailVerification);
        Assert.Equal(OtpIssueStatus.RateLimited, limited.Status);
    }

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
