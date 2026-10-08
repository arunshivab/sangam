using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Sms;

namespace Sangam.Identity.Infrastructure.Customisation;

/// <summary>
/// Branding and message templates by level (PR-19, SGM-209). Every setting resolves from the most specific level
/// that sets it: the organisation (and the organisations above it), then the application, then the platform, then
/// Sangam's built-in default.
/// </summary>
public sealed class EfCustomisationService : ICustomisationService, IMessageTemplates, IDisposable
{
    /// <summary>The branding key in <c>customisations</c>.</summary>
    public const string BrandingKey = "branding";

    /// <summary>The largest logo accepted.</summary>
    public const int MaxLogoBytes = 200 * 1024;

    /// <summary>The languages templates and welcome lines are kept in.</summary>
    public static readonly IReadOnlyList<string> Languages = ["en-IN", "hi-IN", "ml-IN"];

    /// <summary>Sangam's own header colour in e-mails when nothing else is set.</summary>
    public const string SangamTeal = "#0F3B38";

    private readonly IDbContextFactory<SangamDbContext> _contexts;

    // The context of the call in progress (one at a time, under _gate).
    private SangamDbContext _db = null!;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly SmsSettings _sms;

    // In a console several components load at once on one circuit. Each call therefore gets its own DbContext
    // (never the circuit's shared one), and calls on this instance take turns.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initialises the service.</summary>
    /// <param name="contexts">Database contexts: each call uses its own, so it never shares a console's scoped context.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="sms">SMS settings, for the registered English SMS texts.</param>
    public EfCustomisationService(IDbContextFactory<SangamDbContext> contexts, IAuditWriter audit, IClock clock, SmsSettings sms)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _sms = sms ?? throw new ArgumentNullException(nameof(sms));
    }

    /// <inheritdoc />
    public Task<LoginBranding> ResolveLoginBrandingAsync(Guid? appId, Guid? orgId, string language, CancellationToken cancellationToken = default)
        => GatedAsync(() => ResolveLoginBrandingAsyncCore(appId, orgId, language, cancellationToken), cancellationToken);

    private async Task<LoginBranding> ResolveLoginBrandingAsyncCore(Guid? appId, Guid? orgId, string language, CancellationToken cancellationToken)
    {
        List<(CustomisationScope Scope, Guid? Id)> chain = await ChainAsync(appId, orgId, includeHere: true, cancellationToken).ConfigureAwait(false);
        List<JsonObject> levels = [];
        foreach ((CustomisationScope scope, Guid? id) in chain)
        {
            if (await ReadBrandingAsync(scope, id, cancellationToken).ConfigureAwait(false) is JsonObject json)
            {
                levels.Add(json);
            }
        }

        string? First(string property) => levels.Select(l => (string?)l[property]).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        string? welcome = null;
        foreach (string lang in new[] { NormaliseLanguage(language), "en-IN" }.Distinct())
        {
            welcome ??= levels.Select(l => (string?)l["welcome"]?[lang]).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }

        string? logo = First("logo");
        string? logoUrl = null;
        if (Guid.TryParse(logo, out Guid assetId))
        {
            string? hash = await _db.BrandingAssets.AsNoTracking().Where(a => a.Id == assetId).Select(a => a.Sha256).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            logoUrl = hash is null ? null : LogoUrl(assetId, hash);
        }

        if (levels.Count == 0 && logoUrl is null)
        {
            return LoginBranding.None;
        }

        return new LoginBranding(logoUrl, First("accent"), welcome, First("help"), First("terms"), First("privacy"));
    }

    /// <inheritdoc />
    public Task<BrandingLogo?> GetLogoAsync(Guid assetId, CancellationToken cancellationToken = default)
        => GatedAsync(() => GetLogoAsyncCore(assetId, cancellationToken), cancellationToken);

    private async Task<BrandingLogo?> GetLogoAsyncCore(Guid assetId, CancellationToken cancellationToken)
        => await _db.BrandingAssets.AsNoTracking().Where(a => a.Id == assetId)
            .Select(a => new BrandingLogo(a.ContentType, a.Content, a.Sha256)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<BrandingSettings?> GetBrandingAsync(Guid userId, CustomisationScope scope, Guid? scopeId, CancellationToken cancellationToken = default)
        => GatedAsync(() => GetBrandingAsyncCore(userId, scope, scopeId, cancellationToken), cancellationToken);

    private async Task<BrandingSettings?> GetBrandingAsyncCore(Guid userId, CustomisationScope scope, Guid? scopeId, CancellationToken cancellationToken)
    {
        if (!await CanEditAsync(userId, scope, scopeId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        CustomisationSetting? row = await FindBrandingRowAsync(scope, scopeId, cancellationToken).ConfigureAwait(false);
        JsonObject json = row is null ? [] : JsonNode.Parse(row.Value) as JsonObject ?? [];
        Dictionary<string, string> welcome = new(StringComparer.Ordinal);
        if (json["welcome"] is JsonObject w)
        {
            foreach ((string lang, JsonNode? text) in w)
            {
                if ((string?)text is string t)
                {
                    welcome[lang] = t;
                }
            }
        }

        string? logoUrl = null;
        if (Guid.TryParse((string?)json["logo"], out Guid assetId))
        {
            string? hash = await _db.BrandingAssets.AsNoTracking().Where(a => a.Id == assetId).Select(a => a.Sha256).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            logoUrl = hash is null ? null : LogoUrl(assetId, hash);
        }

        return new BrandingSettings(logoUrl, (string?)json["accent"], welcome, (string?)json["help"], (string?)json["terms"], (string?)json["privacy"], row?.Version ?? 0);
    }

    /// <inheritdoc />
    public Task<CustomisationResult> SaveBrandingAsync(Guid userId, CustomisationScope scope, Guid? scopeId, BrandingInput input, string? ipAddress, CancellationToken cancellationToken = default)
        => GatedAsync(() => SaveBrandingAsyncCore(userId, scope, scopeId, input, ipAddress, cancellationToken), cancellationToken);

    private async Task<CustomisationResult> SaveBrandingAsyncCore(Guid userId, CustomisationScope scope, Guid? scopeId, BrandingInput input, string? ipAddress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!await CanEditAsync(userId, scope, scopeId, cancellationToken).ConfigureAwait(false))
        {
            return CustomisationResult.Refused("You cannot change this.");
        }

        string? accent = string.IsNullOrWhiteSpace(input.Accent) ? null : input.Accent.Trim().ToUpperInvariant();
        if (accent is not null)
        {
            if (!IsHexColour(accent))
            {
                return CustomisationResult.Refused("The accent colour must be a hex colour such as #1D4E89.");
            }

            double ratio = ContrastWithWhite(accent);
            if (ratio < 4.5)
            {
                return CustomisationResult.Refused($"That colour is too light for white text on it ({ratio.ToString("0.0", CultureInfo.InvariantCulture)}:1; at least 4.5:1 is needed). Choose a darker shade.");
            }
        }

        string?[] links = [input.HelpUrl, input.TermsUrl, input.PrivacyUrl];
        string?[] cleanLinks = new string?[links.Length];
        for (int i = 0; i < links.Length; i++)
        {
            cleanLinks[i] = NormaliseUrl(links[i], out string? error);
            if (error is not null)
            {
                return CustomisationResult.Refused(error);
            }
        }

        JsonObject welcome = [];
        foreach ((string lang, string? text) in input.Welcome)
        {
            if (!Languages.Contains(lang) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            string t = text.Trim();
            if (t.Length > 200 || t.Contains('\n', StringComparison.Ordinal))
            {
                return CustomisationResult.Refused("A welcome line must be one line of at most 200 characters.");
            }

            welcome[lang] = t;
        }

        CustomisationSetting? row = await FindBrandingRowAsync(scope, scopeId, cancellationToken).ConfigureAwait(false);
        JsonObject json = row is null ? [] : JsonNode.Parse(row.Value) as JsonObject ?? [];
        Set(json, "accent", accent);
        Set(json, "help", cleanLinks[0]);
        Set(json, "terms", cleanLinks[1]);
        Set(json, "privacy", cleanLinks[2]);
        json["welcome"] = welcome.Count == 0 ? null : welcome;
        if (json["welcome"] is null)
        {
            json.Remove("welcome");
        }

        await UpsertBrandingAsync(row, scope, scopeId, json, userId, cancellationToken).ConfigureAwait(false);
        await AuditAsync(AuditActions.CustomisationBrandingUpdate, userId, scope, scopeId, new { scope = ScopeCode(scope), accent, help = cleanLinks[0], terms = cleanLinks[1], privacy = cleanLinks[2], welcome = welcome.Select(w => w.Key).ToArray() }, ipAddress, cancellationToken).ConfigureAwait(false);
        return CustomisationResult.Ok("Sign-in page saved.");
    }

    /// <inheritdoc />
    public Task<CustomisationResult> SaveLogoAsync(Guid userId, CustomisationScope scope, Guid? scopeId, string contentType, byte[] content, string? ipAddress, CancellationToken cancellationToken = default)
        => GatedAsync(() => SaveLogoAsyncCore(userId, scope, scopeId, contentType, content, ipAddress, cancellationToken), cancellationToken);

    private async Task<CustomisationResult> SaveLogoAsyncCore(Guid userId, CustomisationScope scope, Guid? scopeId, string contentType, byte[] content, string? ipAddress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(content);
        if (!await CanEditAsync(userId, scope, scopeId, cancellationToken).ConfigureAwait(false))
        {
            return CustomisationResult.Refused("You cannot change this.");
        }

        string? problem = LogoCheck.Problem(contentType, content, MaxLogoBytes);
        if (problem is not null)
        {
            return CustomisationResult.Refused(problem);
        }

        string type = LogoCheck.IsPng(content) ? "image/png" : "image/svg+xml";
        BrandingAsset asset = new()
        {
            Id = Guid.NewGuid(),
            Scope = scope,
            ScopeId = scopeId,
            ContentType = type,
            Content = content,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            CreatedBy = userId,
            CreatedAt = _clock.UtcNow,
        };
        _db.BrandingAssets.Add(asset);
        CustomisationSetting? row = await FindBrandingRowAsync(scope, scopeId, cancellationToken).ConfigureAwait(false);
        JsonObject json = row is null ? [] : JsonNode.Parse(row.Value) as JsonObject ?? [];
        Guid? previous = Guid.TryParse((string?)json["logo"], out Guid old) ? old : null;
        json["logo"] = asset.Id.ToString("D");
        await UpsertBrandingAsync(row, scope, scopeId, json, userId, cancellationToken).ConfigureAwait(false);
        if (previous is Guid p)
        {
            await _db.BrandingAssets.Where(a => a.Id == p).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditAsync(AuditActions.CustomisationBrandingUpdate, userId, scope, scopeId, new { scope = ScopeCode(scope), logo = asset.Sha256, type }, ipAddress, cancellationToken).ConfigureAwait(false);
        return CustomisationResult.Ok("Logo saved.");
    }

    /// <inheritdoc />
    public Task<CustomisationResult> RemoveLogoAsync(Guid userId, CustomisationScope scope, Guid? scopeId, string? ipAddress, CancellationToken cancellationToken = default)
        => GatedAsync(() => RemoveLogoAsyncCore(userId, scope, scopeId, ipAddress, cancellationToken), cancellationToken);

    private async Task<CustomisationResult> RemoveLogoAsyncCore(Guid userId, CustomisationScope scope, Guid? scopeId, string? ipAddress, CancellationToken cancellationToken)
    {
        if (!await CanEditAsync(userId, scope, scopeId, cancellationToken).ConfigureAwait(false))
        {
            return CustomisationResult.Refused("You cannot change this.");
        }

        CustomisationSetting? row = await FindBrandingRowAsync(scope, scopeId, cancellationToken).ConfigureAwait(false);
        JsonObject json = row is null ? [] : JsonNode.Parse(row.Value) as JsonObject ?? [];
        if (Guid.TryParse((string?)json["logo"], out Guid old))
        {
            json.Remove("logo");
            await UpsertBrandingAsync(row, scope, scopeId, json, userId, cancellationToken).ConfigureAwait(false);
            await _db.BrandingAssets.Where(a => a.Id == old).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await AuditAsync(AuditActions.CustomisationBrandingUpdate, userId, scope, scopeId, new { scope = ScopeCode(scope), logo = (string?)null }, ipAddress, cancellationToken).ConfigureAwait(false);
        }

        return CustomisationResult.Ok("Logo removed.");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TemplateRow>?> ListTemplatesAsync(Guid userId, CustomisationScope scope, Guid? scopeId, CancellationToken cancellationToken = default)
        => GatedAsync(() => ListTemplatesAsyncCore(userId, scope, scopeId, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<TemplateRow>?> ListTemplatesAsyncCore(Guid userId, CustomisationScope scope, Guid? scopeId, CancellationToken cancellationToken)
    {
        if (!await CanEditAsync(userId, scope, scopeId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        (Guid? appId, Guid? orgId) = await AppAndOrgAsync(scope, scopeId, cancellationToken).ConfigureAwait(false);
        List<(CustomisationScope Scope, Guid? Id)> chain = await ChainAsync(appId, orgId, includeHere: true, cancellationToken).ConfigureAwait(false);
        List<MessageTemplate> rows = await LoadTemplatesAsync(chain, cancellationToken).ConfigureAwait(false);
        List<TemplateRow> result = [];
        foreach (MessageTemplateKind kind in MessageTemplateKinds.All.Where(k => scope == CustomisationScope.Platform || k.PartnerEditable))
        {
            foreach (string language in Languages)
            {
                if (kind.Sms && language == "en-IN")
                {
                    // The registered English text lives in configuration, with the sender's DLT registration.
                    SmsTemplateSettings english = _sms.Templates[SmsKey(kind.Code)];
                    result.Add(new TemplateRow(kind.Code, language, "configuration", null, english.Text, english.Id, kind.Variables));
                    continue;
                }

                (MessageTemplate? found, int level) = Pick(rows, chain, kind, language, sameLanguageOnly: true);
                if (found is not null)
                {
                    result.Add(new TemplateRow(kind.Code, language, level == 0 ? "here" : ScopeCode(chain[level].Scope), found.Subject, found.Body, found.DltTemplateId, kind.Variables));
                }
                else if (!kind.Sms && DefaultTemplates.Find(kind.Code, language) is (string subject, string body))
                {
                    result.Add(new TemplateRow(kind.Code, language, "default", subject, body, null, kind.Variables));
                }
                else
                {
                    result.Add(new TemplateRow(kind.Code, language, "none", null, string.Empty, null, kind.Variables));
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public Task<CustomisationResult> SaveTemplateAsync(Guid userId, CustomisationScope scope, Guid? scopeId, TemplateInput input, string? ipAddress, CancellationToken cancellationToken = default)
        => GatedAsync(() => SaveTemplateAsyncCore(userId, scope, scopeId, input, ipAddress, cancellationToken), cancellationToken);

    private async Task<CustomisationResult> SaveTemplateAsyncCore(Guid userId, CustomisationScope scope, Guid? scopeId, TemplateInput input, string? ipAddress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!await CanEditAsync(userId, scope, scopeId, cancellationToken).ConfigureAwait(false))
        {
            return CustomisationResult.Refused("You cannot change this.");
        }

        MessageTemplateKind? kind = MessageTemplateKinds.Find(input.Kind);
        if (kind is null || !Languages.Contains(input.Language) || (scope != CustomisationScope.Platform && !kind.PartnerEditable))
        {
            return CustomisationResult.Refused("That message cannot be changed here.");
        }

        string? problem;
        if (kind.Sms)
        {
            if (input.Language == "en-IN")
            {
                return CustomisationResult.Refused("The English text messages are registered with the SMS operator and set in configuration.");
            }

            problem = TemplateText.CheckSms(input.Body, input.DltTemplateId, _sms.Templates[SmsKey(kind.Code)].Text);
        }
        else
        {
            problem = TemplateText.CheckEmail(kind, input.Subject, input.Body);
        }

        if (problem is not null)
        {
            return CustomisationResult.Refused(problem);
        }

        MessageTemplate? row = await _db.MessageTemplates.FirstOrDefaultAsync(t => t.Scope == scope && t.ScopeId == scopeId && t.Kind == kind.Code && t.Language == input.Language, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new MessageTemplate { Id = Guid.NewGuid(), Scope = scope, ScopeId = scopeId, Kind = kind.Code, Language = input.Language };
            _db.MessageTemplates.Add(row);
        }

        row.Subject = kind.Sms ? null : input.Subject!.Trim();
        row.Body = input.Body.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        row.DltTemplateId = kind.Sms ? input.DltTemplateId!.Trim() : null;
        row.Version++;
        row.UpdatedBy = userId;
        row.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(AuditActions.CustomisationTemplateUpdate, userId, scope, scopeId, new { scope = ScopeCode(scope), kind = kind.Code, language = input.Language, version = row.Version }, ipAddress, cancellationToken).ConfigureAwait(false);
        return CustomisationResult.Ok("Message saved.");
    }

    /// <inheritdoc />
    public Task<CustomisationResult> ResetTemplateAsync(Guid userId, CustomisationScope scope, Guid? scopeId, string kind, string language, string? ipAddress, CancellationToken cancellationToken = default)
        => GatedAsync(() => ResetTemplateAsyncCore(userId, scope, scopeId, kind, language, ipAddress, cancellationToken), cancellationToken);

    private async Task<CustomisationResult> ResetTemplateAsyncCore(Guid userId, CustomisationScope scope, Guid? scopeId, string kind, string language, string? ipAddress, CancellationToken cancellationToken)
    {
        if (!await CanEditAsync(userId, scope, scopeId, cancellationToken).ConfigureAwait(false))
        {
            return CustomisationResult.Refused("You cannot change this.");
        }

        int removed = await _db.MessageTemplates.Where(t => t.Scope == scope && t.ScopeId == scopeId && t.Kind == kind && t.Language == language).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (removed > 0)
        {
            await AuditAsync(AuditActions.CustomisationTemplateUpdate, userId, scope, scopeId, new { scope = ScopeCode(scope), kind, language, reset = true }, ipAddress, cancellationToken).ConfigureAwait(false);
        }

        return CustomisationResult.Ok("The message is back to the one above it.");
    }

    /// <inheritdoc />
    public EmailPreview Preview(string kind, string language, string? subject, string body, string heading, string? accent)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(heading);
        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["name"] = "Asha",
            ["code"] = "482913",
            ["minutes"] = "10",
            ["application"] = heading,
            ["organisation"] = "City Hospital",
            ["role"] = "Nurse",
            ["link"] = "https://id.sangamid.in/invite/sample",
            ["days"] = "7",
            ["new_email"] = "a•••@example.in",
        };
        string text = TemplateText.Fill(body, values);
        return new EmailPreview(TemplateText.FillSubject(subject ?? string.Empty, values), text, TemplateText.Html(text, values, heading, ValidAccent(accent), NormaliseLanguage(language)));
    }

    /// <inheritdoc />
    public Task<EmailMessage> EmailAsync(string kind, string? language, Guid? appId, Guid? orgId, IReadOnlyDictionary<string, string> values, string toEmail, string toName, CancellationToken cancellationToken = default)
        => GatedAsync(() => EmailAsyncCore(kind, language, appId, orgId, values, toEmail, toName, cancellationToken), cancellationToken);

    private async Task<EmailMessage> EmailAsyncCore(string kind, string? language, Guid? appId, Guid? orgId, IReadOnlyDictionary<string, string> values, string toEmail, string toName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        MessageTemplateKind k = MessageTemplateKinds.Find(kind) ?? throw new ArgumentException("Unknown message kind " + kind, nameof(kind));
        string lang = NormaliseLanguage(language);
        List<(CustomisationScope Scope, Guid? Id)> chain = await ChainAsync(k.PartnerEditable ? appId : null, k.PartnerEditable ? orgId : null, includeHere: true, cancellationToken).ConfigureAwait(false);
        List<MessageTemplate> rows = await LoadTemplatesAsync(chain, cancellationToken).ConfigureAwait(false);

        string? subject = null;
        string? body = null;
        foreach (string candidate in new[] { lang, "en-IN" }.Distinct())
        {
            (MessageTemplate? found, _) = Pick(rows, chain, k, candidate, sameLanguageOnly: true);
            if (found is not null)
            {
                (subject, body, lang) = (found.Subject, found.Body, candidate);
                break;
            }

            if (DefaultTemplates.Find(k.Code, candidate) is (string s, string b))
            {
                (subject, body, lang) = (s, b, candidate);
                break;
            }
        }

        if (body is null)
        {
            throw new InvalidOperationException("No template for " + kind);
        }

        string heading = "Sangam";
        string accent = SangamTeal;
        if (appId is Guid app)
        {
            heading = await _db.Apps.AsNoTracking().Where(a => a.Id == app).Select(a => a.DisplayName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? heading;
            accent = (await ResolveLoginBrandingAsyncCore(appId, orgId, lang, cancellationToken).ConfigureAwait(false)).Accent ?? accent;
        }

        Dictionary<string, string> all = new(values, StringComparer.Ordinal);
        all.TryAdd("application", heading);
        string text = TemplateText.Fill(body, all);
        return new EmailMessage(toEmail, toName, TemplateText.FillSubject(subject ?? string.Empty, all), text, TemplateText.Html(text, all, heading, accent, lang));
    }

    /// <inheritdoc />
    public Task<(string Text, string DltTemplateId)?> SmsAsync(string kind, string language, CancellationToken cancellationToken = default)
        => GatedAsync(() => SmsAsyncCore(kind, language, cancellationToken), cancellationToken);

    private async Task<(string Text, string DltTemplateId)?> SmsAsyncCore(string kind, string language, CancellationToken cancellationToken)
    {
        MessageTemplate? row = await _db.MessageTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Scope == CustomisationScope.Platform && t.ScopeId == null && t.Kind == kind && t.Language == language && t.DltTemplateId != null, cancellationToken).ConfigureAwait(false);
        return row is null ? null : (row.Body, row.DltTemplateId!);
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>The configuration key of an SMS kind.</summary>
    /// <param name="kind">The kind.</param>
    public static string SmsKey(string kind) => kind switch
    {
        MessageTemplateKinds.SmsSignIn => SmsSettings.SignInTemplate,
        MessageTemplateKinds.SmsMobileVerification => SmsSettings.MobileVerificationTemplate,
        MessageTemplateKinds.SmsRegistrationNotice => SmsSettings.RegistrationNoticeTemplate,
        MessageTemplateKinds.SmsResetNotice => SmsSettings.ResetNoticeTemplate,
        MessageTemplateKinds.SmsOperatorAlert => SmsSettings.OperatorAlertTemplate,
        _ => SmsSettings.StepUpTemplate,
    };

    /// <summary>The address the identity server serves a logo at.</summary>
    /// <param name="assetId">The logo.</param>
    /// <param name="sha256">Its hash, so a new logo gets a new address.</param>
    public static string LogoUrl(Guid assetId, string sha256) => $"/branding/logo/{assetId:D}?v={sha256[..12]}";

    /// <summary>WCAG contrast ratio of a colour with white.</summary>
    /// <param name="hex">The colour, <c>#RRGGBB</c>.</param>
    public static double ContrastWithWhite(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        double Channel(int start)
        {
            double c = int.Parse(hex.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        double luminance = (0.2126 * Channel(1)) + (0.7152 * Channel(3)) + (0.0722 * Channel(5));
        return 1.05 / (luminance + 0.05);
    }

    /// <summary>The supported language for a culture name; English otherwise.</summary>
    /// <param name="language">Culture name.</param>
    public static string NormaliseLanguage(string? language)
        => Languages.FirstOrDefault(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase)) ?? "en-IN";

    private static string ValidAccent(string? accent) => accent is not null && IsHexColour(accent) && ContrastWithWhite(accent) >= 4.5 ? accent : SangamTeal;

    private static bool IsHexColour(string value) => value.Length == 7 && value[0] == '#' && value.Skip(1).All(char.IsAsciiHexDigit);

    private static string ScopeCode(CustomisationScope scope) => scope switch
    {
        CustomisationScope.Organisation => "organisation",
        CustomisationScope.App => "application",
        _ => "platform",
    };

    private static void Set(JsonObject json, string property, string? value)
    {
        if (value is null)
        {
            json.Remove(property);
        }
        else
        {
            json[property] = value;
        }
    }

    private static string? NormaliseUrl(string? value, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > 500 || !Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) || uri.Scheme == Uri.UriSchemeMailto))
        {
            error = "Links must be full https:// addresses (or mailto:).";
            return null;
        }

        return trimmed;
    }

    // The level itself first, then the organisations above it, the application, the platform. An organisation is
    // used only when it belongs to the application.
    private async Task<List<(CustomisationScope Scope, Guid? Id)>> ChainAsync(Guid? appId, Guid? orgId, bool includeHere, CancellationToken cancellationToken)
    {
        List<(CustomisationScope, Guid?)> chain = [];
        if (orgId is Guid org && appId is Guid app)
        {
            string? path = await _db.Organisations.AsNoTracking().Where(o => o.Id == org && o.RegisteredViaAppId == app && o.DeletedAt == null)
                .Select(o => o.Path).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (path is not null)
            {
                chain.AddRange(OrganisationPath.Ids(path).Reverse().Select(id => (CustomisationScope.Organisation, (Guid?)id)));
            }
        }

        if (appId is Guid a)
        {
            chain.Add((CustomisationScope.App, a));
        }

        chain.Add((CustomisationScope.Platform, null));
        return includeHere ? chain : [.. chain.Skip(1)];
    }

    private async Task<(Guid? AppId, Guid? OrgId)> AppAndOrgAsync(CustomisationScope scope, Guid? scopeId, CancellationToken cancellationToken)
    {
        if (scope == CustomisationScope.Organisation && scopeId is Guid org)
        {
            Guid app = await _db.Organisations.AsNoTracking().Where(o => o.Id == org).Select(o => o.RegisteredViaAppId).FirstAsync(cancellationToken).ConfigureAwait(false);
            return (app, org);
        }

        return scope == CustomisationScope.App ? (scopeId, null) : (null, null);
    }

    private async Task<List<MessageTemplate>> LoadTemplatesAsync(List<(CustomisationScope Scope, Guid? Id)> chain, CancellationToken cancellationToken)
    {
        List<Guid> ids = [.. chain.Where(c => c.Id is not null).Select(c => c.Id!.Value)];
        return await _db.MessageTemplates.AsNoTracking()
            .Where(t => (t.Scope == CustomisationScope.Platform && t.ScopeId == null) || (t.ScopeId != null && ids.Contains(t.ScopeId.Value)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static (MessageTemplate? Template, int Level) Pick(List<MessageTemplate> rows, List<(CustomisationScope Scope, Guid? Id)> chain, MessageTemplateKind kind, string language, bool sameLanguageOnly)
    {
        for (int level = 0; level < chain.Count; level++)
        {
            (CustomisationScope scope, Guid? id) = chain[level];
            if (scope != CustomisationScope.Platform && !kind.PartnerEditable)
            {
                continue;
            }

            MessageTemplate? found = rows.FirstOrDefault(r => r.Scope == scope && r.ScopeId == id && r.Kind == kind.Code && (!sameLanguageOnly || r.Language == language));
            if (found is not null)
            {
                return (found, level);
            }
        }

        return (null, -1);
    }

    private async Task<JsonObject?> ReadBrandingAsync(CustomisationScope scope, Guid? id, CancellationToken cancellationToken)
    {
        string? value = await _db.Customisations.AsNoTracking().Where(c => c.Scope == scope && c.ScopeId == id && c.Key == BrandingKey)
            .Select(c => c.Value).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : JsonNode.Parse(value) as JsonObject;
    }

    private Task<CustomisationSetting?> FindBrandingRowAsync(CustomisationScope scope, Guid? scopeId, CancellationToken cancellationToken)
        => _db.Customisations.FirstOrDefaultAsync(c => c.Scope == scope && c.ScopeId == scopeId && c.Key == BrandingKey, cancellationToken);

    private async Task UpsertBrandingAsync(CustomisationSetting? row, CustomisationScope scope, Guid? scopeId, JsonObject json, Guid userId, CancellationToken cancellationToken)
    {
        if (row is null)
        {
            row = new CustomisationSetting { Id = Guid.NewGuid(), Scope = scope, ScopeId = scopeId, Key = BrandingKey };
            _db.Customisations.Add(row);
        }

        row.Value = json.ToJsonString();
        row.Version++;
        row.UpdatedBy = userId;
        row.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CanEditAsync(Guid userId, CustomisationScope scope, Guid? scopeId, CancellationToken cancellationToken)
    {
        switch (scope)
        {
            case CustomisationScope.Platform:
                // Ranks are stored as text, so they are compared here rather than in SQL (where "viewer" > "app_manager").
                PlatformRole? rank = await _db.PlatformOperators.AsNoTracking().Where(o => o.UserId == userId && o.RevokedAt == null)
                    .Select(o => (PlatformRole?)o.Role).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                return scopeId is null && rank >= PlatformRole.AppManager;
            case CustomisationScope.App when scopeId is Guid app:
                return await IsAppAdminAsync(userId, app, cancellationToken).ConfigureAwait(false);
            case CustomisationScope.Organisation when scopeId is Guid org:
                Guid? owner = await _db.Organisations.AsNoTracking().Where(o => o.Id == org && o.DeletedAt == null).Select(o => (Guid?)o.RegisteredViaAppId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                return owner is Guid a && await IsAppAdminAsync(userId, a, cancellationToken).ConfigureAwait(false);
            default:
                return false;
        }
    }

    // Sangam's own applications are never a partner's to brand.
    private Task<bool> IsAppAdminAsync(Guid userId, Guid appId, CancellationToken cancellationToken)
        => _db.AppAdmins.AnyAsync(a => a.UserId == userId && a.AppId == appId && a.RevokedAt == null && !a.App!.IsPlatform, cancellationToken);

    private Task AuditAsync(string action, Guid userId, CustomisationScope scope, Guid? scopeId, object metadata, string? ipAddress, CancellationToken cancellationToken)
        => _audit.WriteAsync(
            new AuditEntry(action, scope == CustomisationScope.Platform ? AuditActorType.Admin : AuditActorType.User, userId, scope == CustomisationScope.App ? scopeId : null,
                ScopeCode(scope), scopeId, Metadata: JsonSerializer.Serialize(metadata), IpAddress: ipAddress),
            cancellationToken);

    private async Task<T> GatedAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SangamDbContext db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (db.ConfigureAwait(false))
            {
                _db = db;
                return await work().ConfigureAwait(false);
            }
        }
        finally
        {
            _db = null!;
            _gate.Release();
        }
    }
}
