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
        return new SangamUser(id, name, email, ParseMemberships(principal.Claims));
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

    private static void Add(List<SangamMembership> result, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !Guid.TryParse(Text(element, SangamOrgClaim.Id), out Guid id)
            || Text(element, SangamOrgClaim.Role) is not { Length: > 0 } role
            || Text(element, SangamOrgClaim.Path) is not { Length: > 0 } path)
        {
            return;
        }

        List<string> permissions = [];
        if (element.TryGetProperty(SangamOrgClaim.Permissions, out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            permissions.AddRange(list.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!));
        }

        bool inherits = element.TryGetProperty(SangamOrgClaim.Inherits, out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
        result.Add(new SangamMembership(id, Text(element, SangamOrgClaim.Name) ?? string.Empty, Text(element, SangamOrgClaim.Type) ?? string.Empty, path, role, permissions, inherits));
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
