using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Sangam.Shared.Constants;

namespace Sangam.Client;

/// <summary>Adds "Sign in with Sangam" to an ASP.NET Core application.</summary>
public static class SangamServiceCollectionExtensions
{
    /// <summary>
    /// Signs people in with Sangam: authorization code with PKCE, a cookie for this application,
    /// and the person's organisations and roles read from the ID token. Unauthenticated requests
    /// that need a signed-in person are sent to Sangam.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <param name="configure">Sets the <see cref="SangamOptions"/>.</param>
    /// <returns>The authentication builder, for adding further schemes.</returns>
    public static AuthenticationBuilder AddSangam(this IServiceCollection services, Action<SangamOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        SangamOptions sangam = new();
        configure(sangam);
        sangam.Validate();

        // R6: the management client (and the audit forwarder's tokens) use the same settings.
        services.TryAddSingleton(sangam);
        services.AddHttpClient(SangamManagementClient.HttpClientName);
        services.TryAddSingleton<SangamManagementClient>();

        return services
            .AddAuthentication(o =>
            {
                o.DefaultScheme = SangamDefaults.CookieScheme;
                o.DefaultChallengeScheme = SangamDefaults.Scheme;
            })
            .AddCookie(SangamDefaults.CookieScheme, o =>
            {
                o.Cookie.Name = sangam.CookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = sangam.RequireHttpsMetadata ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = sangam.SessionLifetime;
                o.SlidingExpiration = true;
            })
            .AddOpenIdConnect(SangamDefaults.Scheme, "Sangam", o =>
            {
                o.SignInScheme = SangamDefaults.CookieScheme;
                o.Authority = sangam.Authority;
                o.ClientId = sangam.ClientId;
                o.ClientSecret = sangam.ClientSecret;
                o.ResponseType = OpenIdConnectResponseType.Code;
                o.UsePkce = true;
                o.SaveTokens = sangam.SaveTokens;
                o.GetClaimsFromUserInfoEndpoint = true;

                // Keep Sangam's claim names as they are on the wire ("sub", "sangam_orgs"), which
                // is what SangamUser reads. Without this, .NET renames them to long URIs.
                o.MapInboundClaims = false;
                o.RequireHttpsMetadata = sangam.RequireHttpsMetadata;
                o.CallbackPath = sangam.CallbackPath;
                o.SignedOutCallbackPath = sangam.SignedOutCallbackPath;

                o.Scope.Clear();
                o.Scope.Add(SangamScopes.OpenId);
                foreach (string scope in sangam.Scopes.Where(s => s != SangamScopes.OpenId).Distinct(StringComparer.Ordinal))
                {
                    o.Scope.Add(scope);
                }

                o.TokenValidationParameters.NameClaimType = SangamUser.NameClaim;

                // PR-17: keep how and when the person authenticated, for SangamStepUp.Satisfies.
                o.ClaimActions.Remove("acr");
                o.ClaimActions.Remove("amr");
                o.ClaimActions.Remove("auth_time");

                // PR-17: SangamStepUp.ChallengeAsync carries the level asked for as acr_values.
                o.Events.OnRedirectToIdentityProvider = context =>
                {
                    if (context.Properties.Items.TryGetValue(SangamStepUp.AcrValuesItem, out string? acr) && !string.IsNullOrEmpty(acr))
                    {
                        context.ProtocolMessage.AcrValues = acr;
                    }

                    return Task.CompletedTask;
                };
            });
    }

    /// <summary>
    /// Adds the shared audit helper (SGM-208): <see cref="Audit.ISangamAudit"/> builds events in schema 1.0 from the
    /// signed-in person and the request and buffers them in a JSON Lines file; the forwarder sends them to the audit
    /// service once <see cref="Audit.SangamAuditOptions.Endpoint"/> is set. Call after <see cref="AddSangam"/>.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <param name="configure">Sets the <see cref="Audit.SangamAuditOptions"/>.</param>
    public static IServiceCollection AddSangamAudit(this IServiceCollection services, Action<Audit.SangamAuditOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.Configure(configure);
        services.AddHttpContextAccessor();
        services.TryAddSingleton<Audit.ISangamAudit, Audit.SangamAuditRecorder>();
        services.AddHostedService<Audit.SangamAuditForwarder>();
        return services;
    }
}
