using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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

    private static async Task<string> ClientTokenAsync(HttpClient client, string scope)
    {
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_secret"] = DevelopmentSeeder.SampleClientSecret,
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
