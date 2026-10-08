using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Siem;

/// <summary>
/// PR-32 (CAP-084): how one audit event is written for a SIEM. Two formats:
/// <list type="bullet">
/// <item><b>cef</b>: an RFC 5424 syslog message (facility 13, log audit) whose text is an ArcSight CEF record, framed by
/// octet counting as RFC 5425 requires over TLS.</item>
/// <item><b>json</b>: one line per event, <c>{"schema":"sangam.siem.1", "id", "hash", "prev_hash", "detail", "event"}</c>,
/// where <c>event</c> is the event in the shared audit schema 1.0 (SGM-208 §3: action <c>identity.&lt;action&gt;</c>), so
/// a SIEM can read Sangam's events and applications' events the same way.</item>
/// </list>
/// Both carry the audit log's own id and its hash chain, so a receiver can drop repeats (delivery is at least once) and
/// check that nothing is missing or changed.
/// </summary>
public static class SiemFormat
{
    /// <summary>The vendor in CEF headers.</summary>
    public const string Vendor = "imagiQa";

    /// <summary>The product in CEF headers, and the syslog APP-NAME.</summary>
    public const string Product = "Sangam";

    /// <summary>The JSON envelope's schema name.</summary>
    public const string EnvelopeSchema = "sangam.siem.1";

    /// <summary>Syslog facility 13: log audit.</summary>
    public const int Facility = 13;

    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Sangam's version, as reported to the SIEM.</summary>
    public static string Version { get; } = (Assembly.GetEntryAssembly() ?? typeof(SiemFormat).Assembly)
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>The bytes to send for one event: framed for the stream.</summary>
    /// <param name="format"><c>cef</c> or <c>json</c>.</param>
    /// <param name="e">The event.</param>
    /// <param name="hostName">This host's name, for the syslog header.</param>
    /// <param name="environmentName">The host environment.</param>
    public static byte[] Frame(string format, AuditEvent e, string hostName, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(e);
        if (format == "json")
        {
            return Utf8.GetBytes(Json(e, environmentName).ToJsonString() + "\n");
        }

        byte[] message = Utf8.GetBytes(Syslog(e, hostName));
        byte[] prefix = Utf8.GetBytes(message.Length.ToString(CultureInfo.InvariantCulture) + " ");
        return [.. prefix, .. message];
    }

    /// <summary>The RFC 5424 syslog message whose text is the CEF record.</summary>
    /// <param name="e">The event.</param>
    /// <param name="hostName">This host's name.</param>
    public static string Syslog(AuditEvent e, string hostName)
    {
        ArgumentNullException.ThrowIfNull(e);
        int priority = (Facility * 8) + (Failed(e.Action) ? 4 : 6);
        string host = string.IsNullOrWhiteSpace(hostName) ? "-" : new string([.. hostName.Where(c => c is > ' ' and < (char)127)]);
        return "<" + priority.ToString(CultureInfo.InvariantCulture) + ">1 "
            + e.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + " "
            + (host.Length == 0 ? "-" : host[..Math.Min(255, host.Length)]) + " sangam - audit - "
            + Cef(e);
    }

