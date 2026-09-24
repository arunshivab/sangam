using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Apps;

/// <summary>What the auth screens and the token pipeline need to know about a partner app.</summary>
/// <param name="Id">App id.</param>
/// <param name="ClientId">OAuth client id.</param>
/// <param name="Slug">Short machine name.</param>
/// <param name="DisplayName">Name shown to users.</param>
/// <param name="OwnerCompanyName">Legal operator named on the consent screen.</param>
/// <param name="Description">One-line description.</param>
/// <param name="PrivacyUrl">Privacy policy link.</param>
/// <param name="TermsUrl">Terms link.</param>
/// <param name="BrandColour">Hex brand colour for the chip and tile.</param>
/// <param name="Glyph">One or two characters for the partner glyph.</param>
/// <param name="SignInPolicy">The app's sign-in rule.</param>
/// <param name="ConsentVersion">Consent wording version in force.</param>
/// <param name="Status">Lifecycle state.</param>
public sealed record AppSummary(
    Guid Id,
    string ClientId,
    string Slug,
    string DisplayName,
    string OwnerCompanyName,
    string? Description,
    string? PrivacyUrl,
    string? TermsUrl,
    string BrandColour,
    string Glyph,
    SignInPolicy SignInPolicy,
    string ConsentVersion,
    AppStatus Status);
