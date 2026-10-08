using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Signatures;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Signatures;

/// <summary>PR-17: signature requests are signed once, freshly, by the right person, before they expire.</summary>
[Collection("postgres")]
public sealed class SignatureServiceTests : IAsyncLifetime
{
    private static readonly string[] TwoFactors = ["pwd", "otp", "mfa"];
    private readonly PostgresFixture _pg;
    private readonly MutableClock _clock = new(DateTimeOffset.UtcNow);
    private readonly FakeIssuer _issuer = new();
    private readonly List<IServiceScope> _scopes = [];
    private ServiceProvider _provider = null!;
    private Guid _appId;
    private Guid _otherAppId;
    private Guid _signer;
    private Guid _someoneElse;

    public SignatureServiceTests(PostgresFixture pg)
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
        }).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        services.AddSingleton<IClock>(_clock);
        services.AddSingleton<ISignatureTokenIssuer>(_issuer);
        _provider = services.BuildServiceProvider();

        await using SangamDbContext db = _pg.CreateContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        App qms = new() { Id = Guid.NewGuid(), ClientId = "sigma-qms", Slug = "sigma-qms", DisplayName = "Sigma QMS", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        App other = new() { Id = Guid.NewGuid(), ClientId = "other", Slug = "other", DisplayName = "Other", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        SangamUser signer = TestUsers.New("signer@example.in", "+919000000301");
        SangamUser else_ = TestUsers.New("else@example.in", "+919000000302");
        db.AddRange(qms, other, signer, else_);
        await db.SaveChangesAsync();
        (_appId, _otherAppId, _signer, _someoneElse) = (qms.Id, other.Id, signer.Id, else_.Id);
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

    [PostgresFact]
    public async Task ASignature_IsGivenOnce_WithTheAuthenticationBehindIt()
    {
        ISignatureService signatures = Service();
        Guid id = await CreateAsync(signatures);
        SignatureResult signed = await signatures.SignAsync(id, _signer, "Asha Menon", "https://id.sangam.test/", AuthenticationAssurance.Acr2, TwoFactors, _clock.UtcNow.AddMinutes(-1), "203.0.113.5");
        Assert.True(signed.Succeeded, signed.Message);
        Assert.Contains("status=signed", signed.RedirectUrl, StringComparison.Ordinal);
        Assert.Equal(("sigma-qms", "Approved", AuthenticationAssurance.AcrSign), (_issuer.Last!.Audience, _issuer.Last.Meaning, _issuer.Last.Acr));

        Assert.False((await signatures.SignAsync(id, _signer, "Asha Menon", "https://id.sangam.test/", AuthenticationAssurance.Acr2, TwoFactors, _clock.UtcNow, null)).Succeeded);
        Assert.False((await signatures.DeclineAsync(id, _signer, null)).Succeeded);

        SignatureView view = (await signatures.GetForAppAsync(_appId, id))!;
        Assert.Equal(("signed", "token-1", _signer), (view.Status, view.Token, view.DecidedBy!.Value));
        Assert.Null(await signatures.GetForAppAsync(_otherAppId, id));

        await using SangamDbContext db = _pg.CreateContext();
        SignatureRequest row = await db.SignatureRequests.SingleAsync(r => r.Id == id);
        Assert.Equal(("pwd otp mfa", AuthenticationAssurance.Acr2), (row.Amr, row.Acr));
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.UserSignatureSign && e.TargetId == id));
    }

    [PostgresFact]
    public async Task ASingleFactor_OrStale_Authentication_CannotSign()
    {
        ISignatureService signatures = Service();
        Guid id = await CreateAsync(signatures);
        Assert.False((await signatures.SignAsync(id, _signer, "A", "https://id.sangam.test/", AuthenticationAssurance.Acr1, ["pwd"], _clock.UtcNow, null)).Succeeded);
        Assert.False((await signatures.SignAsync(id, _signer, "A", "https://id.sangam.test/", AuthenticationAssurance.Acr2, TwoFactors, _clock.UtcNow.AddMinutes(-6), null)).Succeeded);
        Assert.Equal("pending", (await signatures.GetForAppAsync(_appId, id))!.Status);
    }

    [PostgresFact]
    public async Task ARequest_Expires_AndANamedSignerIsTheOnlySigner()
    {
        ISignatureService signatures = Service();
        (SignatureRequestCreated? named, _) = await signatures.CreateAsync(_appId, Input() with { Signer = _signer });
        Assert.False((await signatures.SignAsync(named!.RequestId, _someoneElse, "B", "https://id.sangam.test/", AuthenticationAssurance.Acr2, TwoFactors, _clock.UtcNow, null)).Succeeded);

        _clock.Advance(TimeSpan.FromMinutes(16));
        Assert.True((await signatures.GetAsync(named.RequestId))!.Expired);
        Assert.False((await signatures.SignAsync(named.RequestId, _signer, "A", "https://id.sangam.test/", AuthenticationAssurance.Acr2, TwoFactors, _clock.UtcNow, null)).Succeeded);
        Assert.Equal("expired", (await signatures.GetForAppAsync(_appId, named.RequestId))!.Status);
    }

    [PostgresFact]
    public async Task Declining_IsRecorded_AndBadRequestsAreRefused()
    {
        ISignatureService signatures = Service();
        Guid id = await CreateAsync(signatures);
        SignatureResult declined = await signatures.DeclineAsync(id, _signer, null);
        Assert.Contains("status=declined", declined.RedirectUrl, StringComparison.Ordinal);
        Assert.Equal("declined", (await signatures.GetForAppAsync(_appId, id))!.Status);

        Assert.NotNull((await signatures.CreateAsync(_appId, Input() with { RecordHash = "sha256:ABC" })).Error);
        Assert.NotNull((await signatures.CreateAsync(_appId, Input() with { RecordHash = "sha512:" + new string('a', 64) })).Error);
        Assert.NotNull((await signatures.CreateAsync(_appId, Input() with { Meaning = " " })).Error);
        Assert.NotNull((await signatures.CreateAsync(_appId, Input() with { Signer = Guid.NewGuid() })).Error);
    }

    private static SignatureRequestInput Input() => new("SOP-1/3", "sha256:" + new string('c', 64), "Approved", "SOP-1 revision 3", "https://qms.example/signed");

    private async Task<Guid> CreateAsync(ISignatureService signatures)
    {
        (SignatureRequestCreated? created, string? error) = await signatures.CreateAsync(_appId, Input());
        Assert.True(created is not null, error);
        return created.RequestId;
    }

    private ISignatureService Service()
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<ISignatureService>();
    }

    private sealed class FakeIssuer : ISignatureTokenIssuer
    {
        private int _count;

        public SignatureClaims? Last { get; private set; }

        public string Issue(SignatureClaims claims)
        {
            Last = claims;
            return "token-" + Interlocked.Increment(ref _count).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
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
