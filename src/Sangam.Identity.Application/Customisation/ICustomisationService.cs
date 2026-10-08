using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Customisation;

/// <summary>What the sign-in screens show for an application or organisation (PR-19, SGM-209 §3), already resolved.</summary>
/// <param name="LogoUrl">The logo's address on the identity server, or null.</param>
/// <param name="Accent">The theme accent (<c>#RRGGBB</c>), checked for contrast, or null for Sangam's own.</param>
/// <param name="Welcome">The welcome line in the reader's language (or English), or null.</param>
/// <param name="HelpUrl">Where "Help" goes for this application, or null.</param>
/// <param name="TermsUrl">The application's terms, or null.</param>
/// <param name="PrivacyUrl">The application's privacy notice, or null.</param>
public sealed record LoginBranding(string? LogoUrl, string? Accent, string? Welcome, string? HelpUrl, string? TermsUrl, string? PrivacyUrl)
{
    /// <summary>Nothing customised.</summary>
    public static LoginBranding None { get; } = new(null, null, null, null, null, null);
}

/// <summary>The branding set at one level, for its editor.</summary>
/// <param name="LogoUrl">The logo set at this level, or null.</param>
/// <param name="Accent">The accent set at this level, or null.</param>
/// <param name="Welcome">The welcome line by culture.</param>
/// <param name="HelpUrl">Help link.</param>
/// <param name="TermsUrl">Terms link.</param>
/// <param name="PrivacyUrl">Privacy link.</param>
/// <param name="Version">Changes counter.</param>
public sealed record BrandingSettings(string? LogoUrl, string? Accent, IReadOnlyDictionary<string, string> Welcome, string? HelpUrl, string? TermsUrl, string? PrivacyUrl, int Version);

/// <summary>Branding to save at one level. Blank means "not set here": the level above applies.</summary>
/// <param name="Accent">Accent colour, <c>#RRGGBB</c>.</param>
/// <param name="Welcome">Welcome line by culture (<c>en-IN</c>, <c>hi-IN</c>, <c>ml-IN</c>).</param>
/// <param name="HelpUrl">Help link.</param>
/// <param name="TermsUrl">Terms link.</param>
/// <param name="PrivacyUrl">Privacy link.</param>
public sealed record BrandingInput(string? Accent, IReadOnlyDictionary<string, string?> Welcome, string? HelpUrl, string? TermsUrl, string? PrivacyUrl);

/// <summary>A message template as its editor shows it.</summary>
/// <param name="Kind">Kind of message.</param>
/// <param name="Language">Culture.</param>
/// <param name="Source">Where the text in use comes from: <c>here</c>, <c>organisation</c>, <c>application</c>, <c>platform</c> or <c>default</c>.</param>
/// <param name="Subject">Subject in use (e-mail).</param>
/// <param name="Body">Text in use.</param>
/// <param name="DltTemplateId">DLT template id (SMS).</param>
/// <param name="Variables">The variables it may use.</param>
public sealed record TemplateRow(string Kind, string Language, string Source, string? Subject, string Body, string? DltTemplateId, IReadOnlyList<string> Variables);

/// <summary>A template to save at one level.</summary>
/// <param name="Kind">Kind of message.</param>
/// <param name="Language">Culture.</param>
/// <param name="Subject">Subject (e-mail).</param>
/// <param name="Body">Text.</param>
/// <param name="DltTemplateId">DLT template id (SMS).</param>
public sealed record TemplateInput(string Kind, string Language, string? Subject, string Body, string? DltTemplateId);

/// <summary>An e-mail as it would be sent, with sample values.</summary>
/// <param name="Subject">Subject.</param>
/// <param name="Text">Plain text.</param>
/// <param name="Html">HTML.</param>
public sealed record EmailPreview(string Subject, string Text, string Html);

