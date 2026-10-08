using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Evidence;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Siem;

namespace Sangam.Identity.Infrastructure.Evidence;

/// <summary>
/// <see cref="IEvidencePackService"/> over the identity database. Everything in a pack is read from what Sangam holds;
/// nothing is typed in for it. The pack says plainly which controls are Sangam's and which stay the application's.
/// Spreadsheet cells that would start a formula are prefixed with an apostrophe, so a name typed by a person cannot run
/// as a formula when an auditor opens the file.
/// </summary>
public sealed class EfEvidencePackService : IEvidencePackService
{
    /// <summary>The pack format, recorded in its manifest.</summary>
    public const string Format = "sangam.evidence.1";

    /// <summary>The longest period one pack covers.</summary>
    public const int MaxDays = 366;

    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly SangamDbContext _db;
    private readonly ISecurityPolicyService _policies;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly string _environment;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="policies">Security policies.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="environment">Host environment; Production when the container has none.</param>
    public EfEvidencePackService(SangamDbContext db, ISecurityPolicyService policies, IAuditWriter audit, IClock clock, IHostEnvironment? environment = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _environment = environment?.EnvironmentName ?? "Production";
    }

    /// <inheritdoc />
    public async Task<EvidencePack?> BuildAsync(Guid userId, Guid appId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from || to.DayNumber - from.DayNumber >= MaxDays)
        {
            throw new ArgumentOutOfRangeException(nameof(to), $"The period must run forwards and cover at most {MaxDays} days.");
        }

