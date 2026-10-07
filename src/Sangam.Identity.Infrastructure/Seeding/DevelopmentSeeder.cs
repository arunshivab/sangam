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

    /// <summary>Client id of the admin console.</summary>
    public const string AdminClientId = "sangam-admin";

    /// <summary>Development client secret of the admin console.</summary>
    public const string AdminClientSecret = "sangam-dev-admin-secret-change-me";

    /// <summary>Development redirect URIs of the admin console.</summary>
    public static IReadOnlyList<string> AdminRedirectUris { get; } =
    [
        "http://localhost:5300/signin-sangam",
        "https://localhost:5301/signin-sangam",
    ];

    /// <summary>Development post-logout redirect URIs of the admin console.</summary>
    public static IReadOnlyList<string> AdminPostLogoutRedirectUris { get; } =
    [
        "http://localhost:5300/signout-sangam",
        "https://localhost:5301/signout-sangam",
    ];

    /// <summary>Client id of imagiQa, the sample hospital application in <c>samples/</c>.</summary>
    public const string ImagiqaClientId = "imagiqa";

    /// <summary>Development client secret of imagiQa.</summary>
    public const string ImagiqaClientSecret = "imagiqa-dev-secret-change-me";

    /// <summary>imagiQa's development redirect URIs.</summary>
    public static IReadOnlyList<string> ImagiqaRedirectUris { get; } =
    [
        "http://localhost:5500/signin-sangam",
        "https://localhost:5501/signin-sangam",
    ];

    /// <summary>imagiQa's development post-logout redirect URIs.</summary>
    public static IReadOnlyList<string> ImagiqaPostLogoutRedirectUris { get; } =
    [
        "http://localhost:5500/signout-sangam",
        "https://localhost:5501/signout-sangam",
    ];

    /// <summary>Client id of the partner console.</summary>
    public const string PartnerClientId = "sangam-partner";

    /// <summary>Development client secret of the partner console.</summary>
    public const string PartnerClientSecret = "sangam-dev-partner-secret-change-me";

    /// <summary>Development redirect URIs of the partner console.</summary>
    public static IReadOnlyList<string> PartnerRedirectUris { get; } =
    [
        "http://localhost:5400/signin-sangam",
        "https://localhost:5401/signin-sangam",
    ];

    /// <summary>Development post-logout redirect URIs of the partner console.</summary>
    public static IReadOnlyList<string> PartnerPostLogoutRedirectUris { get; } =
    [
        "http://localhost:5400/signout-sangam",
        "https://localhost:5401/signout-sangam",
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
        changed |= await EnsureImagiqaClientAsync(cancellationToken).ConfigureAwait(false);
        changed |= await EnsureImagiqaAppAsync(cancellationToken).ConfigureAwait(false);

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
        await EnsureConsoleAppAsync(now, cancellationToken).ConfigureAwait(false);
        await EnsurePartnerConsoleAppAsync(now, cancellationToken).ConfigureAwait(false);

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
    /// imagiQa is registered exactly as a partner's application would be: explicit consent,
    /// PKCE, and <c>orgs.read</c> so its sign-in carries the person's hospital roles.
    /// </summary>
    private async Task<bool> EnsureImagiqaClientAsync(CancellationToken cancellationToken)
    {
        object? existing = await _applications.FindByClientIdAsync(ImagiqaClientId, cancellationToken).ConfigureAwait(false);
        OpenIddictApplicationDescriptor descriptor = new();
        if (existing is not null)
        {
            await _applications.PopulateAsync(descriptor, existing, cancellationToken).ConfigureAwait(false);
        }

        descriptor.ClientId = ImagiqaClientId;
        descriptor.ClientType = ClientTypes.Confidential;
        descriptor.ConsentType = ConsentTypes.Explicit;
        descriptor.DisplayName = "imagiQa";
        if (existing is null)
        {
            descriptor.ClientSecret = ImagiqaClientSecret;
        }

        string[] permissions =
        [
            Permissions.Endpoints.Authorization,
            Permissions.Endpoints.Token,
            Permissions.Endpoints.EndSession,
            Permissions.GrantTypes.AuthorizationCode,
            Permissions.GrantTypes.RefreshToken,
            Permissions.ResponseTypes.Code,
            Permissions.Prefixes.Scope + SangamScopes.Profile,
            Permissions.Prefixes.Scope + SangamScopes.Email,
            Permissions.Prefixes.Scope + SangamScopes.OrgsRead,
        ];
        bool changed = existing is null
            || !permissions.All(descriptor.Permissions.Contains)
            || !ImagiqaRedirectUris.All(u => descriptor.RedirectUris.Contains(new Uri(u)));
        foreach (string permission in permissions)
        {
            descriptor.Permissions.Add(permission);
        }

        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        foreach (string uri in ImagiqaRedirectUris)
        {
            descriptor.RedirectUris.Add(new Uri(uri));
        }

        foreach (string uri in ImagiqaPostLogoutRedirectUris)
        {
            descriptor.PostLogoutRedirectUris.Add(new Uri(uri));
        }

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

    /// <summary>
    /// imagiQa's registry entry and its role vocabulary. Created once and then left alone, so what
    /// its partner changes on the partner console survives a restart.
    /// </summary>
    private async Task<bool> EnsureImagiqaAppAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        App? app = await _db.Apps.FirstOrDefaultAsync(a => a.ClientId == ImagiqaClientId, cancellationToken).ConfigureAwait(false);
        bool created = app is null;
        if (app is null)
        {
            app = new App
            {
                Id = Guid.NewGuid(),
                ClientId = ImagiqaClientId,
                Slug = "imagiqa",
                DisplayName = "imagiQa",
                OwnerCompanyName = "imagiQa Healthcare Services Pvt Ltd",
                Description = "A small hospital information system that shows how an application signs people in with Sangam.",
                HomepageUrl = "http://localhost:5500/",
                BrandColour = "#2A3F8F",
                Glyph = "iQ",
                ConsentVersion = "v1",
                RequireConsent = true,
                Status = AppStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Apps.Add(app);
        }

        (string Code, string Name, string Description, string Permissions)[] roles =
        [
            ("org_admin", "Organisation admin", "Manages members and roles of an organisation for this app.", "[\"org:manage\",\"user:invite\"]"),
            ("doctor", "Doctor", "Registers and finds patients, reads vitals, writes consultation notes.", "[\"patients:read\",\"patients:write\",\"vitals:read\",\"notes:write\"]"),
            ("nurse", "Nurse", "Registers and finds patients, records vitals, reads consultation notes.", "[\"patients:read\",\"patients:write\",\"vitals:write\",\"notes:read\"]"),
        ];
        List<string> existingRoles = await _db.Roles.Where(r => r.AppId == app.Id && r.OrgId == null).Select(r => r.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach ((string code, string name, string description, string permissions) in roles.Where(r => !existingRoles.Contains(r.Code)))
        {
            _db.Roles.Add(new Role
            {
                Id = Guid.NewGuid(),
                AppId = app.Id,
                Code = code,
                DisplayName = name,
                Description = description,
                Permissions = permissions,
                IsSystem = code == "org_admin",
                CreatedAt = now,
            });
        }

        bool changed = created || _db.ChangeTracker.HasChanges();
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
        portal.IsPlatform = true;
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

    /// <summary>The operator console: a confidential client like any other; its extra bars are enforced inside it.</summary>
    private Task EnsureConsoleAppAsync(DateTimeOffset now, CancellationToken cancellationToken)
        => EnsureConfidentialConsoleAsync(
            new ConsoleClient(AdminClientId, AdminClientSecret, "console", "Sangam console", "The platform operator console.", "http://localhost:5300/", AdminRedirectUris, AdminPostLogoutRedirectUris),
            now,
            cancellationToken);

    /// <summary>The partner console: where an application's own staff manage it. Its bars are enforced inside it too.</summary>
    private Task EnsurePartnerConsoleAppAsync(DateTimeOffset now, CancellationToken cancellationToken)
        => EnsureConfidentialConsoleAsync(
            new ConsoleClient(PartnerClientId, PartnerClientSecret, "partners", "Sangam partner console", "Where partners manage their applications on Sangam.", "http://localhost:5400/", PartnerRedirectUris, PartnerPostLogoutRedirectUris),
            now,
            cancellationToken);

    private async Task EnsureConfidentialConsoleAsync(ConsoleClient client, DateTimeOffset now, CancellationToken cancellationToken)
    {
        App? console = await _db.Apps.FirstOrDefaultAsync(a => a.ClientId == client.ClientId, cancellationToken).ConfigureAwait(false);
        if (console is null)
        {
            console = new App { Id = Guid.NewGuid(), ClientId = client.ClientId, Slug = client.Slug, CreatedAt = now };
            _db.Apps.Add(console);
        }

        console.DisplayName = client.DisplayName;
        console.OwnerCompanyName = "imagiQa Healthcare Services Pvt Ltd";
        console.Description = client.Description;
        console.HomepageUrl = client.HomepageUrl;
        console.BrandColour = "#15302E";
        console.Glyph = "\u0B95";
        console.ConsentVersion = "v1";
        console.RequireConsent = true;
        console.Status = AppStatus.Active;
        console.IsPlatform = true;
        console.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        object? existing = await _applications.FindByClientIdAsync(client.ClientId, cancellationToken).ConfigureAwait(false);
        OpenIddictApplicationDescriptor descriptor = new();
        if (existing is not null)
        {
            await _applications.PopulateAsync(descriptor, existing, cancellationToken).ConfigureAwait(false);
        }

        descriptor.ClientId = client.ClientId;
        descriptor.ClientType = ClientTypes.Confidential;
        descriptor.ConsentType = ConsentTypes.Explicit;
        descriptor.DisplayName = client.DisplayName;
        if (existing is null)
        {
            descriptor.ClientSecret = client.Secret;
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
        })
        {
            descriptor.Permissions.Add(permission);
        }

        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        foreach (string uri in client.RedirectUris)
        {
            descriptor.RedirectUris.Add(new Uri(uri));
        }

        foreach (string uri in client.PostLogoutRedirectUris)
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

    private sealed record ConsoleClient(
        string ClientId,
        string Secret,
        string Slug,
        string DisplayName,
        string Description,
        string HomepageUrl,
        IReadOnlyList<string> RedirectUris,
        IReadOnlyList<string> PostLogoutRedirectUris);

    [LoggerMessage(EventId = 1100, Level = LogLevel.Information, Message = "Seeded development sample app '{ClientId}'.")]
    private partial void LogSeeded(string clientId);
}
