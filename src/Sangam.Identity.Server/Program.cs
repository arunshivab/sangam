using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application;
using Sangam.Identity.Domain;
using Sangam.Identity.Infrastructure;
using Sangam.Identity.Infrastructure.Audit;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Sms;
using Sangam.Identity.Server.Api;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Endpoints;
using Sangam.Identity.Server.Saml;
using Sangam.Identity.Server.Verification;
using Sangam.Shared.Constants;
using Sangam.Web.Shared.Hosting;
using Sangam.Web.Shared.Localization;
using static OpenIddict.Abstractions.OpenIddictConstants;

// D-A: the audit archive's key generation, listing and reading tool, likewise.
if (args.Length > 0 && args[0] == AuditArchiveCommand.Verb)
{
    Environment.Exit(await AuditArchiveCommand.RunAsync(args[1..], Console.Out));
}

// A refused start is logged at Critical and exits with code 1 (V-08).
StartupGuard.Install(typeof(Program).Assembly);
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddSangamSecretFiles();

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AddPageRoute("/Account/Login", "/login");
    o.Conventions.AddPageRoute("/Account/LoginCode", "/login/code");
    o.Conventions.AddPageRoute("/Account/LoginVerify", "/login/verify");
    o.Conventions.AddPageRoute("/Account/LoginMfa", "/login/authenticator");
    o.Conventions.AddPageRoute("/Account/Register", "/register");
    o.Conventions.AddPageRoute("/Account/Verify", "/verify");
    o.Conventions.AddPageRoute("/Account/Verified", "/verified");
    o.Conventions.AddPageRoute("/Account/Forgot", "/forgot");
    o.Conventions.AddPageRoute("/Account/Reset", "/reset");
    o.Conventions.AddPageRoute("/Account/Consent", "/consent");
    o.Conventions.AddPageRoute("/Account/Device", "/device");
    o.Conventions.AddPageRoute("/Account/Home", "/account");
    o.Conventions.AddPageRoute("/Account/Logout", "/logout");
    o.Conventions.AddPageRoute("/Account/Logout", "/connect/endsession");
}).AddDataAnnotationsLocalization(o => o.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(Sangam.Web.Shared.Localization.SangamText)));
// PR-18: every screen's text comes from the shared catalogue (Hindi and Malayalam translated).
builder.Services.AddSangamLocalization();
builder.Services.AddRazorComponents();
builder.Services.AddHttpClient();

builder.Services.AddSangamApplication();
builder.Services.AddSangamInfrastructure(builder.Configuration);
// PR-22: the SAML identity provider's keys (a throwaway one in Development and Testing).
builder.Services.AddSingleton(Sangam.Identity.Infrastructure.Saml.SamlOptions.From(builder.Configuration, builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")));
// PR-17: the identity server holds the token-signing keys, so it alone issues signature tokens.
builder.Services.AddSingleton<Sangam.Identity.Application.Signatures.ISignatureTokenIssuer, Sangam.Identity.Server.Signatures.SignatureTokenIssuer>();
// PR-20: deliver queued back-channel logouts (only the identity server holds the signing keys).
// V-16: back-channel logout calls go through the same guard as SCIM and webhooks: no redirects, and no private-network
// address (checked again on every connection) unless Sangam:Outbound:AllowPrivateNetworks.
builder.Services.AddHttpClient(Sangam.Identity.Server.Logout.BackChannelLogoutSender.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(sp => Sangam.Identity.Infrastructure.Provisioning.OutboundHttp.CreateHandler(sp.GetRequiredService<Sangam.Identity.Infrastructure.Provisioning.OutboundSettings>().AllowPrivate));
builder.Services.AddSingleton<Sangam.Identity.Server.Logout.BackChannelLogoutSender>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Sangam.Identity.Server.Logout.BackChannelLogoutSender>());
// PR-23/24: SCIM provisioning and webhooks are delivered from here (Sangam-signed SCIM tokens need the signing keys).
builder.Services.AddSingleton<Sangam.Identity.Infrastructure.Provisioning.IServiceTokenIssuer, Sangam.Identity.Server.Provisioning.ServiceTokenIssuer>();
builder.Services.AddSingleton<Sangam.Identity.Server.Provisioning.ProvisioningWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Sangam.Identity.Server.Provisioning.ProvisioningWorker>());

string? keyRingProblem = KeyRingProtection.Validate(builder.Environment.EnvironmentName, builder.Configuration);
if (keyRingProblem is not null)
{
    throw new InvalidOperationException(keyRingProblem);
}

builder.Services.AddSangamCookies(builder.Environment.EnvironmentName);
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.SecurePolicy = SecurityHeaders.CookiePolicy(builder.Environment.EnvironmentName);
    o.Cookie.Name = SecurityHeaders.CookieName("sangam.antiforgery", builder.Environment.EnvironmentName);
});
builder.Services.AddAuthRateLimiting(builder.Configuration);
builder.Services.AddAuthorization(o => o.AddManagementPolicy());
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, Sangam.Identity.Server.Api.AccessDeniedAudit>();

