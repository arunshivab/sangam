using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Sangam.Identity.Infrastructure.Tests.Provisioning;

/// <summary>
/// A small, strict SCIM 2.0 server (RFC 7643/7644) on a real port, for the provisioning tests: users and groups in
/// memory, <c>externalId</c> and <c>active</c> filters, PATCH add/remove/replace, the bearer token checked on every call,
/// the SCIM media type required, and failures on demand. Every request is recorded.
/// </summary>
internal sealed class ReferenceScimServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _failNext;
    private int _failStatus;

    private ReferenceScimServer(WebApplication app)
    {
        _app = app;
    }

    public ConcurrentDictionary<string, JsonObject> Users { get; } = new();

    public ConcurrentDictionary<string, JsonObject> Groups { get; } = new();

    public ConcurrentQueue<string> Requests { get; } = new();

    public string Token { get; set; } = "scim-test-token";

    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>Whether a valid bearer token must be Sangam's own (any non-empty token is accepted with a validator).</summary>
    public Func<string, bool>? ValidateToken { get; set; }

    public static async Task<ReferenceScimServer> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        ReferenceScimServer server = new(app);
        server.Map(app);
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.BaseUrl = address.TrimEnd('/') + "/scim/v2";
        return server;
    }

    public void FailNext(int count, HttpStatusCode status)
    {
        _failStatus = (int)status;
        Interlocked.Exchange(ref _failNext, count);
    }

    public JsonObject? UserByExternalId(Guid id) => Users.Values.FirstOrDefault(u => u["externalId"]?.GetValue<string>() == id.ToString("D"));

    public IEnumerable<string> MembersOf(string displayName)
        => Groups.Values.Where(g => g["displayName"]!.GetValue<string>() == displayName).SelectMany(g => g["members"]!.AsArray().Select(m => m!["value"]!.GetValue<string>()));

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private void Map(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            Requests.Enqueue(context.Request.Method + " " + context.Request.Path + context.Request.QueryString);
            string auth = context.Request.Headers.Authorization.ToString();
            string token = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : string.Empty;
            bool authorised = ValidateToken is not null ? ValidateToken(token) : token == Token;
            if (!authorised)
            {
                context.Response.StatusCode = 401;
                return;
            }

            if (context.Request.ContentLength > 0 && context.Request.ContentType?.StartsWith("application/scim+json", StringComparison.Ordinal) != true)
            {
                context.Response.StatusCode = 415;
                return;
            }

            if (Interlocked.Decrement(ref _failNext) >= 0)
            {
                context.Response.StatusCode = _failStatus;
                await context.Response.WriteAsync("{\"detail\":\"injected failure\"}");
                return;
            }

            await next();
        });

        app.MapGet("/scim/v2/ServiceProviderConfig", () => Scim(new JsonObject { ["patch"] = new JsonObject { ["supported"] = true }, ["filter"] = new JsonObject { ["supported"] = true } }));

        app.MapGet("/scim/v2/Users", (HttpContext context) =>
        {
            string filter = context.Request.Query["filter"].ToString();
            IEnumerable<JsonObject> found = Users.Values;
            if (filter.StartsWith("externalId eq ", StringComparison.Ordinal))
            {
                string value = filter["externalId eq ".Length..].Trim('"');
                found = found.Where(u => u["externalId"]?.GetValue<string>() == value);
            }
            else if (filter == "active eq true")
            {
                found = found.Where(u => u["active"]?.GetValue<bool>() == true);
            }

            List<JsonObject> list = [.. found];
            bool countOnly = context.Request.Query["count"].ToString() == "0";
            return Scim(new JsonObject
            {
                ["schemas"] = new JsonArray("urn:ietf:params:scim:api:messages:2.0:ListResponse"),
                ["totalResults"] = list.Count,
                ["Resources"] = new JsonArray([.. (countOnly ? [] : list).Select(u => (JsonNode)u.DeepClone())]),
            });
        });

        app.MapPost("/scim/v2/Users", async (HttpContext context) =>
        {
            JsonObject user = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            if (Users.Values.Any(u => u["userName"]?.GetValue<string>() == user["userName"]?.GetValue<string>()))
            {
                return Results.Conflict();
            }

            string id = Guid.NewGuid().ToString("N");
            user["id"] = id;
            Users[id] = user;
            return Scim(user, 201);
        });

        app.MapMethods("/scim/v2/Users/{id}", ["PATCH"], async (string id, HttpContext context) =>
        {
            if (!Users.TryGetValue(id, out JsonObject? user))
            {
                return Results.NotFound();
            }

            JsonObject patch = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            foreach (JsonNode? op in patch["Operations"]!.AsArray())
            {
                if (op!["op"]!.GetValue<string>() == "replace" && op["path"] is null)
                {
                    foreach ((string key, JsonNode? value) in op["value"]!.AsObject())
                    {
                        user[key] = value?.DeepClone();
                    }
                }
            }

            return Scim(user);
        });

        app.MapDelete("/scim/v2/Users/{id}", (string id) => Users.TryRemove(id, out _) ? Results.NoContent() : Results.NotFound());

        app.MapPost("/scim/v2/Groups", async (HttpContext context) =>
        {
            JsonObject group = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            string id = Guid.NewGuid().ToString("N");
            group["id"] = id;
            group["members"] ??= new JsonArray();
            Groups[id] = group;
            return Scim(group, 201);
        });

        app.MapMethods("/scim/v2/Groups/{id}", ["PATCH"], async (string id, HttpContext context) =>
        {
            if (!Groups.TryGetValue(id, out JsonObject? group))
            {
                return Results.NotFound();
            }

            JsonArray members = group["members"]!.AsArray();
            JsonObject patch = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            foreach (JsonNode? op in patch["Operations"]!.AsArray())
            {
                string kind = op!["op"]!.GetValue<string>();
                string path = op["path"]?.GetValue<string>() ?? string.Empty;
                if (kind == "add" && path == "members")
                {
                    foreach (JsonNode? m in op["value"]!.AsArray())
                    {
                        if (!members.Any(x => x!["value"]!.GetValue<string>() == m!["value"]!.GetValue<string>()))
                        {
                            members.Add(m!.DeepClone());
                        }
                    }
                }
                else if (kind == "remove" && path.StartsWith("members[value eq ", StringComparison.Ordinal))
                {
                    string value = path["members[value eq ".Length..].TrimEnd(']').Trim('"');
                    foreach (JsonNode? m in members.Where(x => x!["value"]!.GetValue<string>() == value).ToList())
                    {
                        members.Remove(m);
                    }
                }
                else if (kind == "replace" && path == "displayName")
                {
                    group["displayName"] = op["value"]!.DeepClone();
                }
            }

            return Results.NoContent();
        });
    }

    private static IResult Scim(JsonObject body, int status = 200) => Results.Text(body.ToJsonString(), "application/scim+json", statusCode: status);
}
