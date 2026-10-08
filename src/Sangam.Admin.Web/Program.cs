using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Sangam.Admin.Web.Components;
using Sangam.Identity.Application;
using Sangam.Identity.Infrastructure;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Shared.Constants;
using static OpenIddict.Abstractions.OpenIddictConstants;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddSangamSecretFiles();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSangamApplication();
builder.Services.AddSangamInfrastructure(builder.Configuration);
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
        o.Cookie.Name = "sangam.admin";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        // Shorter than the portal's: a console session left open is a bigger problem.
        o.ExpireTimeSpan = TimeSpan.FromHours(2);
        o.SlidingExpiration = true;
    })
    .AddOpenIdConnect(o =>
    {
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

builder.Services.AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy);

builder.Services.AddSangamWebHosting(builder.Configuration);

WebApplication app = builder.Build();

// Behind Caddy: take the client address from trusted proxies only, before anything reads it (OI-037).
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseSangamClientContext();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapGet("/signout", () =>
    Results.SignOut(
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]))
    .AllowAnonymous();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapSangamHealth();

await app.RunAsync().ConfigureAwait(false);
return 0;
