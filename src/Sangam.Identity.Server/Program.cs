using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application;
using Sangam.Identity.Infrastructure;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Endpoints;
using Sangam.Shared.Constants;

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
    o.Conventions.AddPageRoute("/Account/Home", "/account");
    o.Conventions.AddPageRoute("/Account/Logout", "/logout");
});
builder.Services.AddRazorComponents();

builder.Services.AddSangamApplication();
builder.Services.AddSangamInfrastructure(builder.Configuration);
builder.Services.AddSangamCookies();
builder.Services.AddAuthRateLimiting(builder.Configuration);
builder.Services.AddAuthorization();

string? issuer = builder.Configuration["Sangam:Issuer"];
bool developmentCertificates = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");

builder.Services.AddOpenIddict()
    .AddServer(o =>
    {
        if (!string.IsNullOrWhiteSpace(issuer))
        {
            o.SetIssuer(new Uri(issuer, UriKind.Absolute));
        }

        o.SetTokenEndpointUris("connect/token");
        o.AllowClientCredentialsFlow();
        o.RegisterScopes([.. SangamScopes.All]);

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
            .EnableTokenEndpointPassthrough();

        if (developmentCertificates)
        {
            // Local runs are plain HTTP on localhost; production sits behind Caddy (TLS + forwarded headers, PR-08).
            aspnet.DisableTransportSecurityRequirement();
        }
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

await app.RunAsync().ConfigureAwait(false);
