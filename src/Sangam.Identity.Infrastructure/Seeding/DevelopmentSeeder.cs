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

    /// <summary>Redirect URI of the sample app (the PR-07 sample partner listens here).</summary>
    public const string SampleRedirectUri = "http://localhost:5900/signin-sangam";

    /// <summary>Post-logout redirect URI of the sample app.</summary>
    public const string SamplePostLogoutRedirectUri = "http://localhost:5900/signout-sangam";

    /// <summary>Development-only redirect URIs: the identity server's own /dev/callback page, on either local host name.</summary>
    public static IReadOnlyList<string> DevCallbackRedirectUris { get; } =
    [
        "http://localhost:5100/dev/callback",
        "http://127.0.0.1:5100/dev/callback",
        "https://localhost:5101/dev/callback",
    ];

    /// <summary>PKCE verifier the /dev/callback page uses, so a browser walkthrough needs no tooling.</summary>
    public const string DevCallbackVerifier = "sangam-dev-callback-verifier-0123456789abcdef";

    /// <summary>S256 challenge for <see cref="DevCallbackVerifier"/>.</summary>
    public static string DevCallbackChallenge { get; } = Convert.ToBase64String(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(DevCallbackVerifier)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

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
        object? existing = await _applications.FindByClientIdAsync(SampleClientId, cancellationToken).ConfigureAwait(false);

        OpenIddictApplicationDescriptor descriptor = new();
        if (existing is not null)
        {
            await _applications.PopulateAsync(descriptor, existing, cancellationToken).ConfigureAwait(false);
        }

        descriptor.ClientId = SampleClientId;
        descriptor.ClientType = ClientTypes.Confidential;
        descriptor.ConsentType = ConsentTypes.Explicit;
        descriptor.DisplayName = "Sangam development sample";
        if (existing is null)
        {
            descriptor.ClientSecret = SampleClientSecret;
        }

        string[] permissions =
        [
            Permissions.Endpoints.Authorization,
            Permissions.Endpoints.Token,
            Permissions.Endpoints.EndSession,
            Permissions.GrantTypes.AuthorizationCode,
            Permissions.GrantTypes.RefreshToken,
            Permissions.GrantTypes.ClientCredentials,
            Permissions.ResponseTypes.Code,
            .. SangamScopes.All.Where(scope => scope != SangamScopes.OpenId && scope != SangamScopes.OfflineAccess).Select(scope => Permissions.Prefixes.Scope + scope),
        ];
        bool changed = existing is null || !permissions.All(descriptor.Permissions.Contains) || descriptor.RedirectUris.Count < 1 + DevCallbackRedirectUris.Count;
        foreach (string permission in permissions)
        {
            descriptor.Permissions.Add(permission);
        }

        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        descriptor.RedirectUris.Add(new Uri(SampleRedirectUri));
        foreach (string devCallback in DevCallbackRedirectUris)
        {
            descriptor.RedirectUris.Add(new Uri(devCallback));
            descriptor.PostLogoutRedirectUris.Add(new Uri(devCallback));
        }

        descriptor.PostLogoutRedirectUris.Add(new Uri(SamplePostLogoutRedirectUri));

        if (existing is null)
        {
            await _applications.CreateAsync(descriptor, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (changed)
        {
            await _applications.UpdateAsync(existing, descriptor, cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    private async Task<bool> EnsureSampleAppAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        App? app = await _db.Apps.FirstOrDefaultAsync(a => a.ClientId == SampleClientId, cancellationToken).ConfigureAwait(false);
        bool created = app is null;

        if (app is null)
        {
            app = new App { Id = Guid.NewGuid(), ClientId = SampleClientId, Slug = "dev-sample", CreatedAt = now };
            _db.Apps.Add(app);
        }

        // Branding and consent settings are refreshed on every run, so a database seeded by an
        // earlier release picks up columns added since.
        app.DisplayName = "Sangam development sample";
        app.OwnerCompanyName = "imagiQa Healthcare Services Pvt Ltd";
        app.Description = "Local-only partner app used to exercise the sign-in, consent and token flows.";
        app.HomepageUrl = "http://localhost:5900/";
        app.PrivacyUrl = "http://localhost:5900/privacy";
        app.TermsUrl = "http://localhost:5900/terms";
        app.BrandColour = "#1D4E89";
        app.Glyph = "\u0932\u093F";
        app.ConsentVersion = "v1";
        app.RequireConsent = true;
        app.Status = AppStatus.Active;
        app.UpdatedAt = now;

        bool hasSystemRole = await _db.Roles.AnyAsync(r => r.AppId == app.Id && r.Code == "org_admin" && r.OrgId == null, cancellationToken).ConfigureAwait(false);
        if (!hasSystemRole)
        {
            _db.Roles.Add(new Role
            {
                Id = Guid.NewGuid(),
                AppId = app.Id,
                Code = "org_admin",
                DisplayName = "Organisation admin",
                Description = "Manages members and roles of an organisation for this app.",
                Permissions = "[\"org:manage\",\"user:invite\"]",
                IsSystem = true,
                CreatedAt = now,
            });
        }

        bool changed = created || !hasSystemRole || _db.ChangeTracker.HasChanges();
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    [LoggerMessage(EventId = 1100, Level = LogLevel.Information, Message = "Seeded development sample app '{ClientId}'.")]
    private partial void LogSeeded(string clientId);
}
