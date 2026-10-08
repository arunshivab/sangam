using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Provisioning;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Provisioning;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Provisioning;

/// <summary>
/// PR-24 (SGM-217): signed webhooks — Standard Webhooks signatures (with the published test vector), only the events an
/// endpoint asked for, ids and no personal data in the body, retries then giving up with the owners told, secret
/// rotation with both secrets signing for a day, replay with the same id, and the partner console's rules.
/// </summary>
[Collection("postgres")]
public sealed class WebhookTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private readonly List<IServiceScope> _scopes = [];
    private ServiceProvider _provider = null!;
    private Receiver _receiver = null!;
    private Guid _app;
    private Guid _owner;
    private Guid _nurse;
    private Guid _hospital;

    public WebhookTests(PostgresFixture pg)
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
        _receiver = await Receiver.StartAsync();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sangam"] = _pg.ConnectionString,
            ["Sangam:Email:UseOutbox"] = "true",
            ["Sangam:Maintenance:Enabled"] = "false",
            ["Sangam:DataProtection:PersistKeys"] = "false",
            ["Sangam:Outbound:AllowPrivateNetworks"] = "true",
        }).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using SangamDbContext db = _pg.CreateContext();
        App his = new() { Id = Guid.NewGuid(), ClientId = "lipi-his", Slug = "lipi-his", DisplayName = "LiPi HIS", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        db.Apps.Add(his);
        SangamUser owner = TestUsers.New("owner@his.test", "+919876503001");
        SangamUser nurse = TestUsers.New("meera@his.test", "+919876503002");
        nurse.FirstName = "Meera";
        db.Users.AddRange(owner, nurse);
        db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = his.Id, UserId = owner.Id, Role = AppAdminRole.Owner, GrantedAt = now });
        db.AppGrants.Add(new AppGrant { Id = Guid.NewGuid(), AppId = his.Id, UserId = nurse.Id, GrantedAt = now });
        await db.SaveChangesAsync();
        (_app, _owner, _nurse) = (his.Id, owner.Id, nurse.Id);
        _hospital = Guid.NewGuid();
        IManagementService management = Scoped<IManagementService>();
        await management.UpsertRoleAsync(_app, "nurse", new RoleUpsert("Nurse", null, [], null), ManagementActor.Api);
        await management.UpsertOrganisationAsync(_app, _hospital, new OrganisationUpsert("Apulki Hospital", "hospital", null, null), ManagementActor.Api);
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

        if (_receiver is not null)
        {
            await _receiver.DisposeAsync();
        }
    }

    [Fact]
    public void Signatures_MatchTheStandardWebhooksTestVector()
    {
        // The published Standard Webhooks example: any receiver library verifies Sangam's signatures.
        const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
        const string Body = "{\"test\": 2432232314}";
        Assert.Equal("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", WebhookSigner.Sign("msg_p5jXN8AQM9LWM0D4loKWxJek", 1614265330, Body, Secret));
        DateTimeOffset then = DateTimeOffset.FromUnixTimeSeconds(1614265330);
        Assert.True(WebhookSigner.Verify("msg_p5jXN8AQM9LWM0D4loKWxJek", "1614265330", "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", Body, Secret, then));
        Assert.False(WebhookSigner.Verify("msg_p5jXN8AQM9LWM0D4loKWxJek", "1614265330", "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", Body + " ", Secret, then));
        Assert.False(WebhookSigner.Verify("msg_p5jXN8AQM9LWM0D4loKWxJek", "1614265330", "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", Body, Secret, then.AddMinutes(6)));
    }

    [PostgresFact]
    public async Task AnEvent_IsDelivered_Signed_ToTheEndpointsThatAskedForIt_WithIdsOnly()
    {
        WebhookResult granted = await AddAsync([AppEventTypes.MembershipGranted, AppEventTypes.UserCreated]);
        WebhookResult revokedOnly = await AddAsync([AppEventTypes.MembershipRevoked], "/other");
        Assert.StartsWith(WebhookSigner.SecretPrefix, granted.Secret, StringComparison.Ordinal);

        Assert.Equal(ManagementStatus.Ok, (await Scoped<IManagementService>().UpsertMembershipAsync(_app, _hospital, _nurse, new MembershipUpsert("nurse", true), ManagementActor.Api)).Status);
        await RunAsync();

        Received message = Assert.Single(_receiver.Messages, m => m.Path == "/hook");
        Assert.DoesNotContain(_receiver.Messages, m => m.Path == "/other");
        Assert.True(WebhookSigner.Verify(message.Id, message.Timestamp, message.Signature, message.Body, granted.Secret!, DateTimeOffset.UtcNow));
        Assert.False(WebhookSigner.Verify(message.Id, message.Timestamp, message.Signature, message.Body, revokedOnly.Secret!, DateTimeOffset.UtcNow));

        JsonElement body = JsonDocument.Parse(message.Body).RootElement;
        Assert.Equal(message.Id, body.GetProperty("id").GetString());
        Assert.Equal("membership.granted", body.GetProperty("type").GetString());
        Assert.Equal("lipi-his", body.GetProperty("app_id").GetString());
        Assert.Equal(_nurse.ToString("D"), body.GetProperty("subject").GetProperty("sub").GetString());
        Assert.Equal(_hospital.ToString("D"), body.GetProperty("tenant").GetProperty("org_id").GetString());
        Assert.Equal("nurse", body.GetProperty("data").GetProperty("role").GetString());
        Assert.True(body.GetProperty("data").GetProperty("applies_to_descendants").GetBoolean());
        Assert.DoesNotContain("meera", message.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("application/json; charset=utf-8", message.ContentType);
    }

    [PostgresFact]
    public async Task AFailingEndpoint_IsRetried_ThenGivenUp_MarkedFailing_AndTheOwnersAreTold()
    {
        await AddAsync([AppEventTypes.MembershipGranted]);
        _receiver.Fail = true;
        await Scoped<IManagementService>().UpsertMembershipAsync(_app, _hospital, _nurse, new MembershipUpsert("nurse", false), ManagementActor.Api);
        await RunAsync();
        await using (SangamDbContext db = _pg.CreateContext())
        {
            WebhookDelivery first = await db.WebhookDeliveries.SingleAsync();
            Assert.Equal(("pending", 1, 500), (first.Status, first.Attempts, first.LastStatusCode));
            Assert.Equal("boom", first.ResponseSnippet);
            for (int i = 0; i < ScimProvisioner.Backoff.Count; i++)
            {
                await db.WebhookDeliveries.ExecuteUpdateAsync(u => u.SetProperty(d => d.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
                await Scoped<WebhookSender>().DeliverDueAsync();
            }

            WebhookDelivery dead = await db.WebhookDeliveries.AsNoTracking().SingleAsync();
            Assert.Equal(("dead", ScimProvisioner.Backoff.Count + 1), (dead.Status, dead.Attempts));
            Assert.Equal("failing", (await db.WebhookEndpoints.SingleAsync()).Status);
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.WebhookFailing));
        }

        Assert.Equal(ScimProvisioner.Backoff.Count + 1, _receiver.Messages.Select(m => m.Id).Distinct().Count() == 1 ? _receiver.Messages.Count : -1);
        InMemoryEmailOutbox outbox = _provider.GetRequiredService<InMemoryEmailOutbox>();
        Assert.Contains("has stopped working", outbox.LatestFor("owner@his.test")!.Message.Subject, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ARotatedSecret_SignsAlongsideTheOldOne_ForADay_AndAReplayKeepsTheId()
    {
        WebhookResult added = await AddAsync([AppEventTypes.MembershipGranted]);
        Guid endpoint = (await Scoped<IWebhookService>().GetAsync(_owner, _app))!.Endpoints.Single().Id;
        WebhookResult rotated = await Scoped<IWebhookService>().RotateSecretAsync(_owner, _app, endpoint);
        Assert.True(rotated.Succeeded);
        Assert.NotEqual(added.Secret, rotated.Secret);

        Assert.True((await Scoped<IWebhookService>().SendTestAsync(_owner, _app, endpoint)).Succeeded);
        await RunAsync();
        Received ping = Assert.Single(_receiver.Messages);
        Assert.Equal(2, ping.Signature.Split(' ').Length);
        Assert.True(WebhookSigner.Verify(ping.Id, ping.Timestamp, ping.Signature, ping.Body, added.Secret!, DateTimeOffset.UtcNow));
        Assert.True(WebhookSigner.Verify(ping.Id, ping.Timestamp, ping.Signature, ping.Body, rotated.Secret!, DateTimeOffset.UtcNow));
        Assert.Equal("ping", JsonDocument.Parse(ping.Body).RootElement.GetProperty("type").GetString());

        // A day later, only the new secret signs.
        await using (SangamDbContext db = _pg.CreateContext())
        {
            await db.WebhookEndpoints.ExecuteUpdateAsync(u => u.SetProperty(e => e.PreviousSecretExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        long delivered = (await Scoped<IWebhookService>().GetAsync(_owner, _app))!.Deliveries.Single().Id;
        Assert.True((await Scoped<IWebhookService>().ReplayAsync(_owner, _app, delivered)).Succeeded);
        await RunAsync();
        Received replay = _receiver.Messages.Last();
        Assert.Equal(ping.Id, replay.Id);
        Assert.Equal(ping.Body, replay.Body);
        Assert.Single(replay.Signature.Split(' '));
        Assert.True(WebhookSigner.Verify(replay.Id, replay.Timestamp, replay.Signature, replay.Body, rotated.Secret!, DateTimeOffset.UtcNow));
        Assert.False(WebhookSigner.Verify(replay.Id, replay.Timestamp, replay.Signature, replay.Body, added.Secret!, DateTimeOffset.UtcNow));
    }

    [PostgresFact]
    public async Task OnlyTheApplicationsAdministrators_ManageEndpoints_AndSecretsAreNeverShownAgain()
    {
        IWebhookService service = Scoped<IWebhookService>();
        Assert.Null(await service.GetAsync(_nurse, _app));
        Assert.False((await service.AddAsync(_nurse, _app, Input([AppEventTypes.UserCreated]))).Succeeded);
        Assert.Contains("https", (await service.AddAsync(_owner, _app, Input([AppEventTypes.UserCreated]) with { Url = "ftp://his.example.in/hook" })).Message, StringComparison.Ordinal);
        Assert.False((await service.AddAsync(_owner, _app, Input(["user.teleported"]))).Succeeded);
        Assert.False((await service.AddAsync(_owner, _app, Input([]))).Succeeded);

        WebhookResult added = await service.AddAsync(_owner, _app, Input([AppEventTypes.UserCreated]));
        await using SangamDbContext db = _pg.CreateContext();
        WebhookEndpoint stored = await db.WebhookEndpoints.SingleAsync();
        Assert.DoesNotContain(added.Secret![WebhookSigner.SecretPrefix.Length..], stored.ProtectedSecret, StringComparison.Ordinal);
        string overview = JsonSerializer.Serialize(await service.GetAsync(_owner, _app));
        Assert.DoesNotContain(added.Secret, overview, StringComparison.Ordinal);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.WebhookEndpointSave));

        Assert.True((await service.RemoveAsync(_owner, _app, stored.Id)).Succeeded);
        Assert.Empty((await service.GetAsync(_owner, _app))!.Endpoints);
    }

    private WebhookEndpointInput Input(IReadOnlyList<string> events, string path = "/hook") => new(_receiver.BaseUrl + path, events, "Ward system", true);

    private async Task<WebhookResult> AddAsync(IReadOnlyList<string> events, string path = "/hook")
    {
        WebhookResult result = await Scoped<IWebhookService>().AddAsync(_owner, _app, Input(events, path));
        Assert.True(result.Succeeded, result.Message);
        return result;
    }

    private async Task RunAsync()
    {
        await Scoped<AppEventDispatcher>().DispatchAsync();
        await Scoped<WebhookSender>().DeliverDueAsync();
    }

    private T Scoped<T>()
        where T : notnull
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    private sealed record Received(string Path, string Id, string Timestamp, string Signature, string Body, string? ContentType);

    /// <summary>A webhook receiver on a real port: records each message, or fails with 500 on demand.</summary>
    private sealed class Receiver : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Receiver(WebApplication app)
        {
            _app = app;
        }

        public ConcurrentQueue<Received> Messages { get; } = new();

        public bool Fail { get; set; }

        public string BaseUrl { get; private set; } = string.Empty;

        public static async Task<Receiver> StartAsync()
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            WebApplication app = builder.Build();
            Receiver receiver = new(app);
            app.MapPost("/{**path}", async (HttpContext context) =>
            {
                using StreamReader reader = new(context.Request.Body);
                string body = await reader.ReadToEndAsync();
                receiver.Messages.Enqueue(new Received(context.Request.Path, context.Request.Headers["webhook-id"].ToString(), context.Request.Headers["webhook-timestamp"].ToString(), context.Request.Headers["webhook-signature"].ToString(), body, context.Request.ContentType));
                return receiver.Fail ? Results.Text("boom", statusCode: 500) : Results.NoContent();
            });
            await app.StartAsync();
            receiver.BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
            return receiver;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