string? issuer = builder.Configuration["Sangam:Issuer"];
bool developmentCertificates = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");

string? emailProblem = EmailSenderGuard.Validate(builder.Environment.EnvironmentName, builder.Configuration);
if (emailProblem is not null)
{
    throw new InvalidOperationException(emailProblem);
}

// rc.5 (ASVS V2.4.5): passwords are never hashed without the pepper outside Development and Testing.
string? pepperProblem = Sangam.Identity.Infrastructure.Security.PasswordPepperGuard.Validate(builder.Environment.EnvironmentName, builder.Configuration);
if (pepperProblem is not null)
{
    throw new InvalidOperationException(pepperProblem);
}

// rc.6 (SGM-914): the identity server now talks to DigiLocker itself (an application that requires verification,
// recovering an account), so it needs the same credentials and the same subject key as the portal.
string? digiLockerProblem = Sangam.Identity.Infrastructure.Verification.DigiLockerGuard.Validate(builder.Environment.EnvironmentName, builder.Configuration);
if (digiLockerProblem is not null)
{
    throw new InvalidOperationException(digiLockerProblem);
}

string? smsProblem = SmsGuard.Validate(builder.Environment.EnvironmentName, builder.Configuration);
if (smsProblem is not null)
{
    throw new InvalidOperationException(smsProblem);
}

// R4: the deployment's own applications (portal, consoles, the demo) are registered from settings; bad settings refuse.
IReadOnlyList<ClientSpec> clients = ClientRegistration.Read(builder.Configuration);

string? samlProblem = Sangam.Identity.Infrastructure.Saml.SamlOptions.Validate(builder.Environment.EnvironmentName, builder.Configuration);
if (samlProblem is not null)
{
    throw new InvalidOperationException(samlProblem);
}

string? auditProblem = AuditArchiveOptions.Validate(builder.Environment.EnvironmentName, builder.Configuration);
if (auditProblem is not null)
{
    throw new InvalidOperationException(auditProblem);
}

// PR-32: SIEM streaming, when switched on, needs a receiver and TLS outside Development.
string? siemProblem = Sangam.Identity.Infrastructure.Siem.SiemOptions.Validate(builder.Environment.EnvironmentName, builder.Configuration);
if (siemProblem is not null)
{
    throw new InvalidOperationException(siemProblem);
}

