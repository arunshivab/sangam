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

    /// <summary>Client id of the self-service portal, which is itself an OIDC client.</summary>
    public const string PortalClientId = "sangam-portal";

    /// <summary>Development client secret of the portal.</summary>
    public const string PortalClientSecret = "sangam-dev-portal-secret-change-me";

    /// <summary>Development redirect URIs of the portal.</summary>
    public static IReadOnlyList<string> PortalRedirectUris { get; } =
    [
        "http://localhost:5200/signin-sangam",
        "https://localhost:5201/signin-sangam",
    ];

    /// <summary>Development post-logout redirect URIs of the portal.</summary>
    public static IReadOnlyList<string> PortalPostLogoutRedirectUris { get; } =
    [
        "http://localhost:5200/signout-sangam",
        "https://localhost:5201/signout-sangam",
    ];

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

        await EnsurePortalAppAsync(now, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// The self-service portal is an ordinary confidential client: it goes through the same
    /// authorization code + PKCE flow as any partner app, so the portal proves the flow works.
    /// </summary>
    private async Task EnsurePortalAppAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        App? portal = await _db.Apps.FirstOrDefaultAsync(a => a.ClientId == PortalClientId, cancellationToken).ConfigureAwait(false);
        if (portal is null)
        {
            portal = new App { Id = Guid.NewGuid(), ClientId = PortalClientId, Slug = "portal", CreatedAt = now };
            _db.Apps.Add(portal);
        }

        portal.DisplayName = "Sangam account portal";
        portal.OwnerCompanyName = "imagiQa Healthcare Services Pvt Ltd";
        portal.Description = "The account portal where you manage your Sangam account.";
        portal.HomepageUrl = "http://localhost:5200/";
        portal.BrandColour = "#0F3B38";
        portal.Glyph = "\u0BB8";
        portal.ConsentVersion = "v1";
        portal.RequireConsent = true;
        portal.Status = AppStatus.Active;
        portal.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        object? existing = await _applications.FindByClientIdAsync(PortalClientId, cancellationToken).ConfigureAwait(false);
        OpenIddictApplicationDescriptor descriptor = new();
        if (existing is not null)
        {
            await _applications.PopulateAsync(descriptor, existing, cancellationToken).ConfigureAwait(false);
        }

        descriptor.ClientId = PortalClientId;
        descriptor.ClientType = ClientTypes.Confidential;
        descriptor.ConsentType = ConsentTypes.Explicit;
        descriptor.DisplayName = "Sangam account portal";
        if (existing is null)
        {
            descriptor.ClientSecret = PortalClientSecret;
        }

        foreach (string permission in new[]
        {
            Permissions.Endpoints.Authorization,
            Permissions.Endpoints.Token,
            Permissions.Endpoints.EndSession,
            Permissions.GrantTypes.AuthorizationCode,
            Permissions.GrantTypes.RefreshToken,
            Permissions.ResponseTypes.Code,
            Permissions.Prefixes.Scope + SangamScopes.Profile,
            Permissions.Prefixes.Scope + SangamScopes.Email,
            Permissions.Prefixes.Scope + SangamScopes.Phone,
        })
        {
            descriptor.Permissions.Add(permission);
        }

        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        foreach (string uri in PortalRedirectUris)
        {
            descriptor.RedirectUris.Add(new Uri(uri));
        }

        foreach (string uri in PortalPostLogoutRedirectUris)
        {
            descriptor.PostLogoutRedirectUris.Add(new Uri(uri));
        }

        if (existing is null)
        {
            await _applications.CreateAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _applications.UpdateAsync(existing, descriptor, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 1100, Level = LogLevel.Information, Message = "Seeded development sample app '{ClientId}'.")]
    private partial void LogSeeded(string clientId);
}
