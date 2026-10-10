using Imagiqa.Web.Components;
using Imagiqa.Web.Components.Ward;
using Imagiqa.Web.Demo;
using Imagiqa.Web.Records;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Sangam.Client;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// D-I: at demo.sangamid.in the client secret and the database password arrive as Docker secret files.
string secrets = Environment.GetEnvironmentVariable("SANGAM_SECRETS_DIR") ?? "/run/secrets";
if (Directory.Exists(secrets))
{
    builder.Configuration.AddKeyPerFile(secrets, optional: true);
}

// Behind Caddy: the client address and https come from the proxy, trusted on its network only.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    foreach (string network in (builder.Configuration["Imagiqa:ForwardedHeaders:KnownNetworks"] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
});

// The sign-in cookie survives a restart: keys kept on a volume (a demo with no real patient data; see the README).
if (builder.Configuration["Imagiqa:DataProtection:KeysDirectory"] is { Length: > 0 } keys)
{
    builder.Services.AddDataProtection().SetApplicationName("imagiqa").PersistKeysToFileSystem(new DirectoryInfo(keys));
}

builder.Services.AddHealthChecks();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContextFactory<ImagiqaDbContext>(o => o
    .UseNpgsql(builder.Configuration.GetConnectionString("Imagiqa"))
    .UseSnakeCaseNamingConvention());
builder.Services.AddScoped<PatientRecords>();
builder.Services.AddScoped<WardState>();
builder.Services.AddHttpClient<DemoHospital>();

// Everything imagiQa knows about who someone is comes from this one call.
builder.Services.AddSangam(o =>
{
    o.Authority = builder.Configuration["Sangam:Authority"] ?? string.Empty;
    o.ClientId = builder.Configuration["Sangam:ClientId"] ?? string.Empty;
    o.ClientSecret = builder.Configuration["Sangam:ClientSecret"];
    // rc.5: no cookie name set, so the SDK uses __Host-sangam.app on HTTPS (ASVS V3.4.4).
    o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
});
builder.Services.AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy);

WebApplication app = builder.Build();

app.UseForwardedHeaders();

// R7 (SGM-503): the same security headers as Sangam's own hosts. Scripts only from this origin; forms may lead here
// and to Sangam (joining the demo hospital signs in again); no page may be framed.
string sangamOrigin = Uri.TryCreate(app.Configuration["Sangam:Authority"], UriKind.Absolute, out Uri? authorityUri) ? authorityUri.GetLeftPart(UriPartial.Authority) : string.Empty;
string policy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; "
    + "connect-src 'self'; frame-src 'none'; frame-ancestors 'none'; form-action 'self' " + sangamOrigin + "; base-uri 'self'; object-src 'none'";
app.Use((context, next) =>
{
    context.Response.OnStarting(() =>
    {
        IHeaderDictionary headers = context.Response.Headers;
        headers["Content-Security-Policy"] = policy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        return Task.CompletedTask;
    });
    return next(context);
});

// Development, and the demo (its own database, no real patient data), create their tables on start.
if (app.Configuration.GetValue("Imagiqa:MigrateOnStartup", false))
{
    using IServiceScope scope = app.Services.CreateScope();
    using ImagiqaDbContext db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ImagiqaDbContext>>().CreateDbContext();
    await db.Database.MigrateAsync().ConfigureAwait(false);
}

if (!app.Environment.IsDevelopment())
{
    // A plain answer, with no page behind the sign-in that could itself fail: the usual cause is that Sangam cannot be
    // reached to sign someone in.
    app.UseExceptionHandler(error => error.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("imagiQa could not complete this request. If you were signing in, Sangam may be unreachable: try again in a few minutes.").ConfigureAwait(false);
    }));
    app.UseHsts();
}

// Behind Caddy (a known proxy network), Caddy redirects to https itself and health-checks over plain http, as for
// Sangam's own hosts (V-07); a host exposed directly still redirects.
bool behindProxy = !string.IsNullOrWhiteSpace(app.Configuration["Imagiqa:ForwardedHeaders:KnownNetworks"]);
if (!behindProxy && !app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();
app.MapSangamSignOut();

// V-15: on the demo, a tester with no role joins the made-up Demo Hospital, then signs in again for the new role.
app.MapPost("/demo/join", async (HttpContext context, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery, DemoHospital demo, CancellationToken cancellationToken) =>
{
    if (!await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false))
    {
        return Results.BadRequest();
    }

    IFormCollection form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
    string role = form["role"].ToString();
    Guid? userId = context.User.GetSangamUser()?.Id;
    string? problem = userId is null ? "Not signed in." : await demo.JoinAsync(userId.Value, role, cancellationToken).ConfigureAwait(false);
    if (problem is not null)
    {
        return Results.Redirect("/?demo=failed");
    }

    await context.SignOutAsync(SangamDefaults.CookieScheme).ConfigureAwait(false);
    return Results.Challenge(new AuthenticationProperties { RedirectUri = "/" });
}).RequireAuthorization();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(o =>
{
    // R7: Blazor would send its own Content-Security-Policy (frame-ancestors 'self') on every page, in place of the
    // full policy the security headers set; that one already forbids all framing.
    o.ContentSecurityFrameAncestorsPolicy = null;
});

await app.RunAsync().ConfigureAwait(false);

/// <summary>Entry point; public so the tests can host the application.</summary>
public partial class Program
{
}