builder.Services.AddOpenIddict()
    .AddServer(o =>
    {
        if (!string.IsNullOrWhiteSpace(issuer))
        {
            o.SetIssuer(new Uri(issuer, UriKind.Absolute));
        }

        o.SetAuthorizationEndpointUris("connect/authorize")
         .SetTokenEndpointUris("connect/token")
         .SetUserInfoEndpointUris("connect/userinfo")
         .SetEndSessionEndpointUris("connect/endsession")
         // PR-20: RFC 7662 introspection and RFC 7009 revocation, for applications' own back ends.
         .SetIntrospectionEndpointUris("connect/introspect")
         .SetRevocationEndpointUris("connect/revoke")
         // PR-21 (SGM-219): the device authorization grant (RFC 8628) for TVs, kiosks and command lines, with the
         // code entered at /device; and pushed authorization requests (RFC 9126).
         .SetDeviceAuthorizationEndpointUris("connect/device")
         .SetEndUserVerificationEndpointUris("device")
         .SetPushedAuthorizationEndpointUris("connect/par");

        o.AllowAuthorizationCodeFlow()
         .AllowRefreshTokenFlow()
         .AllowClientCredentialsFlow()
         .AllowDeviceAuthorizationFlow()
         // PR-21: token exchange (RFC 8693) — an application's back end trades a person's access token for one
         // addressed to another Sangam application, only where it holds that audience's permission.
         .AllowTokenExchangeFlow()
         .RequireProofKeyForCodeExchange();

        // Audiences are applications' client ids, registered at any time: each exchange is checked against the
        // caller's own audience permissions (aud:<client id>) instead of a fixed list.
        o.DisableAudienceValidation();

        // PR-21: refresh tokens rotate on every use (OpenIddict's default); reusing a spent one after this leeway
        // revokes the whole sign-in — the theft signal RFC 8252 and SGM-219 ask native applications to rely on.
        o.SetRefreshTokenReuseLeeway(TimeSpan.FromSeconds(Math.Max(0, builder.Configuration.GetValue("Sangam:Tokens:RefreshReuseLeewaySeconds", 30))));

        o.RegisterScopes([.. SangamScopes.All]);
        o.RegisterClaims(Claims.Name, Claims.GivenName, Claims.FamilyName, Claims.Birthdate, Claims.Gender, Claims.Locale, Claims.Zoneinfo, Claims.UpdatedAt,
            Claims.Email, Claims.EmailVerified, Claims.PhoneNumber, Claims.PhoneNumberVerified, SangamClaims.SessionId, SangamClaims.Orgs, SangamClaims.IdentityVerified,
            Claims.AuthenticationContextReference, Claims.AuthenticationMethodReference, Claims.AuthenticationTime);

        // R7 (ASVS V7.2.1): refused clients and grants are audited.
        o.AddTokenRefusalAudit();

        // PR-17: advertise the assurance levels an application may ask for with acr_values (SGM-207 §3).
        o.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.HandleConfigurationRequestContext>(handler => handler.UseInlineHandler(context =>
        {
            context.Metadata["acr_values_supported"] = System.Collections.Immutable.ImmutableArray.CreateRange<string?>(AuthenticationAssurance.Supported);

            // PR-20: OpenID Connect Front-Channel and Back-Channel Logout 1.0, both with the session id (sid).
            context.Metadata["frontchannel_logout_supported"] = true;
            context.Metadata["frontchannel_logout_session_supported"] = true;
            context.Metadata["backchannel_logout_supported"] = true;
            context.Metadata["backchannel_logout_session_supported"] = true;
            return default;
        }));

        o.SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(5))
         .SetAccessTokenLifetime(TimeSpan.FromHours(1))
         .SetIdentityTokenLifetime(TimeSpan.FromHours(1))
         .SetRefreshTokenLifetime(TimeSpan.FromDays(14));

        if (developmentCertificates)
        {
            o.AddDevelopmentEncryptionCertificate()
             .AddDevelopmentSigningCertificate();
        }
        else
        {
            // PR-10: certificates from files; previous ones stay published during a rotation (SGM-803).
            (IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2> signing, IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2> encryption) = TokenCertificates.Load(builder.Configuration);
            foreach (System.Security.Cryptography.X509Certificates.X509Certificate2 certificate in signing)
            {
                o.AddSigningCertificate(certificate);
            }

            foreach (System.Security.Cryptography.X509Certificates.X509Certificate2 certificate in encryption)
            {
                o.AddEncryptionCertificate(certificate);
            }
        }

        o.DisableAccessTokenEncryption();

        OpenIddictServerAspNetCoreBuilder aspnet = o.UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough()
            .EnableTokenEndpointPassthrough()
            .EnableUserInfoEndpointPassthrough()
            .EnableEndSessionEndpointPassthrough()
            .EnableEndUserVerificationEndpointPassthrough();

        if (developmentCertificates)
        {
            // Local runs are plain HTTP on localhost; production sits behind Caddy (TLS + forwarded headers, PR-10).
            aspnet.DisableTransportSecurityRequirement();
        }
    })
    .AddValidation(o =>
    {
        // Userinfo and the management API validate access tokens issued by this same server.
        o.UseLocalServer();
        o.UseAspNetCore();
    });

builder.Services.AddSangamWebHosting(builder.Configuration);

WebApplication app = builder.Build();
app.Lifetime.ApplicationStarted.Register(StartupGuard.MarkStarted);

// Behind Caddy: take the client address from trusted proxies only, before anything reads it (OI-037).
app.UseForwardedHeaders();
// R7: CSP, framing, nosniff and the other security headers on every response (SGM-503). The identity server frames
// applications' front-channel logout pages.
app.UseSangamSecurityHeaders(app.Environment.EnvironmentName, identityServer: true);
app.UseSangamRequestMetrics();

if (app.Configuration.GetValue<bool>("Sangam:Database:MigrateOnStartup"))
{
    using IServiceScope scope = app.Services.CreateScope();
    SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
    await db.Database.MigrateAsync().ConfigureAwait(false);

    if (app.Configuration.GetValue<bool>("Sangam:Seed:DevelopmentSample"))
    {
        DevelopmentSeeder seeder = scope.ServiceProvider.GetRequiredService<DevelopmentSeeder>();
        await seeder.SeedAsync().ConfigureAwait(false);
    }
}

if (clients.Count > 0)
{
    using IServiceScope scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<ClientRegistration>().ApplyAsync(clients).ConfigureAwait(false);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseSangamHttpsRedirection(app.Environment.EnvironmentName, app.Configuration);
app.UseSangamClientContext();
app.UseSangamRequestCulture();
app.UseRateLimiter();
// OpenIddict answers its endpoints inside UseAuthentication, so the rate limiter stays in front of it.
app.UseAuthentication();
// PR-18: the signed-in person's profile language, unless they picked one for this browser.
app.UseSangamProfileCulture();
app.UseAuthorization();
app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets().RequireRateLimiting(AuthRateLimiting.PolicyName);
app.MapSangamLanguageSwitch();
app.MapBrandingEndpoints();
app.MapConnectEndpoints();
app.MapIdentityDigiLocker();
app.MapSamlEndpoints();
app.MapManagementEndpoints();
app.MapSmsEndpoints();

app.MapSangamHealth();
app.MapSangamSecurityTxt(app.Configuration);

await app.RunAsync().ConfigureAwait(false);
