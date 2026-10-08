using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Services;

/// <summary>
/// Hash chain over audit events (OI-039): each event’s hash covers its content and the previous
/// hash, so altering, inserting or deleting an event breaks the chain from that point on.
/// </summary>
public static class AuditChain
{
    /// <summary>PostgreSQL advisory-lock key that serialises chain writes.</summary>
    public const long LockKey = 0x5A6E_6761_6D41_7564;

    /// <summary>Truncates to whole microseconds, the precision PostgreSQL stores.</summary>
    /// <param name="value">The time.</param>
    public static DateTimeOffset ToStoredPrecision(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Ticks - (value.Ticks % 10), value.Offset);
    }

    /// <summary>The hash of <paramref name="e"/> chained after <paramref name="prevHash"/>.</summary>
    /// <param name="prevHash">Previous hash, or <see langword="null"/>.</param>
    /// <param name="e">The event (Id is not covered: it is assigned after hashing).</param>
    public static string Compute(string? prevHash, AuditEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        string canonical = string.Join('\u001f',
            prevHash ?? string.Empty,
            e.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
            e.Action,
            e.ActorType.ToString(),
            e.ActorUserId?.ToString() ?? string.Empty,
            e.ActorAppId?.ToString() ?? string.Empty,
            e.TargetType ?? string.Empty,
            e.TargetId?.ToString() ?? string.Empty,
            CanonicalJson(e.Metadata),
            e.IpAddress ?? string.Empty,
            e.UserAgent ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// The metadata in a canonical form — keys sorted, no whitespace — so that PostgreSQL’s jsonb
    /// normalisation (which reorders keys and drops whitespace) cannot change the hash.
    /// </summary>
    /// <param name="json">The metadata JSON.</param>
    public static string CanonicalJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonNode? node = JsonNode.Parse(json);
        return node is null ? "null" : Sort(node).ToJsonString();
    }

    private static JsonNode Sort(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                JsonObject sorted = [];
                foreach (KeyValuePair<string, JsonNode?> pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    sorted[pair.Key] = pair.Value is null ? null : Sort(pair.Value.DeepClone());
                }

                return sorted;
            case JsonArray array:
                JsonArray copy = [];
                foreach (JsonNode? item in array)
                {
                    copy.Add(item is null ? null : Sort(item.DeepClone()));
                }

                return copy;
            default:
                return node.DeepClone();
        }
    }

    /// <summary>
    /// Re-computes the chain in id order from the oldest remaining chained event. Returns the id of
    /// the first event whose hash does not match, or <see langword="null"/> when the chain is intact.
    /// Events written before the chain existed (no hash) are skipped.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<long?> VerifyAsync(SangamDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        string? previous = null;
        bool first = true;
        await foreach (AuditEvent e in db.AuditEvents.AsNoTracking().Where(x => x.Hash != null).OrderBy(x => x.Id).AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (first)
            {
                // The oldest remaining event is the trusted start: retention removes the head of
                // the chain, and records what it removed in an audit.retention.purge event.
                previous = e.PrevHash;
                first = false;
            }

            if (!string.Equals(e.PrevHash, previous, StringComparison.Ordinal) || !string.Equals(e.Hash, Compute(previous, e), StringComparison.Ordinal))
            {
                return e.Id;
            }

            previous = e.Hash;
        }

        return null;
    }
}
