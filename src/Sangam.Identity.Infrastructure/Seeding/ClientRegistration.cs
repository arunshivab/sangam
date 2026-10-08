using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
/// Registers the applications a deployment runs — Sangam's own portal and consoles, and the imagiQa demo at
/// demo.sangamid.in (D-I) — from settings, in every environment, each time the identity server starts:
/// <code>
/// Sangam:Clients:&lt;key&gt;:ClientId     sangam-portal, sangam-admin, sangam-partner, imagiqa …
/// Sangam:Clients:&lt;key&gt;:Kind         portal | console | partner | application | native | device
/// Sangam:Clients:&lt;key&gt;:BaseUrl      https://account.sangamid.in/  (callbacks are /signin-sangam and /signout-sangam)
/// Sangam:Clients:&lt;key&gt;:Secret       from a secret file (Sangam__Clients__&lt;key&gt;__Secret), 32+ characters
/// Sangam:Clients:&lt;key&gt;:DisplayName, :Description, :OwnerCompanyName (applications)
/// Sangam:Clients:&lt;key&gt;:RequirePushedAuthorization  true: this application must use PAR (RFC 9126)
/// Sangam:Clients:&lt;key&gt;:ExchangeAudiences   client ids this application may exchange tokens for (RFC 8693)
/// Sangam:Clients:&lt;key&gt;:RedirectUris  native only: claimed https, private-use scheme or loopback (RFC 8252)
/// </code>
/// PR-21: <c>native</c> applications (Android, iOS, desktop) are public clients with PKCE and no secret, redirected by
/// the system browser to the addresses RFC 8252 allows; <c>device</c> applications (TVs, kiosks, command lines) use the
/// device authorization grant and have no redirect address at all.
/// Creates what is missing and corrects what drifted: redirect addresses, permissions, the secret (rotated when the
/// file changes) and the platform flag. Redirect addresses are exactly the configured ones — nothing from development
/// survives. Each change is audited. Never touches an application that is not in the settings.
/// </summary>
public sealed partial class ClientRegistration
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Sangam:Clients";

    private readonly SangamDbContext _db;
    private readonly IOpenIddictApplicationManager _applications;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ILogger<ClientRegistration> _logger;

    /// <summary>Initialises the registration.</summary>
    /// <param name="db">Database.</param>
    /// <param name="applications">OpenIddict applications.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="logger">Logger.</param>
    public ClientRegistration(SangamDbContext db, IOpenIddictApplicationManager applications, IAuditWriter audit, IClock clock, ILogger<ClientRegistration> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _applications = applications ?? throw new ArgumentNullException(nameof(applications));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The configured clients; refuses settings that cannot be right.</summary>
    /// <param name="configuration">Configuration.</param>
    public static IReadOnlyList<ClientSpec> Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        List<ClientSpec> specs = [];
        foreach (IConfigurationSection section in configuration.GetSection(SectionName).GetChildren())
        {
            string clientId = section["ClientId"] ?? section.Key;
            string kind = (section["Kind"] ?? "application").ToLowerInvariant();
            string secret = section["Secret"] ?? string.Empty;
            if (kind is not ("portal" or "console" or "partner" or "application" or "native" or "device"))
            {
                throw new InvalidOperationException($"{SectionName}:{section.Key}:Kind must be portal, console, partner, application, native or device. Refusing to start.");
            }

            bool confidential = kind is not ("native" or "device");
            Uri root = new("https://invalid.local/");
            List<Uri> redirects = [];
            if (confidential)
            {
                if (!Uri.TryCreate(section["BaseUrl"], UriKind.Absolute, out Uri? baseUrl) || (baseUrl.Scheme != Uri.UriSchemeHttps && !baseUrl.IsLoopback))
                {
                    throw new InvalidOperationException($"{SectionName}:{section.Key}:BaseUrl must be an https address. Refusing to start.");
                }

                if (secret.Length < 32 || secret.Contains("change-me", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"{SectionName}:{section.Key}:Secret is missing, shorter than 32 characters, or a development value. Refusing to start.");
                }

                root = new Uri(baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl.AbsoluteUri : baseUrl.AbsoluteUri + "/");
            }
            else
            {
                if (secret.Length > 0)
                {
                    throw new InvalidOperationException($"{SectionName}:{section.Key} is a {kind} application: it is a public client and must have no secret, which an installed app cannot keep. Refusing to start.");
                }

                foreach (string value in Split(section["RedirectUris"]))
                {
                    redirects.Add(NativeRedirectUris.Validate(value) is string problem
                        ? throw new InvalidOperationException($"{SectionName}:{section.Key}:RedirectUris: {problem} Refusing to start.")
                        : new Uri(value, UriKind.Absolute));
                }

                if (kind == "native" && redirects.Count == 0)
                {
                    throw new InvalidOperationException($"{SectionName}:{section.Key}: a native application needs at least one redirect address. Refusing to start.");
                }

                if (kind == "device" && redirects.Count > 0)
                {
                    throw new InvalidOperationException($"{SectionName}:{section.Key}: a device application signs in with a code, not a redirect. Refusing to start.");
                }
            }

            specs.Add(new ClientSpec(
                clientId,
                kind,
                root,
                secret,
                section["DisplayName"] ?? DefaultName(kind, clientId),
                section["Description"],
                section["OwnerCompanyName"] ?? PlatformOwner.Name,
                section["Slug"] ?? section.Key.ToLowerInvariant())
            {
                RedirectUris = redirects,
                RequirePushedAuthorization = section.GetValue("RequirePushedAuthorization", false),
                ExchangeAudiences = Split(section["ExchangeAudiences"]),
            });
        }

        return specs;
    }

    /// <summary>Makes the registrations match the settings; returns how many clients changed.</summary>
    /// <param name="specs">The configured clients.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> ApplyAsync(IReadOnlyList<ClientSpec> specs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specs);
        int changed = 0;
        foreach (ClientSpec spec in specs)
        {
            if (await ApplyAsync(spec, cancellationToken).ConfigureAwait(false))
            {
                changed++;
                await _audit.WriteAsync(
                    new AuditEntry(AuditActions.SystemClientRegistered, AuditActorType.System, Metadata: JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["client_id"] = spec.ClientId,
                        ["kind"] = spec.Kind,
                        ["base_url"] = spec.BaseUrl.AbsoluteUri,
                    })),
                    cancellationToken).ConfigureAwait(false);
                LogRegistered(spec.ClientId, spec.BaseUrl.AbsoluteUri);
            }
        }

        return changed;
    }

    private async Task<bool> ApplyAsync(ClientSpec spec, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        bool platform = spec.Kind is "portal" or "console" or "partner";
        bool confidential = spec.Kind is not ("native" or "device");
        App? app = await _db.Apps.FirstOrDefaultAsync(a => a.ClientId == spec.ClientId, cancellationToken).ConfigureAwait(false);
        bool changed = app is null;
        if (app is null)
        {
            app = new App { Id = Guid.NewGuid(), ClientId = spec.ClientId, Slug = spec.Slug, CreatedAt = now, Status = AppStatus.Active, ConsentVersion = "v1", RequireConsent = true };
            _db.Apps.Add(app);
        }

        string? homepage = confidential ? spec.BaseUrl.AbsoluteUri : app.HomepageUrl;
        changed |= app.DisplayName != spec.DisplayName || app.IsPlatform != platform || app.HomepageUrl != homepage || app.OwnerCompanyName != spec.OwnerCompanyName;
        app.DisplayName = spec.DisplayName;
        app.OwnerCompanyName = spec.OwnerCompanyName;
        app.Description = spec.Description ?? app.Description ?? DefaultDescription(spec.Kind);
        app.HomepageUrl = homepage;
        app.IsPlatform = platform;
        if (platform)
        {
            app.BrandColour = spec.Kind == "portal" ? "#0F3B38" : "#15302E";
            app.Glyph = spec.Kind == "portal" ? "ஸ" : "க";
        }

        app.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        object? existing = await _applications.FindByClientIdAsync(spec.ClientId, cancellationToken).ConfigureAwait(false);
        OpenIddictApplicationDescriptor descriptor = new();
        if (existing is not null)
        {
            await _applications.PopulateAsync(descriptor, existing, cancellationToken).ConfigureAwait(false);
        }

        Uri[] redirects = confidential ? [new(spec.BaseUrl, "signin-sangam")] : [.. spec.RedirectUris];
        Uri[] postLogout = confidential ? [new(spec.BaseUrl, "signout-sangam")] : [.. spec.RedirectUris.Where(u => spec.Kind == "native")];
        string[] permissions = Permissions(spec);
        string[] requirements = spec.Kind == "device"
            ? []
            : spec.RequirePushedAuthorization
                ? [Requirements.Features.ProofKeyForCodeExchange, Requirements.Features.PushedAuthorizationRequests]
                : [Requirements.Features.ProofKeyForCodeExchange];
        string clientType = confidential ? ClientTypes.Confidential : ClientTypes.Public;
        bool secretChanged = confidential && (existing is null || !await _applications.ValidateClientSecretAsync(existing, spec.Secret, cancellationToken).ConfigureAwait(false));
        changed |= existing is null
            || secretChanged
            || descriptor.ClientType != clientType
            || !descriptor.RedirectUris.SetEquals(redirects)
            || !descriptor.PostLogoutRedirectUris.SetEquals(postLogout)
            || !descriptor.Permissions.SetEquals(permissions)
            || !descriptor.Requirements.SetEquals(requirements)
            || descriptor.DisplayName != spec.DisplayName;
        if (!changed)
        {
            return false;
        }

        descriptor.ClientId = spec.ClientId;
        descriptor.ClientType = clientType;
        descriptor.ApplicationType = spec.Kind == "native" ? ApplicationTypes.Native : ApplicationTypes.Web;
        descriptor.ConsentType = ConsentTypes.Explicit;
        descriptor.DisplayName = spec.DisplayName;
        descriptor.ClientSecret = secretChanged ? spec.Secret : null;
        descriptor.Permissions.Clear();
        descriptor.Permissions.UnionWith(permissions);
        descriptor.Requirements.Clear();
        descriptor.Requirements.UnionWith(requirements);
        descriptor.RedirectUris.Clear();
        descriptor.RedirectUris.UnionWith(redirects);
        descriptor.PostLogoutRedirectUris.Clear();
        descriptor.PostLogoutRedirectUris.UnionWith(postLogout);
        if (existing is null)
        {
            await _applications.CreateAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _applications.UpdateAsync(existing, descriptor, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private static string[] Permissions(ClientSpec spec)
    {
        string kind = spec.Kind;
        List<string> list =
        [
            OpenIddictConstants.Permissions.Endpoints.Token,
            OpenIddictConstants.Permissions.Endpoints.Revocation,
            OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
            OpenIddictConstants.Permissions.Prefixes.Scope + SangamScopes.Profile,
            OpenIddictConstants.Permissions.Prefixes.Scope + SangamScopes.Email,
        ];
        if (kind == "device")
        {
            list.Add(OpenIddictConstants.Permissions.Endpoints.DeviceAuthorization);
            list.Add(OpenIddictConstants.Permissions.GrantTypes.DeviceCode);
        }
        else
        {
            list.Add(OpenIddictConstants.Permissions.Endpoints.Authorization);
            list.Add(OpenIddictConstants.Permissions.Endpoints.EndSession);
            list.Add(OpenIddictConstants.Permissions.Endpoints.PushedAuthorization);
            list.Add(OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode);
            list.Add(OpenIddictConstants.Permissions.ResponseTypes.Code);
        }

        if (kind is not ("native" or "device"))
        {
            // Introspection needs a client secret.
            list.Add(OpenIddictConstants.Permissions.Endpoints.Introspection);
        }

        if (kind == "portal")
        {
            list.Add(OpenIddictConstants.Permissions.Prefixes.Scope + SangamScopes.Phone);
        }
        else if (kind is "application" or "native" or "device")
        {
            list.Add(OpenIddictConstants.Permissions.Prefixes.Scope + SangamScopes.OrgsRead);
        }

        if (kind is "native" or "device")
        {
            list.Add(OpenIddictConstants.Permissions.Prefixes.Scope + SangamScopes.OfflineAccess);
        }

        if (spec.ExchangeAudiences.Count > 0 && kind is not ("native" or "device"))
        {
            list.Add(OpenIddictConstants.Permissions.GrantTypes.TokenExchange);
            list.AddRange(spec.ExchangeAudiences.Select(a => OpenIddictConstants.Permissions.Prefixes.Audience + a));
        }

        return [.. list];
    }

    private static List<string> Split(string? value)
        => [.. (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static string DefaultName(string kind, string clientId) => kind switch
    {
        "portal" => "Sangam account portal",
        "console" => "Sangam console",
        "partner" => "Sangam partner console",
        _ => clientId,
    };

    private static string DefaultDescription(string kind) => kind switch
    {
        "portal" => "The account portal where you manage your Sangam account.",
        "console" => "The platform operator console.",
        "partner" => "Where partners manage their applications on Sangam.",
        _ => "An application that signs people in with Sangam.",
    };

    [LoggerMessage(EventId = 1101, Level = LogLevel.Information, Message = "Registered client '{ClientId}' at {BaseUrl} from settings.")]
    private partial void LogRegistered(string clientId, string baseUrl);
}

/// <summary>One configured client.</summary>
/// <param name="ClientId">Client id.</param>
/// <param name="Kind">portal, console, partner or application.</param>
/// <param name="BaseUrl">Its address, ending in a slash.</param>
/// <param name="Secret">Its client secret.</param>
/// <param name="DisplayName">Its name.</param>
/// <param name="Description">What it is.</param>
/// <param name="OwnerCompanyName">Who runs it.</param>
/// <param name="Slug">Its short name.</param>
public sealed record ClientSpec(string ClientId, string Kind, Uri BaseUrl, string Secret, string DisplayName, string? Description, string OwnerCompanyName, string Slug)
{
    /// <summary>Native applications' redirect addresses (RFC 8252).</summary>
    public IReadOnlyList<Uri> RedirectUris { get; init; } = [];

    /// <summary>Whether the application must use pushed authorization requests (RFC 9126).</summary>
    public bool RequirePushedAuthorization { get; init; }

    /// <summary>Client ids the application may exchange a person's token for (RFC 8693).</summary>
    public IReadOnlyList<string> ExchangeAudiences { get; init; } = [];

    /// <inheritdoc />
    public override string ToString() => $"{ClientId} ({Kind})";
}
