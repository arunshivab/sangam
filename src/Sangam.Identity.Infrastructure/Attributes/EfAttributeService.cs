using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Attributes;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Provisioning;

namespace Sangam.Identity.Infrastructure.Attributes;

/// <summary>
/// <see cref="IAttributeService"/> over the identity database (PR-25). Limits: 20 attributes and 10 claims per
/// application, values of 200 characters at most. Health data is refused by design (SGM-203, SGM-204): a definition
/// whose key or label reads like a condition, treatment or result is refused, and its maker must declare it is not health
/// data. A value is set only for someone who has linked the application, and an organisation's attribute only for
/// someone with a role there. Every change is audited and raises <c>user.updated</c> for SCIM and webhooks.
/// </summary>
public sealed partial class EfAttributeService : IAttributeService
{
    /// <summary>Most attributes an application may define.</summary>
    public const int MaxDefinitions = 20;

    /// <summary>Most custom claims an application may have.</summary>
    public const int MaxClaims = 10;

    /// <summary>Longest value.</summary>
    public const int MaxValueLength = 200;

    /// <summary>Claims Sangam already issues or a token needs, which a custom claim may not replace.</summary>
    public static readonly IReadOnlySet<string> ReservedClaims = new HashSet<string>(StringComparer.Ordinal)
    {
        "sub", "iss", "aud", "exp", "iat", "nbf", "jti", "azp", "nonce", "acr", "amr", "auth_time", "sid", "scope", "client_id", "act", "typ", "cnf",
        "name", "given_name", "family_name", "middle_name", "nickname", "preferred_username", "profile", "picture", "website", "email", "email_verified",
        "gender", "birthdate", "zoneinfo", "locale", "phone_number", "phone_number_verified", "address", "updated_at", "role", "roles",
        "sangam_orgs", "oi_au_id", "oi_tkn_id", "oi_prst", "oi_crt_dt", "oi_exp_dt",
    };

    // Word stems: a key or label is refused when one of its words starts with one (so "archive" is not "hiv").
    private static readonly string[] HealthWords =
    [
        "health", "medication", "medicine", "clinical", "diagnos", "disease", "illness", "symptom", "treatment", "therap", "prescri", "allerg", "blood",
        "hiv", "hepatitis", "tubercul", "cancer", "tumo", "diabet", "pregnan", "disab", "mental", "psychiatr", "covid", "vaccin", "genetic",
        "dna", "bmi", "icd", "comorbid", "pathology", "radiolog", "surgery", "surgical",
    ];

