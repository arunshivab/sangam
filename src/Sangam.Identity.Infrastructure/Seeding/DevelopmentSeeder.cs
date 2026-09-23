using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Shared.Constants;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Sangam.Identity.Infrastructure.Seeding;

/// <summary>
/// Seeds the development sample partner app so the token endpoint can be exercised locally:
/// an OpenIddict application allowed the client-credentials grant, the matching <see cref="App"/>
/// business record, and its system <c>org_admin</c> role. Idempotent. Never runs in production
/// (gated by <c>Sangam:Seed:DevelopmentSample</c>).
/// </summary>
public sealed partial class DevelopmentSeeder
{
    /// <summary>Client id of the sample app.</summary>
    public const string SampleClientId = "sangam-dev-sample";

    /// <summary>Client secret of the sample app. Development only; changing it here changes the seed.</summary>
    public const string SampleClientSecret = "sangam-dev-sample-secret-change-me";

    private readonly SangamDbContext _db;
    private readonly IOpenIddictApplicationManager _applications;
    private readonly IOpenIddictScopeManager _scopes;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ILogger<DevelopmentSeeder> _logger;

    /// <summary>Initialises the seeder.</summary>
    /// <param name="db">Database.</param>
    /// <param name="applications">OpenIddict application manager.</param>
    /// <param name="scopes">OpenIddict scope manager.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="logger">Logger.</param>
    public DevelopmentSeeder(
        SangamDbContext db,
        IOpenIddictApplicationManager applications,
        IOpenIddictScopeManager scopes,
        IAuditWriter audit,
        IClock clock,
        ILogger<DevelopmentSeeder> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _applications = applications ?? throw new ArgumentNullException(nameof(applications));
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Creates the sample app, its OpenIddict client and its scopes if they do not exist.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        bool changed = false;
        changed |= await EnsureScopesAsync(cancellationToken).ConfigureAwait(false);
        changed |= await EnsureSampleClientAsync(cancellationToken).ConfigureAwait(false);
        changed |= await EnsureSampleAppAsync(cancellationToken).ConfigureAwait(false);

        if (changed)
        {
            await _audit.WriteAsync(
                new AuditEntry(AuditActions.SystemSeed, AuditActorType.System, Metadata: "{\"seed\":\"development-sample\"}"),
                cancellationToken).ConfigureAwait(false);
            LogSeeded(SampleClientId);
        }
    }

    private async Task<bool> EnsureScopesAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        foreach (string scope in SangamScopes.All)
        {
            if (await _scopes.FindByNameAsync(scope, cancellationToken).ConfigureAwait(false) is null)
            {
                await _scopes.CreateAsync(new OpenIddictScopeDescriptor { Name = scope, DisplayName = scope }, cancellationToken).ConfigureAwait(false);
                changed = true;
            }
        }

        return changed;
    }

    private async Task<bool> EnsureSampleClientAsync(CancellationToken cancellationToken)
    {
        if (await _applications.FindByClientIdAsync(SampleClientId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return false;
        }

        OpenIddictApplicationDescriptor descriptor = new()
        {
            ClientId = SampleClientId,
            ClientSecret = SampleClientSecret,
            ClientType = ClientTypes.Confidential,
            ConsentType = ConsentTypes.Explicit,
            DisplayName = "Sangam development sample",
        };
        descriptor.Permissions.Add(Permissions.Endpoints.Token);
        descriptor.Permissions.Add(Permissions.GrantTypes.ClientCredentials);
        descriptor.Permissions.Add(Permissions.Prefixes.Scope + SangamScopes.OrgsRead);

        await _applications.CreateAsync(descriptor, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> EnsureSampleAppAsync(CancellationToken cancellationToken)
    {
        if (await _db.Apps.AnyAsync(a => a.ClientId == SampleClientId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        DateTimeOffset now = _clock.UtcNow;
        App app = new()
        {
            Id = Guid.NewGuid(),
            ClientId = SampleClientId,
            Slug = "dev-sample",
            DisplayName = "Sangam development sample",
            OwnerCompanyName = "imagiQa Healthcare Services Pvt Ltd",
            Description = "Local-only partner app used to exercise the token endpoint.",
            RequireConsent = true,
            Status = AppStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Role orgAdmin = new()
        {
            Id = Guid.NewGuid(),
            AppId = app.Id,
            Code = "org_admin",
            DisplayName = "Organisation admin",
            Description = "Manages members and roles of an organisation for this app.",
            Permissions = "[\"org:manage\",\"user:invite\"]",
            IsSystem = true,
            CreatedAt = now,
        };

        _db.Apps.Add(app);
        _db.Roles.Add(orgAdmin);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    [LoggerMessage(EventId = 1100, Level = LogLevel.Information, Message = "Seeded development sample app '{ClientId}'.")]
    private partial void LogSeeded(string clientId);
}
