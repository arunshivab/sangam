using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// A partner application registered with Sangam (LiPi HIS, Aran, …). The OAuth client
/// record (secret hash, redirect URIs, permissions) lives in OpenIddict's application table
/// and is linked by <see cref="ClientId"/>; this entity is the business record.
/// </summary>
public sealed class App
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>OAuth <c>client_id</c>; unique; matches the OpenIddict application.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Short machine name used in URLs and logs ("lipi-his"). Unique.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Name shown on the consent screen and in the portal.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The company that operates this app (shown on consent; essential for exit clauses).</summary>
    public string OwnerCompanyName { get; set; } = string.Empty;

    /// <summary>One-line description shown on the consent screen.</summary>
    public string? Description { get; set; }

    /// <summary>Public homepage of the app.</summary>
    public string? HomepageUrl { get; set; }

    /// <summary>The app's privacy policy, linked from the consent screen.</summary>
    public string? PrivacyUrl { get; set; }

    /// <summary>The app's terms of service, linked from the consent screen.</summary>
    public string? TermsUrl { get; set; }

    /// <summary>Whether users must see the consent screen on first sign-in to this app.</summary>
    public bool RequireConsent { get; set; } = true;

    /// <summary>The app's sign-in rule. <see cref="SignInPolicy.Default"/> lets each user's own preference apply.</summary>
    public SignInPolicy SignInPolicy { get; set; } = SignInPolicy.Default;

    /// <summary>Lifecycle state.</summary>
    public AppStatus Status { get; set; } = AppStatus.Active;

    /// <summary>When the app was registered (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When any column last changed (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the app was disabled (UTC).</summary>
    public DateTimeOffset? DisabledAt { get; set; }
}
