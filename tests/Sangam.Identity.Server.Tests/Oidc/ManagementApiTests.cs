using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Infrastructure.Seeding;

namespace Sangam.Identity.Server.Tests.Oidc;

/// <summary>The management API over HTTP: authorised by a client-credentials token, scoped to the calling app.</summary>
[Collection("server")]
public sealed class ManagementApiTests
{
    private static readonly string[] NursePermissions = ["patient:read", "vitals:write"];
    private readonly SangamServerFactory _factory;

    public ManagementApiTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task WithoutAScopedToken_TheApiIsClosed()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage anonymous = await client.GetAsync(new Uri("/api/v1/roles", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // A token without sangam.manage is authenticated but not authorised.
        string plain = await ClientTokenAsync(client, scope: "orgs.read");
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/roles");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plain);
        using HttpResponseMessage forbidden = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // R7 (ASVS V7.2.2): the refusal is in the audit log, with the client and the path; the anonymous call is not.
        using IServiceScope scope = _factory.Services.CreateScope();
        Infrastructure.Persistence.SangamDbContext db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.SangamDbContext>();
        Domain.Entities.AuditEvent denied = await db.AuditEvents.AsNoTracking().OrderByDescending(e => e.Id).FirstAsync(e => e.Action == Domain.AuditActions.AccessDenied);
        using JsonDocument detail = JsonDocument.Parse(denied.Metadata);
        Assert.Equal("/api/v1/roles", detail.RootElement.GetProperty("path").GetString());
        Assert.Equal(DevelopmentSeeder.SampleClientId, detail.RootElement.GetProperty("client_id").GetString());
    }

    [PostgresFact]
    public async Task AWrongClientSecret_IsRefused_AndAudited_WithoutTheSecret()
    {
        // R7 (ASVS V7.2.1): client authentication failures inside OpenIddict reach the audit log.
        using HttpClient client = _factory.CreateClient();
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_secret"] = "not-the-secret-r7",
            ["scope"] = "sangam.manage",
        });
        using HttpResponseMessage response = await client.PostAsync(new Uri("/connect/token", UriKind.Relative), form);
        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains("invalid_client", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using IServiceScope scope = _factory.Services.CreateScope();
        Infrastructure.Persistence.SangamDbContext db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.SangamDbContext>();
        Domain.Entities.AuditEvent refused = await db.AuditEvents.AsNoTracking().OrderByDescending(e => e.Id).FirstAsync(e => e.Action == Domain.AuditActions.TokenRefused);
        using JsonDocument detail = JsonDocument.Parse(refused.Metadata);
        Assert.Equal("invalid_client", detail.RootElement.GetProperty("error").GetString());
        Assert.Equal(DevelopmentSeeder.SampleClientId, detail.RootElement.GetProperty("client_id").GetString());
        Assert.DoesNotContain("not-the-secret-r7", refused.Metadata, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Roles_Organisations_And_Memberships_RoundTrip()
    {
        using HttpClient client = _factory.CreateClient();
        string token = await ClientTokenAsync(client, scope: "sangam.manage");
        Guid orgId = Guid.NewGuid();
        Guid deptId = Guid.NewGuid();

        using HttpResponseMessage role = await SendAsync(client, token, HttpMethod.Put, "/api/v1/roles/nurse", new
        {
            displayName = "Nurse",
            description = "Ward nursing",
            permissions = NursePermissions,
            orgId = (Guid?)null,
        });
        Assert.Equal(HttpStatusCode.OK, role.StatusCode);

        using HttpResponseMessage org = await SendAsync(client, token, HttpMethod.Put, $"/api/v1/orgs/{orgId:D}", new
        {
            name = "Apulki Medical Center",
            type = "hospital",
            parentId = (Guid?)null,
            metadata = "{\"city\":\"Pune\"}",
        });
        Assert.Equal(HttpStatusCode.OK, org.StatusCode);

        using HttpResponseMessage dept = await SendAsync(client, token, HttpMethod.Put, $"/api/v1/orgs/{deptId:D}", new
        {
            name = "Oncology",
            type = "department",
            parentId = orgId,
            metadata = (string?)null,
        });
        JsonElement deptBody = JsonDocument.Parse(await dept.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(HttpStatusCode.OK, dept.StatusCode);
        Assert.Equal(1, deptBody.GetProperty("depth").GetInt32());

        using HttpResponseMessage rootDept = await SendAsync(client, token, HttpMethod.Put, $"/api/v1/orgs/{Guid.NewGuid():D}", new
        {
            name = "Loose department",
            type = "department",
            parentId = (Guid?)null,
            metadata = (string?)null,
        });
        Assert.Equal(HttpStatusCode.BadRequest, rootDept.StatusCode);

        using HttpResponseMessage ghost = await SendAsync(client, token, HttpMethod.Put, $"/api/v1/orgs/{orgId:D}/members/{Guid.NewGuid():D}", new { role = "nurse", appliesToDescendants = false });
        Assert.Equal(HttpStatusCode.NotFound, ghost.StatusCode);

        using HttpRequestMessage list = new(HttpMethod.Get, "/api/v1/roles");
        list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage listed = await client.SendAsync(list);
        JsonElement[] roles = [.. JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement.EnumerateArray()];
        Assert.Contains(roles, r => r.GetProperty("code").GetString() == "nurse");
        Assert.Contains(roles, r => r.GetProperty("code").GetString() == "org_admin" && r.GetProperty("isSystem").GetBoolean());

        using HttpResponseMessage systemRole = await SendAsync(client, token, HttpMethod.Delete, "/api/v1/roles/org_admin", null);
        Assert.Equal(HttpStatusCode.BadRequest, systemRole.StatusCode);

        using HttpResponseMessage retired = await SendAsync(client, token, HttpMethod.Delete, "/api/v1/roles/nurse", null);
        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);
    }

    [PostgresFact]
    public async Task TheDemo_CanPlaceATesterAtItsMadeUpHospital_AsAnyApplicationWould()
    {
        // V-15: the same calls imagiQa's DemoHospital makes, with imagiQa's own development client.
        using HttpClient client = _factory.CreateClient();
        string token = await ClientTokenAsync(client, scope: "sangam.manage", DevelopmentSeeder.ImagiqaClientId, DevelopmentSeeder.ImagiqaClientSecret);
        Guid hospital = Guid.NewGuid();
        Guid tester;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            Infrastructure.Persistence.SangamDbContext db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.SangamDbContext>();
            tester = await db.Users.OrderBy(u => u.CreatedAt).Select(u => u.Id).FirstAsync();

            // rc.5 (consent first): the tester has signed in to the demo, as DemoHospital's caller always has.
            Guid demo = await db.Apps.Where(a => a.ClientId == DevelopmentSeeder.ImagiqaClientId).Select(a => a.Id).SingleAsync();
            if (!await db.AppGrants.AnyAsync(g => g.AppId == demo && g.UserId == tester && g.RevokedAt == null))
            {
                db.AppGrants.Add(new Domain.Entities.AppGrant { Id = Guid.NewGuid(), AppId = demo, UserId = tester, GrantedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }
        }

        using HttpResponseMessage role = await SendAsync(client, token, HttpMethod.Put, "/api/v1/roles/doctor", new { displayName = "Doctor", description = "Demo", permissions = NursePermissions, orgId = (Guid?)null });
        Assert.Equal(HttpStatusCode.OK, role.StatusCode);
        using HttpResponseMessage org = await SendAsync(client, token, HttpMethod.Put, $"/api/v1/orgs/{hospital:D}", new { name = "Demo Hospital (made up)", type = "hospital", parentId = (Guid?)null, metadata = "{\"demo\":true}" });
        Assert.Equal(HttpStatusCode.OK, org.StatusCode);
        using HttpResponseMessage member = await SendAsync(client, token, HttpMethod.Put, $"/api/v1/orgs/{hospital:D}/members/{tester:D}", new { role = "doctor", appliesToDescendants = false });
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
        using HttpResponseMessage again = await SendAsync(client, token, HttpMethod.Put, $"/api/v1/orgs/{hospital:D}/members/{tester:D}", new { role = "doctor", appliesToDescendants = false });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [PostgresFact]
    public async Task APersonWhoDoesNotUseTheApplication_IsInvited_NotAdded()
    {
        // rc.5 (ASVS V4.2.1, consent first).
        using HttpClient client = _factory.CreateClient();
        string token = await ClientTokenAsync(client, scope: "sangam.manage");
        Guid orgId = Guid.NewGuid();
        Guid stranger;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            Infrastructure.Persistence.SangamDbContext db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.SangamDbContext>();
            Guid appId = await db.Apps.Where(a => a.ClientId == DevelopmentSeeder.SampleClientId).Select(a => a.Id).SingleAsync();
            List<Guid> linked = await db.AppGrants.Where(g => g.AppId == appId).Select(g => g.UserId).ToListAsync();
            stranger = await db.Users.Where(u => !linked.Contains(u.Id)).Select(u => u.Id).FirstAsync();
        }

        using HttpResponseMessage role = await SendAsync(client, token, HttpMethod.Put, "/api/v1/roles/invitee", new { displayName = "Invitee", description = "rc.5", permissions = NursePermissions, orgId = (Guid?)null });
        Assert.Equal(HttpStatusCode.OK, role.StatusCode);
        using HttpResponseMessage org = await SendAsync(client, token, HttpMethod.Put, $"/api/v1/orgs/{orgId:D}", new { name = "Invitation Clinic", type = "hospital", parentId = (Guid?)null, metadata = (string?)null });
        Assert.Equal(HttpStatusCode.OK, org.StatusCode);

        using HttpResponseMessage added = await SendAsync(client, token, HttpMethod.Put, $"/api/v1/orgs/{orgId:D}/members/{stranger:D}", new { role = "invitee", appliesToDescendants = false });
        Assert.Equal(HttpStatusCode.NotFound, added.StatusCode);
        Assert.Contains("Invite them instead", await added.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        string email = $"invitee-{Guid.NewGuid():N}@example.in";
        using HttpResponseMessage invited = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/orgs/{orgId:D}/invitations", new { email, role = "invitee", appliesToDescendants = false });
        string body = await invited.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, invited.StatusCode);
        Assert.Equal(email, JsonDocument.Parse(body).RootElement.GetProperty("email").GetString());
        Assert.NotNull(_factory.Services.GetRequiredService<Infrastructure.Services.InMemoryEmailOutbox>().LatestFor(email));

        using HttpResponseMessage badRole = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/orgs/{orgId:D}/invitations", new { email, role = "ghost", appliesToDescendants = false });
        Assert.Equal(HttpStatusCode.NotFound, badRole.StatusCode);
        using HttpResponseMessage badEmail = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/orgs/{orgId:D}/invitations", new { email = "not an address", role = "invitee", appliesToDescendants = false });
        Assert.Equal(HttpStatusCode.BadRequest, badEmail.StatusCode);
    }

    private static async Task<string> ClientTokenAsync(HttpClient client, string scope, string clientId = DevelopmentSeeder.SampleClientId, string clientSecret = DevelopmentSeeder.SampleClientSecret)
    {
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = scope,
        });
        using HttpResponseMessage response = await client.PostAsync(new Uri("/connect/token", UriKind.Relative), form);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, object? body)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }
}
