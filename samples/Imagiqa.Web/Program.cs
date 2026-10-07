using Imagiqa.Web.Components;
using Imagiqa.Web.Components.Ward;
using Imagiqa.Web.Records;
using Microsoft.EntityFrameworkCore;
using Sangam.Client;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContextFactory<ImagiqaDbContext>(o => o
    .UseNpgsql(builder.Configuration.GetConnectionString("Imagiqa"))
    .UseSnakeCaseNamingConvention());
builder.Services.AddScoped<PatientRecords>();
builder.Services.AddScoped<WardState>();

// Everything imagiQa knows about who someone is comes from this one call.
builder.Services.AddSangam(o =>
{
    o.Authority = builder.Configuration["Sangam:Authority"] ?? string.Empty;
    o.ClientId = builder.Configuration["Sangam:ClientId"] ?? string.Empty;
    o.ClientSecret = builder.Configuration["Sangam:ClientSecret"];
    o.CookieName = "imagiqa.session";
    o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
});
builder.Services.AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy);

WebApplication app = builder.Build();

if (app.Environment.IsDevelopment() && app.Configuration.GetValue("Imagiqa:MigrateOnStartup", false))
{
    using IServiceScope scope = app.Services.CreateScope();
    using ImagiqaDbContext db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ImagiqaDbContext>>().CreateDbContext();
    await db.Database.MigrateAsync().ConfigureAwait(false);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapSangamSignOut();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync().ConfigureAwait(false);

/// <summary>Entry point; public so the tests can host the application.</summary>
public partial class Program
{
}
