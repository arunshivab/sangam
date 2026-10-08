using Sangam.Identity.Application.Admin;

namespace Sangam.Identity.Application.Saml;

/// <summary>
/// SAML service providers in the operator console (PR-22, SGM-215 §4): registered and changed by an AppManager (or
/// above), read by any operator. Each is also an application, so consent, status and the audit log apply to it.
/// </summary>
public interface ISamlAdminService
{
    /// <summary>The service providers, or <see langword="null"/> for a non-operator.</summary>
    /// <param name="operatorUserId">The operator.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<SamlSpView>?> ListAsync(Guid operatorUserId, CancellationToken cancellationToken = default);

    /// <summary>Registers a service provider, or changes one (when <see cref="SamlSpInput.Id"/> is set).</summary>
    /// <param name="operatorUserId">The operator.</param>
    /// <param name="input">The settings.</param>
    /// <param name="ipAddress">Operator's IP address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AdminResult> SaveAsync(Guid operatorUserId, SamlSpInput input, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Reads an SP's metadata into settings to review before saving; <see langword="null"/> with the reason when it cannot.</summary>
    /// <param name="metadataXml">The SP's metadata.</param>
    (SamlSpInput? Input, string? Problem) FromMetadata(string metadataXml);
}

/// <summary>A service provider's settings.</summary>
/// <param name="Id">The provider, when changing one.</param>
/// <param name="DisplayName">Its name, as people see it.</param>
/// <param name="OwnerCompanyName">Who runs it.</param>
/// <param name="EntityId">Its entity id.</param>
/// <param name="AcsUrls">Its assertion consumer addresses, one per line, default first.</param>
/// <param name="SloUrl">Its logout address.</param>
/// <param name="SigningCertificate">Its signing certificate (PEM).</param>
/// <param name="EncryptionCertificate">Its encryption certificate (PEM).</param>
/// <param name="NameIdFormat"><c>persistent</c> or <c>email</c>.</param>
/// <param name="Attributes">Released attributes.</param>
/// <param name="RequireSignedRequests">Whether its requests must be signed.</param>
/// <param name="AllowIdpInitiated">Whether sign-in may start at Sangam.</param>
/// <param name="DefaultRelayState">Where an IdP-initiated sign-in lands.</param>
public sealed record SamlSpInput(Guid? Id, string DisplayName, string OwnerCompanyName, string EntityId, string AcsUrls, string? SloUrl, string? SigningCertificate, string? EncryptionCertificate, string NameIdFormat, IReadOnlyList<string> Attributes, bool RequireSignedRequests, bool AllowIdpInitiated, string? DefaultRelayState);

/// <summary>A service provider as listed.</summary>
/// <param name="Id">Row id.</param>
/// <param name="AppId">Its application.</param>
/// <param name="Input">Its settings.</param>
/// <param name="Active">Whether its application is active.</param>
/// <param name="EncryptsAssertions">Whether assertions are encrypted for it.</param>
public sealed record SamlSpView(Guid Id, Guid AppId, SamlSpInput Input, bool Active, bool EncryptsAssertions);

/// <summary>The attributes a service provider may be given (SGM-215 §3).</summary>
public static class SamlAttributes
{
    /// <summary>Every attribute, in the order the console shows them.</summary>
    public static IReadOnlyList<string> All { get; } = ["name", "given_name", "family_name", "email", "roles", "orgs"];
}