/// <summary>An uploaded logo, as served.</summary>
/// <param name="ContentType">Media type.</param>
/// <param name="Content">Bytes.</param>
/// <param name="Sha256">Hash, hex.</param>
public sealed record BrandingLogo(string ContentType, byte[] Content, string Sha256);

/// <summary>Outcome of a change.</summary>
/// <param name="Succeeded">Whether it was applied.</param>
/// <param name="Message">What to tell the person.</param>
public sealed record CustomisationResult(bool Succeeded, string? Message)
{
    /// <summary>Applied.</summary>
    /// <param name="message">What to tell the person.</param>
    public static CustomisationResult Ok(string message) => new(true, message);

    /// <summary>Refused.</summary>
    /// <param name="message">Why.</param>
    public static CustomisationResult Refused(string message) => new(false, message);
}

/// <summary>
/// Per-application and per-organisation customisation (PR-19, SGM-209): sign-in page branding and message
/// templates, resolved from the most specific level that sets them. Editing an application or organisation needs an
/// administrator of that application; editing the platform needs an operator of AppManager rank or above.
/// </summary>
public interface ICustomisationService
{
    /// <summary>The branding for a sign-in screen; an organisation counts only when it belongs to the application.</summary>
    Task<LoginBranding> ResolveLoginBrandingAsync(Guid? appId, Guid? orgId, string language, CancellationToken cancellationToken = default);

    /// <summary>An uploaded logo, or null.</summary>
    Task<BrandingLogo?> GetLogoAsync(Guid assetId, CancellationToken cancellationToken = default);

    /// <summary>The branding set at one level, or null when the person may not edit it.</summary>
    Task<BrandingSettings?> GetBrandingAsync(Guid userId, CustomisationScope scope, Guid? scopeId, CancellationToken cancellationToken = default);

    /// <summary>Saves the branding of one level.</summary>
    Task<CustomisationResult> SaveBrandingAsync(Guid userId, CustomisationScope scope, Guid? scopeId, BrandingInput input, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Checks and saves a logo (PNG, or SVG without scripts; at most 200 KB).</summary>
    Task<CustomisationResult> SaveLogoAsync(Guid userId, CustomisationScope scope, Guid? scopeId, string contentType, byte[] content, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Removes the logo of one level.</summary>
    Task<CustomisationResult> RemoveLogoAsync(Guid userId, CustomisationScope scope, Guid? scopeId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Every template this level may set, in every language, with where its text comes from; null when not allowed.</summary>
    Task<IReadOnlyList<TemplateRow>?> ListTemplatesAsync(Guid userId, CustomisationScope scope, Guid? scopeId, CancellationToken cancellationToken = default);

    /// <summary>Checks and saves a template at one level.</summary>
    Task<CustomisationResult> SaveTemplateAsync(Guid userId, CustomisationScope scope, Guid? scopeId, TemplateInput input, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Removes one level's template, so the level above (or Sangam's default) applies again.</summary>
    Task<CustomisationResult> ResetTemplateAsync(Guid userId, CustomisationScope scope, Guid? scopeId, string kind, string language, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>An e-mail template filled with sample values, as text and HTML.</summary>
    EmailPreview Preview(string kind, string language, string? subject, string body, string heading, string? accent);
}

/// <summary>Builds the messages Sangam sends from the templates in force (PR-19).</summary>
public interface IMessageTemplates
{
    /// <summary>
    /// The e-mail of one kind in the person's language: an organisation's or application's own template when the kind
    /// allows one, otherwise the platform's, otherwise Sangam's built-in text. A template in the person's language
    /// beats one in English at any level.
    /// </summary>
    Task<EmailMessage> EmailAsync(string kind, string? language, Guid? appId, Guid? orgId, IReadOnlyDictionary<string, string> values, string toEmail, string toName, CancellationToken cancellationToken = default);

    /// <summary>The platform's SMS template of one kind in a language, with its DLT id; null when only English is registered.</summary>
    Task<(string Text, string DltTemplateId)?> SmsAsync(string kind, string language, CancellationToken cancellationToken = default);
}
