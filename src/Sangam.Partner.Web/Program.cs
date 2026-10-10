using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Sangam.Identity.Application;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Application.Evidence;
using Sangam.Identity.Infrastructure;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Partner.Web;
using Sangam.Partner.Web.Components;
using Sangam.Partner.Web.Components.Common;
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

string authority = builder.Configuration["Sangam:Authority"] ?? "https://id.sangamid.in";

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(o =>
    {
        o.Cookie.Name = SecurityHeaders.CookieName("sangam.partner", builder.Environment.EnvironmentName);
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = SecurityHeaders.CookiePolicy(builder.Environment.EnvironmentName);
        // As short as the operator console's: this session can change who holds clinical roles.
        o.ExpireTimeSpan = TimeSpan.FromHours(2);
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = SessionLifetime.ValidateAsync;
    })
    .AddOpenIdConnect(o =>
    {
        o.Events.OnTicketReceived = SessionLifetime.StampAsync;
        o.Authority = authority;
        o.ClientId = builder.Configuration["Sangam:Partner:ClientId"] ?? DevelopmentSeeder.PartnerClientId;
        o.ClientSecret = builder.Configuration["Sangam:Partner:ClientSecret"] ?? string.Empty;
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

// R7 (ASVS V11.1.4): an evidence pack reads a whole year of records; each administrator may build a few at a time.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(EvidenceLimit.PolicyName, context => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        SignedInUser.Id(context.User)?.ToString("D") ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = EvidenceLimit.PerWindow, Window = EvidenceLimit.Window, QueueLimit = 0 }));
});

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
app.UseRateLimiter();
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

// PR-32 (CAP-110): an application's evidence pack, as a download. The form carries the antiforgery token (checked by
// the framework for form-bound endpoints); the console's own gate is repeated here: signed in, an authenticator
// enrolled, and administering the application (the service checks the rank).
app.MapPost("/apps/{appId:guid}/evidence", async (Guid appId, [Microsoft.AspNetCore.Mvc.FromForm] string from, [Microsoft.AspNetCore.Mvc.FromForm] string to, HttpContext context, IMfaService mfa, IEvidencePackService evidence, CancellationToken cancellationToken) =>
{
    if (SignedInUser.Id(context.User) is not Guid userId)
    {
        return Results.Unauthorized();
    }

    if (!await mfa.IsEnrolledAsync(userId, cancellationToken).ConfigureAwait(false))
    {
        return Results.Forbid();
    }

    if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateOnly start)
        || !DateOnly.TryParseExact(to, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateOnly end)
        || end < start || end.DayNumber - start.DayNumber >= Sangam.Identity.Infrastructure.Evidence.EfEvidencePackService.MaxDays)
    {
        return Results.BadRequest();
    }

    EvidencePack? pack = await evidence.BuildAsync(userId, appId, start, end, cancellationToken).ConfigureAwait(false);
    return pack is null ? Results.NotFound() : Results.File(pack.Content, "application/zip", pack.FileName);
}).RequireRateLimiting(EvidenceLimit.PolicyName);

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
