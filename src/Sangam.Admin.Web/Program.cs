using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Sangam.Admin.Web.Components;
using Sangam.Identity.Application;
using Sangam.Identity.Infrastructure;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Shared.Constants;
using Sangam.Web.Shared.Hosting;
using Sangam.Web.Shared.Localization;
using static OpenIddict.Abstractions.OpenIddictConstants;

// A refused start is logged at Critical and exits with code 1 (V-08).
StartupGuard.Install(typeof(Program).Assembly);
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddSangamSecretFiles();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
// PR-18: every screen's text comes from the shared catalogue (Hindi and Malayalam translated).
builder.Services.AddSangamLocalization();
builder.Services.AddSangamApplication();
builder.Services.AddSangamInfrastructure(builder.Configuration);

string? keyRingProblem = KeyRingProtection.Validate(builder.Environment.EnvironmentName, builder.Configuration);
if (keyRingProblem is not null)
{
    throw new InvalidOperationException(keyRingProblem);
}

// D-B: this host sends e-mail too (codes, notices, invitations), so it needs Anjal like the identity server.
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

// rc.5 (ASVS V12.4.2): logos are uploaded here, and none is kept unscanned.
string? antivirusProblem = Sangam.Identity.Infrastructure.Customisation.AntivirusGuard.Validate(builder.Environment.EnvironmentName, builder.Configuration);
if (antivirusProblem is not null)
{
    throw new InvalidOperationException(antivirusProblem);
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();

// Bootstrapping: "dotnet run -- create-operator someone@example.in" works only while the
// platform has no operator at all. After that, owners grant access from the console.
if (args.Length > 0 && string.Equals(args[0], OperatorBootstrapper.CommandName, StringComparison.OrdinalIgnoreCase))
{
    using WebApplication bootstrapHost = builder.Build();
    return await OperatorBootstrapper.RunAsync(bootstrapHost.Services, args).ConfigureAwait(false);
}

string authority = builder.Configuration["Sangam:Authority"] ?? "https://id.sangamid.in";

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(o =>
    {
        o.Cookie.Name = SecurityHeaders.CookieName("sangam.admin", builder.Environment.EnvironmentName);
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = SecurityHeaders.CookiePolicy(builder.Environment.EnvironmentName);
        // Shorter than the portal's: a console session left open is a bigger problem.
        o.ExpireTimeSpan = TimeSpan.FromHours(2);
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = SessionLifetime.ValidateAsync;
    })
    .AddOpenIdConnect(o =>
    {
        o.Events.OnTicketReceived = SessionLifetime.StampAsync;
        o.Authority = authority;
        o.ClientId = builder.Configuration["Sangam:Admin:ClientId"] ?? DevelopmentSeeder.AdminClientId;
        o.ClientSecret = builder.Configuration["Sangam:Admin:ClientSecret"] ?? string.Empty;
        o.ResponseType = OpenIdConnectResponseType.Code;
        o.UsePkce = true;
        o.SaveTokens = true;
        o.GetClaimsFromUserInfoEndpoint = true;
        o.MapInboundClaims = false;
        o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        o.CallbackPath = "/signin-sangam";
        o.SignedOutCallbackPath = "/signout-sangam";
        o.Scope.Clear();
        o.Scope.Add(SangamScopes.OpenId);
        o.Scope.Add(SangamScopes.Profile);
        o.Scope.Add(SangamScopes.Email);
        o.TokenValidationParameters.NameClaimType = Claims.Name;
        o.TokenValidationParameters.RoleClaimType = Claims.Role;
    });

builder.Services.AddAntiforgery(o =>
{
    o.Cookie.SecurePolicy = SecurityHeaders.CookiePolicy(builder.Environment.EnvironmentName);
    o.Cookie.Name = SecurityHeaders.CookieName("sangam.antiforgery", builder.Environment.EnvironmentName);
});
builder.Services.AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy);

builder.Services.AddSangamWebHosting(builder.Configuration);

WebApplication app = builder.Build();
app.Lifetime.ApplicationStarted.Register(StartupGuard.MarkStarted);

// Behind Caddy: take the client address from trusted proxies only, before anything reads it (OI-037).
app.UseForwardedHeaders();
// R7: CSP, framing, nosniff and the other security headers on every response (SGM-503).
app.UseSangamSecurityHeaders(app.Environment.EnvironmentName, identityServer: false, authority);
app.UseSangamRequestMetrics();

if (!app.Environment.IsDevelopment())
{
    app.UseSangamErrorPage();
    app.UseHsts();
}

app.UseSangamHttpsRedirection(app.Environment.EnvironmentName, app.Configuration);
app.UseSangamClientContext();
app.UseSangamRequestCulture();
app.UseAuthentication();
// PR-18: the signed-in person's profile language, unless they picked one for this browser.
app.UseSangamProfileCulture();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapGet("/signout", (HttpContext context) =>
{
    context.Response.Headers["Clear-Site-Data"] = SecurityHeaders.ClearSiteData;
    return Results.SignOut(
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
})
    .AllowAnonymous();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode(o =>
{
    // R7: Blazor would send its own Content-Security-Policy (frame-ancestors 'self') on every page, in place of the
    // full policy the security headers set; that one already forbids all framing.
    o.ContentSecurityFrameAncestorsPolicy = null;
});

app.MapSangamLanguageSwitch();
app.MapSangamHealth();
app.MapSangamSecurityTxt(app.Configuration);

await app.RunAsync().ConfigureAwait(false);
return 0;