        bool administers = await _db.AppAdmins.AsNoTracking()
            .AnyAsync(a => a.UserId == userId && a.AppId == appId && a.RevokedAt == null && !a.App!.IsPlatform, cancellationToken).ConfigureAwait(false);
        App? app = administers ? await _db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, cancellationToken).ConfigureAwait(false) : null;
        if (app is null)
        {
            return null;
        }

        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), India).ToUniversalTime();
        DateTimeOffset end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), India).ToUniversalTime();
        List<(string Name, byte[] Bytes, string Description)> files = [];

        // ---- the application and its sign-in rules
        ClientRow? client = await _db.Database
            .SqlQueryRaw<ClientRow>(
                "SELECT client_id, client_type, consent_type, permissions, " +
                "requirements, redirect_uris, post_logout_redirect_uris " +
                "FROM openiddict_applications WHERE client_id = {0}",
                app.ClientId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        SecurityPolicy effective = await _policies.ForAppAsync(app.Id, cancellationToken).ConfigureAwait(false);
        JsonObject application = new()
        {
            ["app_id"] = app.Id.ToString(),
            ["client_id"] = app.ClientId,
            ["name"] = app.DisplayName,
            ["owner_company"] = app.OwnerCompanyName,
            ["status"] = app.Status.ToString(),
            ["registered_at"] = Stamp(app.CreatedAt),
            ["updated_at"] = Stamp(app.UpdatedAt),
            ["client_type"] = client?.ClientType,
            ["consent"] = app.RequireConsent ? "asked of each person" : "implicit",
            ["consent_version"] = app.ConsentVersion,
            ["pkce_required"] = client?.Requirements?.Contains("ft:pkce", StringComparison.Ordinal) ?? false,
            ["permissions"] = JsonArrayOf(client?.Permissions),
            ["redirect_uris"] = JsonArrayOf(client?.RedirectUris),
            ["post_logout_redirect_uris"] = JsonArrayOf(client?.PostLogoutRedirectUris),
            ["back_channel_logout_uri"] = app.BackChannelLogoutUri,
            ["front_channel_logout_uri"] = app.FrontChannelLogoutUri,
            ["homepage"] = app.HomepageUrl,
            ["privacy_policy"] = app.PrivacyUrl,
            ["terms"] = app.TermsUrl,
        };
        files.Add(("application.json", Json(application), "The application's registration with Sangam: client, redirect and sign-out addresses, grants and permissions, consent.")); // i18n-ignore: the pack is an English document for auditors

        List<Organisation> orgs = await _db.Organisations.AsNoTracking()
            .Where(o => o.RegisteredViaAppId == app.Id && o.DeletedAt == null)
            .OrderBy(o => o.Path)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        JsonObject policy = new()
        {
            ["application"] = new JsonObject
            {
                ["sign_in"] = effective.SignIn.ToString(),
                ["two_step"] = effective.Mfa.ToString(),
                ["minimum_password_length"] = effective.MinPasswordLength,
                ["breached_password_check"] = effective.BreachedPasswordCheck,
            },
            ["organisations_with_their_own_rules"] = new JsonArray([.. orgs
                .Where(o => o.SignInPolicy is not null || o.MfaRequirement is not null || o.MinPasswordLength is not null || o.BreachedPasswordCheck is not null)
                .Select(o => (JsonNode)new JsonObject
                {
                    ["org_id"] = o.Id.ToString(),
                    ["name"] = o.Name,
                    ["sign_in"] = o.SignInPolicy?.ToString(),
                    ["two_step"] = o.MfaRequirement?.ToString(),
                    ["minimum_password_length"] = o.MinPasswordLength,
                    ["breached_password_check"] = o.BreachedPasswordCheck,
                })]),
            ["rule"] = "The strictest rule that applies to a person wins: the application's, then each organisation's above them (SGM-201).",
        };
        files.Add(("sign-in-policy.json", Json(policy), "The sign-in rules in force when the pack was made: the application's and any organisation's own.")); // i18n-ignore: the pack is an English document for auditors

        // ---- who administers it, its roles and organisations, and who has access
        var admins = await _db.AppAdmins.AsNoTracking()
            .Where(a => a.AppId == app.Id && (a.RevokedAt == null || a.RevokedAt >= start))
            .OrderBy(a => a.GrantedAt)
            .Select(a => new { a.UserId, a.User!.FirstName, a.User.LastName, a.User.Email, a.User.TwoFactorEnabled, a.Role, a.GrantedAt, a.RevokedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        files.Add(("administrators.csv", Csv(
            ["user_id", "name", "email", "rank", "two_step_enrolled", "granted_at", "revoked_at"],
            admins.Select(a => new[] { a.UserId.ToString(), Name(a.FirstName, a.LastName), a.Email, a.Role.ToString(), YesNo(a.TwoFactorEnabled), Stamp(a.GrantedAt), Stamp(a.RevokedAt) })),
            "The application's administrators on the partner console, current and removed during the period, with whether each has two-step sign-in.")); // i18n-ignore: the pack is an English document for auditors

        List<Role> roles = await _db.Roles.AsNoTracking().Where(r => r.AppId == app.Id).OrderBy(r => r.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        files.Add(("roles.csv", Csv(
            ["code", "name", "permissions", "organisation_only", "created_at", "retired_at"],
            roles.Select(r => new[] { r.Code, r.DisplayName, string.Join(' ', Permissions(r.Permissions)), r.OrgId?.ToString(), Stamp(r.CreatedAt), Stamp(r.RetiredAt) })),
            "The roles the application defines and the permissions each carries.")); // i18n-ignore: the pack is an English document for auditors

        Dictionary<Guid, string> orgNames = orgs.ToDictionary(o => o.Id, o => o.Name);
        files.Add(("organisations.csv", Csv(
            ["org_id", "name", "type", "parent_id", "path", "status", "created_at"],
            orgs.Select(o => new[] { o.Id.ToString(), o.Name, o.OrgTypeCode, o.ParentOrgId?.ToString(), o.Path, o.Status.ToString(), Stamp(o.CreatedAt) })),
            "The organisations registered through the application.")); // i18n-ignore: the pack is an English document for auditors

        var memberships = await _db.OrgMemberships.AsNoTracking()
            .Where(m => m.AppId == app.Id && (m.RevokedAt == null || m.RevokedAt >= start) && m.GrantedAt < end)
            .OrderBy(m => m.GrantedAt)
            .Select(m => new
            {
                m.UserId,
                m.User!.FirstName,
                m.User.LastName,
                m.User.Email,
                m.User.TwoFactorEnabled,
                UserStatus = m.User.Status,
                m.OrgId,
                RoleCode = m.Role!.Code,
                m.AppliesToDescendants,
                m.GrantedAt,
                m.GrantedByUserId,
                m.ExpiresAt,
                m.RevokedAt,
                m.RevokedByUserId,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<Guid> people = [.. memberships.Select(m => m.UserId).Distinct()];
        Dictionary<Guid, DateTimeOffset> lastSignIn = (await _db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == AuditActions.TokenIssue && e.TargetType == "app" && e.TargetId == app.Id && e.ActorUserId != null && people.Contains(e.ActorUserId.Value))
            .GroupBy(e => e.ActorUserId!.Value)
            .Select(g => new { UserId = g.Key, Last = g.Max(e => e.OccurredAt) })
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(x => x.UserId, x => x.Last);
        files.Add(("access-list.csv", Csv(
            ["user_id", "name", "email", "account_status", "two_step_enrolled", "organisation", "org_id", "role", "includes_organisations_below", "granted_at", "expires_at", "revoked_at", "last_token_issued_at"],
            memberships.Where(m => m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now)).Select(m => new[]
            {
                m.UserId.ToString(), Name(m.FirstName, m.LastName), m.Email, m.UserStatus.ToString(), YesNo(m.TwoFactorEnabled),
                orgNames.GetValueOrDefault(m.OrgId), m.OrgId.ToString(), m.RoleCode, YesNo(m.AppliesToDescendants), Stamp(m.GrantedAt), Stamp(m.ExpiresAt), null,
                lastSignIn.TryGetValue(m.UserId, out DateTimeOffset last) ? Stamp(last) : null,
            })),
            "Everyone who holds a role in the application now, where, as what, until when, and when Sangam last issued them a token for it — the list an access review starts from.")); // i18n-ignore: the pack is an English document for auditors

        List<string?[]> changes = [];
        foreach (var m in memberships)
        {
            if (m.GrantedAt >= start)
            {
                changes.Add([Stamp(m.GrantedAt), "granted", m.UserId.ToString(), Name(m.FirstName, m.LastName), orgNames.GetValueOrDefault(m.OrgId), m.RoleCode, m.GrantedByUserId?.ToString(), Stamp(m.ExpiresAt)]);
            }

            if (m.RevokedAt is DateTimeOffset revoked && revoked >= start && revoked < end)
            {
                changes.Add([Stamp(revoked), m.ExpiresAt is DateTimeOffset expiry && expiry <= revoked ? "expired" : "revoked", m.UserId.ToString(), Name(m.FirstName, m.LastName), orgNames.GetValueOrDefault(m.OrgId), m.RoleCode, m.RevokedByUserId?.ToString(), null]);
            }
        }

        files.Add(("access-changes.csv", Csv(
            ["at", "change", "user_id", "name", "organisation", "role", "by_user_id", "until"],
            changes.OrderBy(c => c[0], StringComparer.Ordinal)),
            "Joiners, movers and leavers in the period: every role granted, revoked or expired, by whom.")); // i18n-ignore: the pack is an English document for auditors

        // ---- the audit trail
        List<AuditEvent> events = await _db.AuditEvents.AsNoTracking()
            .Where(e => e.OccurredAt >= start && e.OccurredAt < end && (e.ActorAppId == app.Id || (e.TargetType == "app" && e.TargetId == app.Id)))
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // R7: a Sangam operator making someone an owner acts in the platform's capacity (no actor application); the
        // grant names the application in its detail.
        string forThisApp = JsonSerializer.Serialize(new Dictionary<string, string> { ["app_id"] = app.Id.ToString("D") });
        List<AuditEvent> ownerGrants = await _db.AuditEvents.AsNoTracking()
            .Where(e => e.OccurredAt >= start && e.OccurredAt < end && e.Action == AuditActions.AppAdminGrant && e.ActorAppId == null
                && EF.Functions.JsonContains(e.Metadata, forThisApp))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        events = [.. events.Concat(ownerGrants).OrderBy(e => e.Id)];
        StringBuilder lines = new();
        foreach (AuditEvent e in events)
        {
            lines.Append(SiemFormat.Json(e, _environment).ToJsonString()).Append('\n');
        }

        files.Add(("audit-events.jsonl", Utf8.GetBytes(lines.ToString()),
            "Every audit event about the application in the period, one per line: Sangam's audit id, its hash chain, and the event in the shared audit schema 1.0 (SGM-208).")); // i18n-ignore: the pack is an English document for auditors

        files.Add(("audit-summary.csv", Csv(
            ["action", "category", "outcome", "events", "first", "last"],
            events.GroupBy(e => e.Action).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new[]
            {
                g.Key, SiemFormat.Category(g.Key), SiemFormat.Outcome(g.Key), g.Count().ToString(CultureInfo.InvariantCulture), Stamp(g.Min(e => e.OccurredAt)), Stamp(g.Max(e => e.OccurredAt)),
            })),
            "The audit events in the period, counted by action.")); // i18n-ignore: the pack is an English document for auditors

        long? broken = await AuditChain.VerifyAsync(_db, cancellationToken).ConfigureAwait(false);
        StringBuilder chain = new();
        chain.Append("Sangam audit log integrity check (OI-039, SGM-801)\n\n")
            .Append("Checked at: ").Append(Stamp(now)).Append('\n')
            .Append("Result: ").Append(broken is null ? "INTACT — every chained event's hash recomputes and links to the one before it." : "BROKEN at audit event " + broken.Value.ToString(CultureInfo.InvariantCulture) + ".").Append('\n') // i18n-ignore: the pack is an English document for auditors
            .Append("Events in this pack: ").Append(events.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (events.Count > 0)
        {
            chain.Append("First: audit id ").Append(events[0].Id.ToString(CultureInfo.InvariantCulture)).Append(", hash ").Append(events[0].Hash ?? "-").Append('\n')
                .Append("Last: audit id ").Append(events[^1].Id.ToString(CultureInfo.InvariantCulture)).Append(", hash ").Append(events[^1].Hash ?? "-").Append('\n');
        }

        chain.Append("\nEach event's hash is SHA-256 over the previous event's hash and the event itself, in id order, across the whole\n")
            .Append("log (not only this application's events). Sangam's operators can re-run the check; a SIEM receiving the stream\n")
            .Append("(Sangam:Siem) holds an independent copy of the chain.\n");
        files.Add(("audit-chain.txt", Utf8.GetBytes(chain.ToString()), "The audit log's hash chain checked when the pack was made.")); // i18n-ignore: the pack is an English document for auditors

        // ---- integrations (never their secrets)
        ScimTarget? scim = await _db.ScimTargets.AsNoTracking().FirstOrDefaultAsync(t => t.AppId == app.Id, cancellationToken).ConfigureAwait(false);
        List<WebhookEndpoint> hooks = await _db.WebhookEndpoints.AsNoTracking().Where(w => w.AppId == app.Id).OrderBy(w => w.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<UserAttributeDefinition> attributes = await _db.UserAttributeDefinitions.AsNoTracking().Where(d => d.AppId == app.Id).OrderBy(d => d.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<AppClaimMapping> claims = await _db.AppClaimMappings.AsNoTracking().Where(c => c.AppId == app.Id).OrderBy(c => c.ClaimName).ToListAsync(cancellationToken).ConfigureAwait(false);
        JsonObject integrations = new()
        {
            ["scim"] = scim is null ? null : new JsonObject
            {
                ["base_url"] = scim.BaseUrl,
                ["authentication"] = scim.AuthMode,
                ["groups"] = scim.GroupMapping,
                ["on_losing_access"] = scim.DeleteOnDeprovision ? "delete" : "deactivate",
                ["enabled"] = scim.Enabled,
                ["status"] = scim.Status,
                ["last_success_at"] = Stamp(scim.LastSuccessAt),
                ["last_reconciled_at"] = Stamp(scim.LastReconciledAt),
            },
            ["webhooks"] = new JsonArray([.. hooks.Select(w => (JsonNode)new JsonObject
            {
                ["url"] = w.Url,
                ["events"] = w.Events,
                ["enabled"] = w.Enabled,
                ["status"] = w.Status,
                ["signed"] = "Standard Webhooks, HMAC-SHA256",
                ["last_success_at"] = Stamp(w.LastSuccessAt),
            })]),
            ["attributes"] = new JsonArray([.. attributes.Select(d => (JsonNode)new JsonObject
            {
                ["key"] = d.Key,
                ["label"] = d.Label,
                ["type"] = d.Type,
                ["editable_by"] = d.EditableBy,
                ["organisation_only"] = d.OrgId?.ToString(),
                ["retired_at"] = Stamp(d.RetiredAt),
            })]),
            ["custom_claims"] = new JsonArray([.. claims.Select(c => (JsonNode)new JsonObject { ["claim"] = c.ClaimName, ["source"] = c.Source, ["attribute"] = c.AttributeKey })]),
            ["note"] = "Secrets and tokens are never included: they are stored encrypted and shown once.",
        };
        files.Add(("integrations.json", Json(integrations), "SCIM provisioning, webhooks, custom attributes and claims as configured (no secrets).")); // i18n-ignore: the pack is an English document for auditors

        files.Insert(0, ("README.md", Utf8.GetBytes(Readme(app, from, to, now, effective, admins.Count(a => a.RevokedAt == null), admins.Count(a => a.RevokedAt == null && !a.TwoFactorEnabled),
            memberships.Count(m => m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now)), changes.Count, events.Count, broken)), "What the pack holds and which controls are Sangam's.")); // i18n-ignore: the pack is an English document for auditors

        // ---- the manifest, then the zip
        List<EvidenceFile> listed = [.. files.Select(f => new EvidenceFile(f.Name, f.Bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(f.Bytes)), f.Description))];
        JsonObject manifest = new()
        {
            ["format"] = Format,
            ["application"] = new JsonObject { ["app_id"] = app.Id.ToString(), ["client_id"] = app.ClientId, ["name"] = app.DisplayName },
            ["period"] = new JsonObject { ["from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["time_zone"] = "Asia/Kolkata" },
            ["generated_at"] = Stamp(now),
            ["generated_by_user_id"] = userId.ToString(),
            ["sangam_version"] = SiemFormat.Version,
            ["files"] = new JsonArray([.. listed.Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["bytes"] = f.Bytes, ["sha256"] = f.Sha256, ["description"] = f.Description })]),
        };
        byte[] manifestBytes = Json(manifest);

        byte[] zip;
        using (MemoryStream buffer = new())
        {
            using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach ((string name, byte[] bytes, _) in files.Append(("manifest.json", manifestBytes, string.Empty)))
                {
                    ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    entry.LastWriteTime = now;
                    using Stream stream = entry.Open();
                    stream.Write(bytes);
                }
            }

            zip = buffer.ToArray();
        }

        string sha = Convert.ToHexStringLower(SHA256.HashData(zip));
        string fileName = string.Create(CultureInfo.InvariantCulture, $"sangam-evidence-{app.Slug}-{from:yyyyMMdd}-{to:yyyyMMdd}.zip");
        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.EvidenceExport,
                AuditActorType.User,
                userId,
                ActorAppId: app.Id,
                TargetType: "app",
                TargetId: app.Id,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["sha256"] = sha,
                    ["events"] = events.Count,
                    ["people"] = people.Count,
                })),
            cancellationToken).ConfigureAwait(false);
        return new EvidencePack(fileName, zip, sha, [.. listed, new EvidenceFile("manifest.json", manifestBytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), "The list of files with their SHA-256.")]); // i18n-ignore: the pack is an English document for auditors
    }

    /// <summary>A CSV file: RFC 4180 quoting, and no cell that a spreadsheet would read as a formula.</summary>
    /// <param name="header">Column names.</param>
    /// <param name="rows">Rows.</param>
    public static byte[] Csv(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string?>> rows)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(rows);
        StringBuilder csv = new();
        csv.AppendJoin(',', header.Select(Cell)).Append("\r\n");
        foreach (IReadOnlyList<string?> row in rows)
        {
            csv.AppendJoin(',', row.Select(Cell)).Append("\r\n");
        }

        return Utf8.GetBytes(csv.ToString());
    }

    /// <summary>One CSV cell.</summary>
    /// <param name="value">The value.</param>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // A cell starting with = + - @ (or a tab or carriage return) is a formula to a spreadsheet: neutralise it.
        string safe = value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return safe.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : safe;
    }

    private static string Readme(App app, DateOnly from, DateOnly to, DateTimeOffset now, SecurityPolicy policy, int admins, int adminsWithoutTwoStep, int people, int changes, int events, long? broken)
    {
        static string Line(string a, string b) => "| " + a + " | " + b + " |\n";
        StringBuilder r = new();
        r.Append("# Evidence pack: ").Append(app.DisplayName).Append("\n\n")
            .Append("Made by Sangam (SangamID) on ").Append(Stamp(now)).Append(" for the period ")
            .Append(from.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)).Append(" to ").Append(to.ToString("d MMMM yyyy", CultureInfo.InvariantCulture))
            .Append(" (India time). Every figure below is read from Sangam's records; `manifest.json` lists each file with its SHA-256.\n\n")
            .Append("## At a glance\n\n| | |\n|---|---|\n")
            .Append(Line("Application", app.DisplayName + " (`" + app.ClientId + "`), " + app.OwnerCompanyName))
            .Append(Line("Status", app.Status.ToString()))
            .Append(Line("Sign-in rule", policy.SignIn.ToString() + "; two-step sign-in: " + policy.Mfa + "; minimum password " + policy.MinPasswordLength.ToString(CultureInfo.InvariantCulture) + " characters; breached-password check " + (policy.BreachedPasswordCheck ? "on" : "off")))
            .Append(Line("Administrators", admins.ToString(CultureInfo.InvariantCulture) + (adminsWithoutTwoStep == 0 ? ", all with two-step sign-in" : ", of whom " + adminsWithoutTwoStep.ToString(CultureInfo.InvariantCulture) + " without two-step sign-in")))
            .Append(Line("People with a role now", people.ToString(CultureInfo.InvariantCulture)))
            .Append(Line("Access changes in the period", changes.ToString(CultureInfo.InvariantCulture)))
            .Append(Line("Audit events in the period", events.ToString(CultureInfo.InvariantCulture)))
            .Append(Line("Audit log integrity", broken is null ? "intact" : "BROKEN at audit event " + broken.Value.ToString(CultureInfo.InvariantCulture)))
            .Append("\n## Files\n\n")
            .Append("- `application.json`: the registration — client, redirect and sign-out addresses, grants, consent.\n")
            .Append("- `sign-in-policy.json`: the sign-in rules in force, the application's and organisations' own.\n")
            .Append("- `administrators.csv`: who administers the application, with two-step sign-in status.\n")
            .Append("- `roles.csv`, `organisations.csv`: the application's roles and permissions, and its organisations.\n")
            .Append("- `access-list.csv`: everyone with a role now — the starting point of an access review.\n")
            .Append("- `access-changes.csv`: joiners, movers and leavers in the period.\n")
            .Append("- `audit-events.jsonl`, `audit-summary.csv`: the audit trail for the application, in the shared audit schema.\n")
            .Append("- `audit-chain.txt`: the audit log's hash chain, checked.\n")
            .Append("- `integrations.json`: SCIM, webhooks, attributes and claims (no secrets).\n\n")
            .Append("## Whose control is it\n\n")
            .Append("Sangam runs, for this application: sign-in (passwords checked against breached lists when on, e-mail and SMS codes, passkeys, ")
            .Append("authenticator apps), the sign-in rules above, step-up for signatures, consent, sessions and sign-out, roles and their expiry, ")
            .Append("provisioning and webhooks, and the tamper-evident audit log of all of it.\n\n")
            .Append("The application remains responsible for: what each role may do inside it, the records it keeps, its own audit of ")
            .Append("actions on those records (the Sangam SDKs write them in the shared schema), its hosting, and reviewing this access ")
            .Append("list at the interval its own policy sets.\n\n")
            .Append("This pack is evidence for the application's own audit (for example NABH, ISO/IEC 27001 or a customer's review). ")
            .Append("It is not a certification of Sangam or of the application.\n");
        return r.ToString();
    }

    private static byte[] Json(JsonNode node) => Utf8.GetBytes(node.ToJsonString(Indented) + "\n");

    private static JsonArray JsonArrayOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(json) as JsonArray ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<string> Permissions(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [json];
        }
    }

    private static string Name(string first, string last) => string.IsNullOrWhiteSpace(last) ? first : first + " " + last;

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string? Stamp(DateTimeOffset? value)
        => value?.ToOffset(India).ToString("yyyy-MM-dd'T'HH:mm:ss'+05:30'", CultureInfo.InvariantCulture);

    /// <summary>The OpenIddict application row, read for the registration.</summary>
    private sealed record ClientRow(string ClientId, string? ClientType, string? ConsentType, string? Permissions, string? Requirements, string? RedirectUris, string? PostLogoutRedirectUris);
}