    private readonly SangamDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITenancyQuery _tenancy;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="tenancy">Tenancy, for roles and permissions in claims.</param>
    public EfAttributeService(SangamDbContext db, IAuditWriter audit, IClock clock, ITenancyQuery tenancy)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _tenancy = tenancy ?? throw new ArgumentNullException(nameof(tenancy));
    }

    /// <inheritdoc />
    public async Task<AttributesView?> GetAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
    {
        if (!await AdministersAsync(userId, appId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var definitions = await _db.UserAttributeDefinitions.AsNoTracking().Where(d => d.AppId == appId && d.RetiredAt == null).OrderBy(d => d.CreatedAt)
            .Select(d => new { d.Id, d.Key, d.Label, d.Type, d.Choices, d.EditableBy, d.OrgId, Count = _db.UserAttributeValues.Count(v => v.DefinitionId == d.Id) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<(Guid Id, string Name)> organisations = [.. (await _db.Organisations.AsNoTracking().Where(o => o.RegisteredViaAppId == appId && o.DeletedAt == null).OrderBy(o => o.Path).Select(o => new { o.Id, o.Name }).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(o => (o.Id, o.Name))];
        Dictionary<Guid, string> names = organisations.ToDictionary(o => o.Id, o => o.Name);
        List<AppClaimMapping> claims = await _db.AppClaimMappings.AsNoTracking().Where(m => m.AppId == appId).OrderBy(m => m.ClaimName).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new AttributesView(
            [.. definitions.Select(d => new AttributeDefinitionView(d.Id, d.Key, d.Label, d.Type, d.Choices, d.EditableBy, d.OrgId, d.OrgId is Guid o ? names.GetValueOrDefault(o) : null, d.Count))],
            [.. claims.Select(c => new ClaimMappingView(c.Id, c.ClaimName, c.Source, c.AttributeKey))],
            organisations);
    }

    /// <inheritdoc />
    public async Task<PartnerResult> DefineAsync(Guid userId, Guid appId, AttributeDefinitionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!await AdministersAsync(userId, appId, cancellationToken).ConfigureAwait(false))
        {
            return NotYours;
        }

        string key = (input.Key ?? string.Empty).Trim();
        string label = (input.Label ?? string.Empty).Trim();
        if (!KeyRegex().IsMatch(key))
        {
            return PartnerResult.Refused("Keys are 2 to 40 lowercase letters, digits and underscores, starting with a letter.");
        }

        if (label.Length is 0 or > 80)
        {
            return PartnerResult.Refused("Give a label of up to 80 characters.");
        }

        if (LooksLikeHealthData(key) || LooksLikeHealthData(label))
        {
            return PartnerResult.Refused("Sangam does not hold health data: keep conditions, treatments and results in your application, not in a Sangam attribute.");
        }

        if (!input.NotHealthData)
        {
            return PartnerResult.Refused("Confirm that this attribute is not health data.");
        }

        if (input.Type is not ("text" or "number" or "date" or "boolean" or "choice"))
        {
            return PartnerResult.Refused("Choose the kind of value.");
        }

        string? choices = null;
        if (input.Type == "choice")
        {
            List<string> list = [.. (input.Choices ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];
            if (list.Count < 2 || list.Any(c => c.Length > 40))
            {
                return PartnerResult.Refused("Give at least two choices, separated by commas, of up to 40 characters each.");
            }

            choices = string.Join(',', list);
        }

        if (input.EditableBy is not ("admin" or "person"))
        {
            return PartnerResult.Refused("Choose who may change it.");
        }

        if (input.OrgId is Guid org && !await _db.Organisations.AnyAsync(o => o.Id == org && o.RegisteredViaAppId == appId, cancellationToken).ConfigureAwait(false))
        {
            return PartnerResult.Refused("That organisation is not one of this application's.");
        }

        if (await _db.UserAttributeDefinitions.CountAsync(d => d.AppId == appId && d.RetiredAt == null, cancellationToken).ConfigureAwait(false) >= MaxDefinitions)
        {
            return PartnerResult.Refused($"An application can have at most {MaxDefinitions} attributes.");
        }

        if (await _db.UserAttributeDefinitions.AnyAsync(d => d.AppId == appId && d.RetiredAt == null && d.Key == key && d.OrgId == input.OrgId, cancellationToken).ConfigureAwait(false))
        {
            return PartnerResult.Refused("There is already an attribute with that key.");
        }

        UserAttributeDefinition definition = new()
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            OrgId = input.OrgId,
            Key = key,
            Label = label,
            Type = input.Type,
            Choices = choices,
            EditableBy = input.EditableBy,
            CreatedAt = _clock.UtcNow,
            CreatedByUserId = userId,
        };
        _db.UserAttributeDefinitions.Add(definition);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.AttributeDefine, AuditActorType.User, userId, appId, "attribute", definition.Id,
            Metadata: JsonSerializer.Serialize(new { key, type = definition.Type, editable_by = definition.EditableBy, org = definition.OrgId, not_health_data = true })), cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok("Attribute added.");
    }

    /// <inheritdoc />
    public async Task<PartnerResult> RetireAsync(Guid userId, Guid appId, Guid definitionId, CancellationToken cancellationToken = default)
    {
        if (!await AdministersAsync(userId, appId, cancellationToken).ConfigureAwait(false))
        {
            return NotYours;
        }

        UserAttributeDefinition? definition = await _db.UserAttributeDefinitions.FirstOrDefaultAsync(d => d.Id == definitionId && d.AppId == appId && d.RetiredAt == null, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return PartnerResult.Refused("No such attribute.");
        }

        definition.RetiredAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.AttributeRetire, AuditActorType.User, userId, appId, "attribute", definition.Id, Metadata: JsonSerializer.Serialize(new { key = definition.Key })), cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok("Attribute retired: its values are no longer shown or released.");
    }

    /// <inheritdoc />
    public async Task<PartnerResult> AddClaimAsync(Guid userId, Guid appId, ClaimMappingInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!await AdministersAsync(userId, appId, cancellationToken).ConfigureAwait(false))
        {
            return NotYours;
        }

        string name = (input.ClaimName ?? string.Empty).Trim();
        if (!KeyRegex().IsMatch(name))
        {
            return PartnerResult.Refused("Claim names are 2 to 40 lowercase letters, digits and underscores, starting with a letter.");
        }

        if (ReservedClaims.Contains(name) || name.StartsWith("sangam_", StringComparison.Ordinal))
        {
            return PartnerResult.Refused("That name is one Sangam's tokens already use. Choose another.");
        }

        if (input.Source is not ("attribute" or "roles" or "permissions" or "org_names"))
        {
            return PartnerResult.Refused("Choose where the claim's value comes from.");
        }

        string? attribute = null;
        if (input.Source == "attribute")
        {
            attribute = input.AttributeKey?.Trim();
            if (attribute is null || !await _db.UserAttributeDefinitions.AnyAsync(d => d.AppId == appId && d.RetiredAt == null && d.Key == attribute, cancellationToken).ConfigureAwait(false))
            {
                return PartnerResult.Refused("Choose one of the application's attributes.");
            }
        }

        if (await _db.AppClaimMappings.CountAsync(m => m.AppId == appId, cancellationToken).ConfigureAwait(false) >= MaxClaims)
        {
            return PartnerResult.Refused($"An application can have at most {MaxClaims} custom claims.");
        }

        if (await _db.AppClaimMappings.AnyAsync(m => m.AppId == appId && m.ClaimName == name, cancellationToken).ConfigureAwait(false))
        {
            return PartnerResult.Refused("There is already a claim with that name.");
        }

        AppClaimMapping mapping = new() { Id = Guid.NewGuid(), AppId = appId, ClaimName = name, Source = input.Source, AttributeKey = attribute, CreatedAt = _clock.UtcNow };
        _db.AppClaimMappings.Add(mapping);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.ClaimMappingAdd, AuditActorType.User, userId, appId, "claim", mapping.Id, Metadata: JsonSerializer.Serialize(new { claim = name, source = mapping.Source, attribute })), cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok("Claim added. It appears in tokens of people who allow the application their details (the attributes scope).");
    }

    /// <inheritdoc />
    public async Task<PartnerResult> RemoveClaimAsync(Guid userId, Guid appId, Guid mappingId, CancellationToken cancellationToken = default)
    {
        if (!await AdministersAsync(userId, appId, cancellationToken).ConfigureAwait(false))
        {
            return NotYours;
        }

        AppClaimMapping? mapping = await _db.AppClaimMappings.FirstOrDefaultAsync(m => m.Id == mappingId && m.AppId == appId, cancellationToken).ConfigureAwait(false);
        if (mapping is null)
        {
            return PartnerResult.Refused("No such claim.");
        }

        _db.AppClaimMappings.Remove(mapping);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.ClaimMappingRemove, AuditActorType.User, userId, appId, "claim", mapping.Id, Metadata: JsonSerializer.Serialize(new { claim = mapping.ClaimName })), cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok("Claim removed.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttributeValueRow>?> GetValuesAsync(Guid? userId, Guid appId, Guid personId, CancellationToken cancellationToken = default)
        => (userId is not Guid admin || await AdministersAsync(admin, appId, cancellationToken).ConfigureAwait(false)) && await LinkedAsync(appId, personId, cancellationToken).ConfigureAwait(false)
            ? await RowsAsync(appId, personId, mineOnly: false, cancellationToken).ConfigureAwait(false)
            : null;

    /// <inheritdoc />
    public async Task<PartnerResult> SetValuesAsync(Guid? userId, Guid appId, Guid personId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (userId is Guid admin && !await AdministersAsync(admin, appId, cancellationToken).ConfigureAwait(false))
        {
            return NotYours;
        }

        if (!await LinkedAsync(appId, personId, cancellationToken).ConfigureAwait(false))
        {
            return PartnerResult.Refused("That person has not linked the application.");
        }

        return await SaveAsync(userId, appId, personId, values, mineOnly: false, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersonAttributeGroup>> GetMineAsync(Guid personId, CancellationToken cancellationToken = default)
    {
        var apps = await _db.AppGrants.AsNoTracking().Where(g => g.UserId == personId && g.RevokedAt == null)
            .Where(g => _db.UserAttributeDefinitions.Any(d => d.AppId == g.AppId && d.RetiredAt == null))
            .Select(g => new { g.AppId, g.App!.DisplayName })
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<PersonAttributeGroup> groups = [];
        foreach (var app in apps.OrderBy(a => a.DisplayName, StringComparer.CurrentCulture))
        {
            // Everything the application keeps is shown (the person's right to know); only the person's own rows are editable.
            IReadOnlyList<AttributeValueRow> rows = await RowsAsync(app.AppId, personId, mineOnly: false, cancellationToken).ConfigureAwait(false);
            if (rows.Any(r => r.Value is not null || r.EditableBy == "person"))
            {
                groups.Add(new PersonAttributeGroup(app.AppId, app.DisplayName, rows));
            }
        }

        return groups;
    }

    /// <inheritdoc />
    public async Task<PartnerResult> SetMineAsync(Guid personId, Guid appId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        return !await LinkedAsync(appId, personId, cancellationToken).ConfigureAwait(false)
            ? PartnerResult.Refused("You have not linked that application.")
            : await SaveAsync(personId, appId, personId, values, mineOnly: true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object>> ClaimsAsync(Guid personId, Guid appId, CancellationToken cancellationToken = default)
    {
        List<AppClaimMapping> mappings = await _db.AppClaimMappings.AsNoTracking().Where(m => m.AppId == appId).ToListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, object> claims = new(StringComparer.Ordinal);
        if (mappings.Count == 0)
        {
            return claims;
        }

        IReadOnlyList<AttributeValueRow> values = await RowsAsync(appId, personId, mineOnly: false, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<OrgClaim> orgs = mappings.Any(m => m.Source != "attribute") ? await _tenancy.GetOrgClaimsAsync(personId, appId, cancellationToken).ConfigureAwait(false) : [];
        foreach (AppClaimMapping mapping in mappings)
        {
            object? value = mapping.Source switch
            {
                "roles" => orgs.Select(o => o.Role).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                "permissions" => orgs.SelectMany(o => o.Permissions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                "org_names" => orgs.Select(o => o.Name).Distinct(StringComparer.Ordinal).ToArray(),
                _ => values.FirstOrDefault(v => v.Key == mapping.AttributeKey && v.Value is not null) is AttributeValueRow row ? Typed(row) : null,
            };
            // An empty list is left out, as an unset attribute is: nothing to release, nothing to show on consent.
            if (value is not null && value is not string[] { Length: 0 })
            {
                claims[mapping.ClaimName] = value;
            }
        }

        return claims;
    }

    /// <summary>Whether a key or label reads like health data.</summary>
    /// <param name="text">The key or label.</param>
    public static bool LooksLikeHealthData(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] words = WordSplit().Split(text.ToLowerInvariant());
        return words.Any(word => word.Length > 0 && HealthWords.Any(stem => word.StartsWith(stem, StringComparison.Ordinal)));
    }

    private async Task<IReadOnlyList<AttributeValueRow>> RowsAsync(Guid appId, Guid personId, bool mineOnly, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        List<Guid> orgs = await _db.OrgMemberships.AsNoTracking().Where(m => m.AppId == appId && m.UserId == personId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now)).Select(m => m.OrgId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var rows = await _db.UserAttributeDefinitions.AsNoTracking()
            .Where(d => d.AppId == appId && d.RetiredAt == null && (d.OrgId == null || orgs.Contains(d.OrgId.Value)) && (!mineOnly || d.EditableBy == "person"))
            .OrderBy(d => d.CreatedAt)
            .Select(d => new { d.Key, d.Label, d.Type, d.Choices, d.EditableBy, Value = _db.UserAttributeValues.Where(v => v.DefinitionId == d.Id && v.UserId == personId).Select(v => v.Value).FirstOrDefault() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new AttributeValueRow(r.Key, r.Label, r.Type, r.Choices, r.EditableBy, r.Value))];
    }

    private async Task<PartnerResult> SaveAsync(Guid? actor, Guid appId, Guid personId, IReadOnlyDictionary<string, string?> values, bool mineOnly, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        List<Guid> orgs = await _db.OrgMemberships.AsNoTracking().Where(m => m.AppId == appId && m.UserId == personId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now)).Select(m => m.OrgId).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<UserAttributeDefinition> definitions = await _db.UserAttributeDefinitions.AsNoTracking()
            .Where(d => d.AppId == appId && d.RetiredAt == null && (d.OrgId == null || orgs.Contains(d.OrgId.Value)) && (!mineOnly || d.EditableBy == "person"))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<string> changed = [];
        foreach ((string key, string? raw) in values)
        {
            UserAttributeDefinition? definition = definitions.FirstOrDefault(d => d.Key == key);
            if (definition is null)
            {
                return PartnerResult.Refused($"There is no attribute {key} you may set.");
            }

            string? value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
            if (value is not null && Problem(definition, value) is string problem)
            {
                return PartnerResult.Refused(problem);
            }

            UserAttributeValue? stored = await _db.UserAttributeValues.FirstOrDefaultAsync(v => v.DefinitionId == definition.Id && v.UserId == personId, cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                if (stored is not null)
                {
                    _db.UserAttributeValues.Remove(stored);
                    changed.Add(key);
                }

                continue;
            }

            value = Normalise(definition, value);
            if (stored is null)
            {
                _db.UserAttributeValues.Add(new UserAttributeValue { DefinitionId = definition.Id, UserId = personId, Value = value, UpdatedAt = now, UpdatedByUserId = actor });
                changed.Add(key);
            }
            else if (stored.Value != value)
            {
                stored.Value = value;
                stored.UpdatedAt = now;
                stored.UpdatedByUserId = actor;
                changed.Add(key);
            }
        }

        if (changed.Count == 0)
        {
            return PartnerResult.Ok("Nothing changed.");
        }

        await AppEventLog.AddAsync(_db, AppEventTypes.UserUpdated, appId, personId, null, new Dictionary<string, object?> { ["attributes"] = changed }, now, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.AttributeValuesSet, actor is null ? AuditActorType.Api : AuditActorType.User, actor, appId, "user", personId,
            Metadata: JsonSerializer.Serialize(new { keys = changed, by = mineOnly ? "person" : actor is null ? "application" : "administrator" })), cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok("Saved.");
    }

    private static string? Problem(UserAttributeDefinition definition, string value)
    {
        if (value.Length > MaxValueLength)
        {
            return $"{definition.Label}: at most {MaxValueLength} characters.";
        }

        return definition.Type switch
        {
            "number" when !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _) => $"{definition.Label}: enter a number.",
            "date" when !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) => $"{definition.Label}: enter a date as yyyy-mm-dd.",
            "boolean" when value is not ("true" or "false") => $"{definition.Label}: choose yes or no.",
            "choice" when !(definition.Choices ?? string.Empty).Split(',').Contains(value) => $"{definition.Label}: choose one of the values offered.",
            _ => null,
        };
    }

    private static string Normalise(UserAttributeDefinition definition, string value)
        => definition.Type == "number" ? decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : value;

    private static object Typed(AttributeValueRow row) => row.Type switch
    {
        "number" => decimal.Parse(row.Value!, CultureInfo.InvariantCulture),
        "boolean" => row.Value == "true",
        _ => row.Value!,
    };

    private Task<bool> AdministersAsync(Guid userId, Guid appId, CancellationToken cancellationToken)
        => _db.AppAdmins.AsNoTracking().AnyAsync(a => a.UserId == userId && a.AppId == appId && a.RevokedAt == null && !a.App!.IsPlatform && a.App.Status == AppStatus.Active, cancellationToken);

    private Task<bool> LinkedAsync(Guid appId, Guid personId, CancellationToken cancellationToken)
        => _db.AppGrants.AsNoTracking().AnyAsync(g => g.AppId == appId && g.UserId == personId && g.RevokedAt == null, cancellationToken);

    private static PartnerResult NotYours { get; } = PartnerResult.Refused("You do not administer this application.");

    [GeneratedRegex("^[a-z][a-z0-9_]{1,39}$")]
    private static partial Regex KeyRegex();

    [GeneratedRegex("[^a-z]+")]
    private static partial Regex WordSplit();
}
