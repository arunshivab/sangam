using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Sangam.Identity.Application;
using Sangam.Identity.Infrastructure;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Verification;
using Sangam.SelfService.Web.Components;
using Sangam.SelfService.Web.Verification;
using Sangam.Shared.Constants;
using Sangam.Web.Shared.Hosting;
using Sangam.Web.Shared.Localization;
using static OpenIddict.Abstractions.OpenIddictConstants;

// A refused start is logged at Critical and exits with code 1 (V-08).
StartupGuard.Install(typeof(Program).Assembly);
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddSangamSecretFiles();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
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

// PR-26: DigiLocker, when switched on, needs its credentials and a real key for hashing DigiLocker ids.
string? digiLockerProblem = DigiLockerGuard.Validate(builder.Environment.EnvironmentName, builder.Configuration);
if (digiLockerProblem is not null)
{
    throw new InvalidOperationException(digiLockerProblem);
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();

string authority = builder.Configuration["Sangam:Authority"] ?? "https://id.sangamid.in";
string clientId = builder.Configuration["Sangam:Portal:ClientId"] ?? "sangam-portal";
string clientSecret = builder.Configuration["Sangam:Portal:ClientSecret"] ?? string.Empty;

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(o =>
    {
        o.Cookie.Name = "sangam.portal";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = SecurityHeaders.CookiePolicy(builder.Environment.EnvironmentName);
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = SessionLifetime.ValidateAsync;
    })
    .AddOpenIdConnect(o =>
    {
        o.Events.OnTicketReceived = SessionLifetime.StampAsync;
        // The portal is just another partner app: authorization code + PKCE, same as LiPi HIS.
        o.Authority = authority;
        o.ClientId = clientId;
        o.ClientSecret = clientSecret;
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
        o.Scope.Add(SangamScopes.Phone);
        o.TokenValidationParameters.NameClaimType = Claims.Name;
        o.TokenValidationParameters.RoleClaimType = Claims.Role;
    });

builder.Services.AddAntiforgery(o => o.Cookie.SecurePolicy = SecurityHeaders.CookiePolicy(builder.Environment.EnvironmentName));
builder.Services.AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy);

builder.Services.AddSangamWebHosting(builder.Configuration);

WebApplication app = builder.Build();
app.Lifetime.ApplicationStarted.Register(StartupGuard.MarkStarted);

// Behind Caddy: take the client address from trusted proxies only, before anything reads it (OI-037).
app.UseForwardedHeaders();
// R7: CSP, framing, nosniff and the other security headers on every response (SGM-503).
// DigiLocker's sign-in page is where the verification form's answer redirects (form-action covers redirects).
Sangam.Identity.Infrastructure.Verification.DigiLockerSettings digiLocker = Sangam.Identity.Infrastructure.Verification.DigiLockerSettings.From(app.Configuration);
app.UseSangamSecurityHeaders(app.Environment.EnvironmentName, identityServer: false, authority, digiLocker.Enabled ? digiLocker.AuthorizeUrl : null);
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

// Sign-out here ends the portal cookie and then the Sangam session (RP-initiated).
app.MapGet("/signout", (HttpContext context) =>
{
    context.Response.Headers["Clear-Site-Data"] = SecurityHeaders.ClearSiteData;
    return Results.SignOut(
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
})
    .AllowAnonymous();

// PR-26: verifying the name, date of birth and gender through DigiLocker.
app.MapDigiLocker();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode(o =>
{
    // R7: Blazor would send its own Content-Security-Policy (frame-ancestors 'self') on every page, in place of the
    // full policy the security headers set; that one already forbids all framing.
    o.ContentSecurityFrameAncestorsPolicy = null;
});

app.MapSangamLanguageSwitch();
app.MapSangamHealth();

await app.RunAsync().ConfigureAwait(false);
