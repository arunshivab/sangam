using System.Security.Claims;
using System.Text.Json;
using Sangam.Shared.Constants;

namespace Sangam.Client;

/// <summary>Reads the Sangam person out of the signed-in principal.</summary>
public static class SangamClaimsPrincipalExtensions
{
    /// <summary>The signed-in person, or <see langword="null"/> when nobody is signed in with Sangam.</summary>
    /// <param name="principal">The current principal.</param>
    /// <returns>The person, with their organisations and roles.</returns>
    public static SangamUser? GetSangamUser(this ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true
            || !Guid.TryParse(principal.FindFirst(SangamUser.SubjectClaim)?.Value, out Guid id))
        {
            return null;
        }

        string? email = principal.FindFirst(SangamUser.EmailClaim)?.Value;
        string name = principal.FindFirst(SangamUser.NameClaim)?.Value ?? email ?? string.Empty;
        return new SangamUser(id, name, email, ParseMemberships(principal.Claims))
        {
            CurrentOrganisation = ParseCurrentOrganisation(principal.FindFirst(SangamClaims.Org)?.Value),
            Emergency = ParseEmergency(principal.FindFirst(SangamClaims.Emergency)?.Value),
        };
    }

    /// <summary>rc.12 (M12): reads the <c>sangam_emergency</c> claim; anything malformed is no emergency access.</summary>
    /// <param name="value">The claim's value.</param>
    /// <returns>The emergency access, or <see langword="null"/>.</returns>
    public static SangamEmergency? ParseEmergency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            JsonElement e = document.RootElement;
            return e.ValueKind == JsonValueKind.Object
                && Guid.TryParse(Text(e, "id"), out Guid id)
                && Text(e, "case") is { Length: > 0 } caseRef
                && Guid.TryParse(Text(e, "org"), out Guid org)
                && e.TryGetProperty("until", out JsonElement until) && until.ValueKind == JsonValueKind.Number && until.TryGetInt64(out long seconds)
                && seconds is > 0 and < 253402300800
                ? new SangamEmergency(id, caseRef, org, DateTimeOffset.FromUnixTimeSeconds(seconds))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>rc.12 (A6): reads the <c>sangam_org</c> claim; anything malformed is no organisation.</summary>
    /// <param name="value">The claim's value.</param>
    /// <returns>The organisation, or <see langword="null"/>.</returns>
    public static SangamCurrentOrganisation? ParseCurrentOrganisation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            JsonElement element = document.RootElement;
            if (element.ValueKind != JsonValueKind.Object
                || !Guid.TryParse(Text(element, SangamOrgClaim.Id), out Guid id)
                || Text(element, SangamOrgClaim.Path) is not { Length: > 0 } path)
            {
                return null;
            }

            List<string> roles = Strings(element, SangamOrgClaim.Roles);
            if (roles.Count == 0 && Text(element, SangamOrgClaim.Role) is { Length: > 0 } single)
            {
                roles.Add(single);
            }

            return new SangamCurrentOrganisation(id, Text(element, SangamOrgClaim.Name) ?? string.Empty, Text(element, SangamOrgClaim.Type) ?? string.Empty, path, roles, Strings(element, SangamOrgClaim.Permissions));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads every membership from the <c>sangam_orgs</c> claims. Accepts both shapes a token
    /// handler may produce — one claim per organisation, or one claim holding the whole array —
    /// and skips anything malformed rather than failing the sign-in.
    /// </summary>
    /// <param name="claims">The principal's claims.</param>
    /// <returns>The memberships found.</returns>
    public static IReadOnlyList<SangamMembership> ParseMemberships(IEnumerable<Claim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        List<SangamMembership> result = [];
        foreach (Claim claim in claims.Where(c => c.Type == SangamClaims.Orgs))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(claim.Value);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement element in document.RootElement.EnumerateArray())
                    {
                        Add(result, element);
                    }
                }
                else
                {
                    Add(result, document.RootElement);
                }
            }
            catch (JsonException)
            {
                // A value that is not JSON is not a membership; ignore it.
            }
        }

        return result;
    }

    /// <summary>
    /// rc.12: one organisation entry carries every role held there (<c>roles</c>; older tokens carry only <c>role</c>).
    /// Each role becomes one membership; a place with no role in Sangam (the application keeps roles itself) becomes one
    /// membership with an empty role, so the organisation is still known.
    /// </summary>
    private static void Add(List<SangamMembership> result, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !Guid.TryParse(Text(element, SangamOrgClaim.Id), out Guid id)
            || Text(element, SangamOrgClaim.Path) is not { Length: > 0 } path)
        {
            return;
        }

        List<string> roles = Strings(element, SangamOrgClaim.Roles);
        if (roles.Count == 0 && Text(element, SangamOrgClaim.Role) is { Length: > 0 } single)
        {
            roles.Add(single);
        }

        if (roles.Count == 0)
        {
            roles.Add(string.Empty);
        }

        List<string> permissions = Strings(element, SangamOrgClaim.Permissions);
        bool inherits = element.TryGetProperty(SangamOrgClaim.Inherits, out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
        string name = Text(element, SangamOrgClaim.Name) ?? string.Empty;
        string type = Text(element, SangamOrgClaim.Type) ?? string.Empty;
        foreach (string role in roles.Distinct(StringComparer.Ordinal))
        {
            result.Add(new SangamMembership(id, name, type, path, role, permissions, inherits));
        }
    }

    private static List<string> Strings(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String && p.GetString()!.Length > 0).Select(p => p.GetString()!)]
            : [];

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
