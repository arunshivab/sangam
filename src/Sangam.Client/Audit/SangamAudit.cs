using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sangam.Client.Audit;

/// <summary>
/// Builds and checks events in the shared audit schema 1.0 (SGM-208): the same rules as
/// <c>sdk/schema/audit-event-1.0.schema.json</c> and every other Sangam SDK, proved by the shared conformance vectors.
/// </summary>
public static partial class SangamAudit
{
    /// <summary>The schema version this helper writes.</summary>
    public const string SchemaVersion = "1.0";

    /// <summary>What a masked value becomes.</summary>
    public const string Masked = "[masked]";

    private static readonly string[] Environments = ["production", "staging", "development"];
    private static readonly string[] ActorTypes = ["user", "admin", "api", "system", "anonymous"];
    private static readonly string[] Categories = ["access", "create", "update", "delete", "approve", "sign", "export", "admin", "security"];
    private static readonly string[] Outcomes = ["success", "failure", "denied"];
    private static readonly string[] Classifications = ["public", "internal", "personal", "sensitive-personal", "health"];
    private static readonly string[] ServiceFields = ["recorded_at", "prev_hash", "hash"];

    /// <summary>Builds an event: source from the options, actor from the signed-in principal, client from the request.</summary>
    /// <param name="options">The application's audit settings.</param>
    /// <param name="user">The signed-in principal, if any.</param>
    /// <param name="ipAddress">The request's client address.</param>
    /// <param name="userAgent">The request's browser string.</param>
    /// <param name="entry">What happened.</param>
    /// <param name="now">When; the clock when omitted.</param>
    public static JsonObject Build(SangamAuditOptions options, ClaimsPrincipal? user, string? ipAddress, string? userAgent, SangamAuditEntry entry, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(entry);
        JsonObject actor = new();
        string? sub = user?.Identity?.IsAuthenticated == true ? user.FindFirst("sub")?.Value : null;
        string type = entry.ActorType ?? (sub is null ? "anonymous" : "user");
        actor["type"] = type;
        if (sub is not null && type is "user" or "admin")
        {
            actor["sub"] = sub;
            Put(actor, "session_id", user!.FindFirst("sid")?.Value);
            Put(actor, "acr", user.FindFirst("acr")?.Value);
            string[] amr = [.. user.FindAll("amr").Select(c => c.Value)];
            if (amr.Length > 0)
            {
                actor["amr"] = new JsonArray([.. amr.Select(a => (JsonNode)a)]);
            }
        }

        Put(actor, "client_id", entry.ClientId);
        JsonObject e = new()
        {
            ["schema_version"] = SchemaVersion,
            ["event_id"] = Guid.CreateVersion7(now ?? DateTimeOffset.UtcNow).ToString("D"),
            ["occurred_at"] = (now ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["source"] = new JsonObject { ["app_id"] = options.AppId, ["app_version"] = options.AppVersion, ["environment"] = options.Environment },
        };
        if (entry.OrganisationId is Guid org)
        {
            JsonObject tenant = new() { ["org_id"] = org.ToString("D") };
            Put(tenant, "org_path", entry.OrganisationPath);
            e["tenant"] = tenant;
        }

        e["actor"] = actor;
        e["action"] = entry.Action;
        e["category"] = entry.Category;
        JsonObject target = new() { ["type"] = entry.TargetType, ["id"] = entry.TargetId };
        Put(target, "display", entry.TargetDisplay);
        e["target"] = target;
        e["outcome"] = entry.Outcome;
        Put(e, "reason", entry.Reason);
        if (entry.Signature is (string tokenId, string meaning, string hash))
        {
            e["signature"] = new JsonObject { ["token_id"] = tokenId, ["meaning"] = meaning, ["record_hash"] = hash };
        }

        if (entry.Changes.Count > 0)
        {
            e["changes"] = new JsonArray([.. entry.Changes.Select(c => (JsonNode)new JsonObject
            {
                ["field"] = c.Field,
                ["before"] = entry.Sensitive.Contains(c.Field) ? Masked : JsonValue.Create(c.Before),
                ["after"] = entry.Sensitive.Contains(c.Field) ? Masked : JsonValue.Create(c.After),
            })]);
        }

        if (!string.IsNullOrEmpty(ipAddress))
        {
            JsonObject client = new() { ["ip"] = ipAddress };
            Put(client, "user_agent", userAgent);
            e["client"] = client;
        }

        Put(e, "correlation_id", entry.CorrelationId);
        e["data_classification"] = entry.DataClassification;
        return e;
    }

    /// <summary>Checks an event against schema 1.0; returns the problems, none when it is valid.</summary>
    /// <param name="e">The event.</param>
    public static IReadOnlyList<string> Validate(JsonNode? e)
    {
        List<string> problems = [];
        if (e is not JsonObject o)
        {
            return ["not an object"];
        }

        if (Text(o, "schema_version") is not { } version || !SchemaVersionRegex().IsMatch(version))
        {
            problems.Add("schema_version must be 1.x");
        }

        if (Text(o, "event_id") is not { } id || !UuidV7Regex().IsMatch(id))
        {
            problems.Add("event_id must be a UUID version 7");
        }

        if (Text(o, "occurred_at") is not { } at || !Rfc3339Regex().IsMatch(at))
        {
            problems.Add("occurred_at must be RFC 3339");
        }

        if (o["source"] is not JsonObject source || string.IsNullOrEmpty(Text(source, "app_id")) || string.IsNullOrEmpty(Text(source, "app_version")) || !Environments.Contains(Text(source, "environment")))
        {
            problems.Add("source needs app_id, app_version and an environment of production, staging or development");
        }

        if (o["tenant"] is JsonNode tenantNode && (tenantNode is not JsonObject tenant || Text(tenant, "org_id") is not { } orgId || !UuidRegex().IsMatch(orgId)
            || (tenant["org_path"] is not null && (Text(tenant, "org_path") is not { } path || !path.StartsWith('/') || !path.EndsWith('/')))))
        {
            problems.Add("tenant needs a UUID org_id and an org_path that starts and ends with /");
        }

        string? actorType = null;
        if (o["actor"] is not JsonObject actor || !ActorTypes.Contains(actorType = Text(actor, "type")))
        {
            problems.Add("actor.type must be user, admin, api, system or anonymous");
        }
        else
        {
            string? sub = Text(actor, "sub");
            if (actorType is "user" or "admin" && string.IsNullOrEmpty(sub))
            {
                problems.Add("a person's event needs actor.sub");
            }

            if (sub is not null && sub.Contains('@', StringComparison.Ordinal))
            {
                problems.Add("actor.sub is the Sangam subject id, never an e-mail address");
            }

            if (actorType == "api" && string.IsNullOrEmpty(Text(actor, "client_id")))
            {
                problems.Add("an api event needs actor.client_id");
            }
        }

        if (Text(o, "action") is not { } action || !ActionRegex().IsMatch(action))
        {
            problems.Add("action must be domain.object.verb in lowercase");
        }

        string? category = Text(o, "category");
        if (!Categories.Contains(category))
        {
            problems.Add("category is not one of the nine");
        }

        if (o["target"] is not JsonObject target || string.IsNullOrEmpty(Text(target, "type")) || string.IsNullOrEmpty(Text(target, "id")))
        {
            problems.Add("target needs type and id");
        }

        if (!Outcomes.Contains(Text(o, "outcome")))
        {
            problems.Add("outcome must be success, failure or denied");
        }

        if (category == "sign" && (o["signature"] is not JsonObject signature || string.IsNullOrEmpty(Text(signature, "token_id")) || string.IsNullOrEmpty(Text(signature, "meaning")) || string.IsNullOrEmpty(Text(signature, "record_hash"))))
        {
            problems.Add("a sign event needs signature.token_id, meaning and record_hash");
        }

        if (o["changes"] is JsonNode changes && (changes is not JsonArray list || list.Any(c => c is not JsonObject change || string.IsNullOrEmpty(Text(change, "field")))))
        {
            problems.Add("each change needs its field");
        }

        if (actorType is "user" or "admin" && (o["client"] is not JsonObject client || string.IsNullOrEmpty(Text(client, "ip"))))
        {
            problems.Add("a person's event needs client.ip");
        }

        if (!Classifications.Contains(Text(o, "data_classification")))
        {
            problems.Add("data_classification is not one of the five");
        }

        foreach (string field in ServiceFields.Where(o.ContainsKey))
        {
            problems.Add(field + " is set by the audit service, never by the application");
        }

        return problems;
    }

    private static void Put(JsonObject o, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            o[name] = value;
        }
    }

    private static string? Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    [GeneratedRegex(@"^1\.[0-9]+$")]
    private static partial Regex SchemaVersionRegex();

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")]
    private static partial Regex UuidV7Regex();

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex UuidRegex();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex Rfc3339Regex();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*){2,}$")]
    private static partial Regex ActionRegex();
}
