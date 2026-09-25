using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application;
using Sangam.Identity.Infrastructure;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Server.Api;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Endpoints;
using Sangam.Shared.Constants;
using static OpenIddict.Abstractions.OpenIddictConstants;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AddPageRoute("/Account/Login", "/login");
    o.Conventions.AddPageRoute("/Account/LoginCode", "/login/code");
    o.Conventions.AddPageRoute("/Account/LoginVerify", "/login/verify");
    o.Conventions.AddPageRoute("/Account/Register", "/register");
    o.Conventions.AddPageRoute("/Account/Verify", "/verify");
    o.Conventions.AddPageRoute("/Account/Verified", "/verified");
    o.Conventions.AddPageRoute("/Account/Forgot", "/forgot");
    o.Conventions.AddPageRoute("/Account/Reset", "/reset");
    o.Conventions.AddPageRoute("/Account/Consent", "/consent");
    o.Conventions.AddPageRoute("/Account/Home", "/account");
    o.Conventions.AddPageRoute("/Account/Logout", "/logout");
    o.Conventions.AddPageRoute("/Account/Logout", "/connect/endsession");
});
builder.Services.AddRazorComponents();
builder.Services.AddHttpClient();

builder.Services.AddSangamApplication();
builder.Services.AddSangamInfrastructure(builder.Configuration);
builder.Services.AddSangamCookies();
builder.Services.AddAuthRateLimiting(builder.Configuration);
builder.Services.AddAuthorization(o => o.AddManagementPolicy());

string? issuer = builder.Configuration["Sangam:Issuer"];
bool developmentCertificates = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");

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
         .SetEndSessionEndpointUris("connect/endsession");

        o.AllowAuthorizationCodeFlow()
         .AllowRefreshTokenFlow()
         .AllowClientCredentialsFlow()
         .RequireProofKeyForCodeExchange();

        o.RegisterScopes([.. SangamScopes.All]);
        o.RegisterClaims(Claims.Name, Claims.GivenName, Claims.FamilyName, Claims.Birthdate, Claims.Gender, Claims.Locale, Claims.Zoneinfo, Claims.UpdatedAt,
            Claims.Email, Claims.EmailVerified, Claims.PhoneNumber, Claims.PhoneNumberVerified, SangamClaims.SessionId, SangamClaims.Orgs);

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
            throw new InvalidOperationException(
                "Production signing and encryption certificates are configured in PR-08 (Sangam:Certificates). Refusing to start with development keys.");
        }

        o.DisableAccessTokenEncryption();

        OpenIddictServerAspNetCoreBuilder aspnet = o.UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough()
            .EnableTokenEndpointPassthrough()
            .EnableUserInfoEndpointPassthrough()
            .EnableEndSessionEndpointPassthrough();

        if (developmentCertificates)
        {
            // Local runs are plain HTTP on localhost; production sits behind Caddy (TLS + forwarded headers, PR-08).
            aspnet.DisableTransportSecurityRequirement();
        }
    })
    .AddValidation(o =>
    {
        // Userinfo and the management API validate access tokens issued by this same server.
        o.UseLocalServer();
        o.UseAspNetCore();
    });

WebApplication app = builder.Build();

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

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets().RequireRateLimiting(AuthRateLimiting.PolicyName);
app.MapConnectEndpoints();
app.MapManagementEndpoints();

await app.RunAsync().ConfigureAwait(false);
