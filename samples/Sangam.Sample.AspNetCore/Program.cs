using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Sangam.Client;
using Sangam.Client.Audit;
using Sangam.Shared.Constants;

// Sample settings: the development Sangam on localhost:5100 and its sample application (see sdk/README.md).
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string authority = builder.Configuration["Sangam:Authority"] ?? "http://localhost:5100";
builder.Services.AddSangam(o =>
{
    o.Authority = authority;
    o.ClientId = builder.Configuration["Sangam:ClientId"] ?? "sangam-dev-sample";
    o.ClientSecret = builder.Configuration["Sangam:ClientSecret"] ?? "sangam-dev-sample-secret-change-me";
    o.CallbackPath = "/auth/callback";
    o.RequireHttpsMetadata = !authority.StartsWith("http://localhost", StringComparison.Ordinal);
});
builder.Services.AddSangamAudit(o =>
{
    o.AppId = "sangam-dev-sample";
    o.AppVersion = "1.0.0-rc.5";
    o.Environment = "development";
    o.BufferPath = builder.Configuration["Sangam:AuditBuffer"] ?? "sangam-audit/pending.jsonl";
});
builder.Services.AddAuthorization();

WebApplication app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

string page = File.ReadAllText(Path.Combine(app.Environment.ContentRootPath, "wwwroot", "page.html"));

// Sign-in, with a step-up requirement when asked for (acr, max_age); only local return addresses are followed.
app.MapGet("/auth/login", (HttpContext context, string? returnTo, string? acr, int? max_age) =>
{
    string back = returnTo is { Length: > 0 } r && r.StartsWith('/') && !r.StartsWith("//", StringComparison.Ordinal) ? r : "/";
    return acr is { Length: > 0 }
        ? SangamStepUp.ChallengeAsync(context, acr, back, max_age is int age ? TimeSpan.FromSeconds(age) : null)
        : context.ChallengeAsync(SangamDefaults.Scheme, new AuthenticationProperties { RedirectUri = back });
});
app.MapSangamSignOut("/auth/logout");

app.MapGet("/", (ClaimsPrincipal principal) => Results.Content(Pages.Home(page, principal), "text/html; charset=utf-8"));

app.MapGet("/api/check", (ClaimsPrincipal principal, string org, string permission) =>
    principal.Identity?.IsAuthenticated == true
        ? Results.Json(new { allowed = principal.GetSangamUser()!.HasPermission(permission, org) })
        : Results.Json(new { error = "sign_in_required", login = "/auth/login?returnTo=%2F" }, statusCode: 401));

// Signing needs a two-factor sign-in from the last five minutes; a fetch is told where to step up.
app.MapPost("/api/records/{id}/sign", async (HttpContext context, ClaimsPrincipal principal, ISangamAudit audit, string id) =>
{
    // A header a cross-site form cannot send: with the SameSite=Lax cookie, enough against request forgery here.
    if (context.Request.Headers["X-Requested-With"] != "fetch")
    {
        return Results.BadRequest();
    }

    if (!principal.Satisfies(SangamAcr.Signature))
    {
        return Results.Json(new { error = "step_up_required", acr = SangamAcr.Signature, login = "/auth/login?returnTo=%2F%3Fresume%3Dsign&acr=" + WebUtility.UrlEncode(SangamAcr.Signature) }, statusCode: 401);
    }

    SangamUser user = principal.GetSangamUser()!;
    SangamMembership? membership = user.Memberships.Count > 0 ? user.Memberships[0] : null;
    JsonObjectResult result = new(id, await audit.RecordAsync(new SangamAuditEntry("sample.record.sign", "sign", "record", id)
    {
        TargetDisplay = "Record " + id,
        Signature = ("sig-" + id, "Approved", "sha256:" + new string('0', 64)),
        OrganisationId = membership?.OrganisationId,
        OrganisationPath = membership?.Path,
    }, context.RequestAborted).ConfigureAwait(false));
    return Results.Content("{\"signed\":" + System.Text.Json.JsonSerializer.Serialize(result.Id) + ",\"event\":" + result.Event.ToJsonString() + "}", "application/json");
});

app.Run();

/// <summary>The record signed and its audit event.</summary>
/// <param name="Id">The record.</param>
/// <param name="Event">The shared audit event.</param>
internal sealed record JsonObjectResult(string Id, System.Text.Json.Nodes.JsonObject Event);