    /// <summary>The CEF record (CEF:0) for an event.</summary>
    /// <param name="e">The event.</param>
    public static string Cef(AuditEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        string category = Category(e.Action);
        StringBuilder x = new();
        Ext(x, "rt", e.OccurredAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        Ext(x, "externalId", e.Id.ToString(CultureInfo.InvariantCulture));
        Ext(x, "act", e.Action);
        Ext(x, "cat", category);
        Ext(x, "outcome", Outcome(e.Action));
        Ext(x, "cs1Label", "actorType");
        Ext(x, "cs1", ActorType(e.ActorType));
        if (e.ActorUserId is Guid user)
        {
            Ext(x, "suid", user.ToString());
        }

        if (e.ActorAppId is Guid app)
        {
            Ext(x, "cs2Label", "appId");
            Ext(x, "cs2", app.ToString());
        }

        if (e.TargetType is { Length: > 0 } targetType)
        {
            Ext(x, "cs3Label", "targetType");
            Ext(x, "cs3", targetType);
        }

        if (e.TargetId is Guid target)
        {
            Ext(x, "duid", target.ToString());
        }

        if (e.IpAddress is { Length: > 0 } ip && IPAddress.TryParse(ip, out _))
        {
            Ext(x, "src", ip);
        }

        if (e.UserAgent is { Length: > 0 } agent)
        {
            Ext(x, "requestClientApplication", agent);
        }

        if (e.Hash is { Length: > 0 } hash)
        {
            Ext(x, "cs4Label", "hash");
            Ext(x, "cs4", hash);
        }

        if (e.PrevHash is { Length: > 0 } prev)
        {
            Ext(x, "cs5Label", "prevHash");
            Ext(x, "cs5", prev);
        }

        if (e.Metadata is { Length: > 2 } metadata)
        {
            Ext(x, "cs6Label", "detail");
            Ext(x, "cs6", metadata);
        }

        return "CEF:0|" + Header(Vendor) + "|" + Header(Product) + "|" + Header(Version) + "|" + Header(e.Action) + "|"
            + Header(e.Action) + "|" + Severity(e.Action, category).ToString(CultureInfo.InvariantCulture) + "|" + x.ToString().TrimEnd();
    }

    /// <summary>The JSON envelope for an event.</summary>
    /// <param name="e">The event.</param>
    /// <param name="environmentName">The host environment.</param>
    public static JsonObject Json(AuditEvent e, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(e);
        JsonObject envelope = new()
        {
            ["schema"] = EnvelopeSchema,
            ["id"] = e.Id,
            ["hash"] = e.Hash,
            ["prev_hash"] = e.PrevHash,
        };
        try
        {
            envelope["detail"] = JsonNode.Parse(string.IsNullOrWhiteSpace(e.Metadata) ? "{}" : e.Metadata);
        }
        catch (JsonException)
        {
            envelope["detail"] = e.Metadata;
        }

        envelope["event"] = SharedEvent(e, environmentName);
        return envelope;
    }

    /// <summary>The event in the shared audit schema 1.0 (SGM-208 §3).</summary>
    /// <param name="e">The event.</param>
    /// <param name="environmentName">The host environment.</param>
    public static JsonObject SharedEvent(AuditEvent e, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(e);
        string type = ActorType(e.ActorType);
        JsonObject actor = new() { ["type"] = type };
        if (e.ActorUserId is Guid user && type is "user" or "admin")
        {
            actor["sub"] = user.ToString();
        }

        if (e.ActorAppId is Guid app)
        {
            actor["client_id"] = app.ToString();
        }

        JsonObject shared = new()
        {
            ["schema_version"] = "1.0",
            ["event_id"] = EventId(e),
            ["occurred_at"] = e.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["source"] = new JsonObject
            {
                ["app_id"] = "sangam",
                ["app_version"] = Version,
                ["environment"] = environmentName switch
                {
                    "Production" => "production",
                    "Staging" => "staging",
                    _ => "development",
                },
            },
        };
        if (e.TargetType == "organisation" && e.TargetId is Guid org)
        {
            shared["tenant"] = new JsonObject { ["org_id"] = org.ToString() };
        }

        shared["actor"] = actor;
        shared["action"] = "identity." + e.Action;
        shared["category"] = Category(e.Action);
        shared["target"] = new JsonObject
        {
            ["type"] = string.IsNullOrWhiteSpace(e.TargetType) ? "sangam" : e.TargetType,
            ["id"] = e.TargetId?.ToString() ?? "-",
        };
        shared["outcome"] = Outcome(e.Action);
        if (e.IpAddress is { Length: > 0 } || type is "user" or "admin")
        {
            // The schema asks for the person's address; an event recorded without one says so rather than omit it.
            JsonObject client = new() { ["ip"] = e.IpAddress is { Length: > 0 } ip ? ip : "unknown" };
            if (e.UserAgent is { Length: > 0 } agent)
            {
                client["user_agent"] = agent;
            }

            shared["client"] = client;
        }

        shared["correlation_id"] = "sangam-audit-" + e.Id.ToString(CultureInfo.InvariantCulture);
        shared["data_classification"] = type is "system" || e.Action.StartsWith("system.", StringComparison.Ordinal) || e.Action.StartsWith("audit.", StringComparison.Ordinal)
            || e.Action.EndsWith(".alert", StringComparison.Ordinal) || e.Action.EndsWith(".failing", StringComparison.Ordinal) ? "internal" : "personal";
        return shared;
    }

    /// <summary>A UUID v7 for the event, the same every time it is sent: its time is the event's, the rest comes from
    /// the audit log id.</summary>
    /// <param name="e">The event.</param>
    public static string EventId(AuditEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        byte[] bytes = SHA256.HashData(Encoding.ASCII.GetBytes("sangam-audit:" + e.Id.ToString(CultureInfo.InvariantCulture)))[..16];
        long ms = Math.Max(0, e.OccurredAt.ToUnixTimeMilliseconds());
        for (int i = 0; i < 6; i++)
        {
            bytes[i] = (byte)(ms >> (8 * (5 - i)));
        }

        bytes[6] = (byte)(0x70 | (bytes[6] & 0x0F));
        bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F));
        string hex = Convert.ToHexStringLower(bytes);
        return hex[..8] + "-" + hex[8..12] + "-" + hex[12..16] + "-" + hex[16..20] + "-" + hex[20..];
    }

    /// <summary>The shared schema's category for an identity action.</summary>
    /// <param name="action">The action.</param>
    public static string Category(string action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.StartsWith("consent.", StringComparison.Ordinal) || action is "app.access.revoke" or "admin.user.read")
        {
            return "access";
        }

        if (action is "user.data.export" or "evidence.export")
        {
            return "export";
        }

        if (action.StartsWith("user.login", StringComparison.Ordinal) || action.StartsWith("user.logout", StringComparison.Ordinal)
            || action.StartsWith("user.password", StringComparison.Ordinal) || action.StartsWith("user.otp", StringComparison.Ordinal)
            || action.StartsWith("user.mfa", StringComparison.Ordinal) || action.StartsWith("user.passkey", StringComparison.Ordinal)
            || action.StartsWith("user.stepup", StringComparison.Ordinal) || action.StartsWith("user.signature", StringComparison.Ordinal)
            || action.StartsWith("user.session", StringComparison.Ordinal) || action.StartsWith("user.sms", StringComparison.Ordinal)
            || action.StartsWith("token.", StringComparison.Ordinal) || action.StartsWith("device.", StringComparison.Ordinal)
            || action.StartsWith("admin.user.mfa", StringComparison.Ordinal) || action.StartsWith("identity.", StringComparison.Ordinal)
            || action is "admin.user.force_logout" or "admin.user.suspend" or "user.register.duplicate" or "sms.volume.alert" or "platform.alert" or "access.denied"
            || action.StartsWith("saml.assertion", StringComparison.Ordinal) || action.StartsWith("saml.logout", StringComparison.Ordinal))
        {
            return "security";
        }

        if (action is "user.register" or "org.create" or "app.register" or "app.invitation.create" or "attribute.define" or "claim.mapping.add"
            or "webhook.endpoint.save" or "grievance.log" or "saml.provider.register")
        {
            return "create";
        }

        if (action.Contains(".delete", StringComparison.Ordinal) || action.EndsWith(".retire", StringComparison.Ordinal)
            || action.EndsWith(".remove", StringComparison.Ordinal) || action.EndsWith(".purge", StringComparison.Ordinal)
            || action is "user.account.deletion.complete")
        {
            return "delete";
        }

        if (action.EndsWith(".update", StringComparison.Ordinal) || action.EndsWith(".change", StringComparison.Ordinal)
            || action is "attribute.values.set" or "user.email.verify" or "user.mobile.verify")
        {
            return "update";
        }

        return "admin";
    }

    /// <summary>The shared schema's outcome for an identity action.</summary>
    /// <param name="action">The action.</param>
    public static string Outcome(string action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action is "consent.deny" or "device.deny" or "user.signature.decline" or "identity.verify.refused" or "access.denied" or "token.refused")
        {
            return "denied";
        }

        return Failed(action) ? "failure" : "success";
    }

    private static bool Failed(string action)
        => action.EndsWith(".fail", StringComparison.Ordinal) || action.EndsWith(".failing", StringComparison.Ordinal)
        || action.EndsWith(".duplicate", StringComparison.Ordinal) || action.EndsWith(".refused", StringComparison.Ordinal);

    private static int Severity(string action, string category)
    {
        if (Failed(action))
        {
            return 6;
        }

        if (action.EndsWith(".alert", StringComparison.Ordinal) || action.StartsWith("admin.user.mfa.reset", StringComparison.Ordinal)
            || action is "admin.user.delete_now" or "admin.operator.grant" or "admin.operator.revoke" or "app.secret.rotate" or "admin.user.suspend")
        {
            return 5;
        }

        return category is "security" or "admin" ? 3 : 2;
    }

    private static string ActorType(AuditActorType type) => type switch
    {
        AuditActorType.Admin => "admin",
        AuditActorType.Api => "api",
        AuditActorType.System => "system",
        AuditActorType.Anonymous => "anonymous",
        _ => "user",
    };

    // CEF: in headers a backslash and a pipe are escaped; in extension values a backslash, an equals sign and line breaks.
    private static string Header(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);

    private static void Ext(StringBuilder x, string key, string value)
        => x.Append(key).Append('=').Append(value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("=", "\\=", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)).Append(' ');
}
