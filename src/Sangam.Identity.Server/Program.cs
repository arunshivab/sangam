using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application;
using Sangam.Identity.Domain;
using Sangam.Identity.Infrastructure;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Policies;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Sms;
using Sangam.Identity.Server.Api;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Endpoints;
using Sangam.Shared.Constants;
using Sangam.Web.Shared.Localization;
using static OpenIddict.Abstractions.OpenIddictConstants;

// D-J: the breached-password list's import and refresh tool runs from this same image, then exits.
if (args.Length > 0 && args[0] == BreachListCommand.Verb)
{
    Environment.Exit(await BreachListCommand.RunAsync(args[1..], Console.Out));
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
// PR-17: the identity server holds the token-signing keys, so it alone issues signature tokens.
builder.Services.AddSingleton<Sangam.Identity.Application.Signatures.ISignatureTokenIssuer, Sangam.Identity.Server.Signatures.SignatureTokenIssuer>();
// PR-20: deliver queued back-channel logouts (only the identity server holds the signing keys).
builder.Services.AddHttpClient(Sangam.Identity.Server.Logout.BackChannelLogoutSender.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<Sangam.Identity.Server.Logout.BackChannelLogoutSender>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Sangam.Identity.Server.Logout.BackChannelLogoutSender>());

string? keyRingProblem = KeyRingProtection.Validate(builder.Environment.EnvironmentName, builder.Configuration);
if (keyRingProblem is not null)
{
    throw new InvalidOperationException(keyRingProblem);
}

builder.Services.AddSangamCookies();
builder.Services.AddAuthRateLimiting(builder.Configuration);
builder.Services.AddAuthorization(o => o.AddManagementPolicy());

string? issuer = builder.Configuration["Sangam:Issuer"];
bool developmentCertificates = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");

string? emailProblem = EmailSenderGuard.Validate(builder.Environment.EnvironmentName, builder.Configuration);
if (emailProblem is not null)
{
    throw new InvalidOperationException(emailProblem);
}

string? smsProblem = SmsGuard.Validate(builder.Environment.EnvironmentName, builder.Configuration);
if (smsProblem is not null)
{
    throw new InvalidOperationException(smsProblem);
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
         .SetRevocationEndpointUris("connect/revoke");

        o.AllowAuthorizationCodeFlow()
         .AllowRefreshTokenFlow()
         .AllowClientCredentialsFlow()
         .RequireProofKeyForCodeExchange();

        o.RegisterScopes([.. SangamScopes.All]);
        o.RegisterClaims(Claims.Name, Claims.GivenName, Claims.FamilyName, Claims.Birthdate, Claims.Gender, Claims.Locale, Claims.Zoneinfo, Claims.UpdatedAt,
            Claims.Email, Claims.EmailVerified, Claims.PhoneNumber, Claims.PhoneNumberVerified, SangamClaims.SessionId, SangamClaims.Orgs,
            Claims.AuthenticationContextReference, Claims.AuthenticationMethodReference, Claims.AuthenticationTime);

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
            .EnableEndSessionEndpointPassthrough();

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
app.MapManagementEndpoints();
app.MapSmsEndpoints();

app.MapSangamHealth();

await app.RunAsync().ConfigureAwait(false);
