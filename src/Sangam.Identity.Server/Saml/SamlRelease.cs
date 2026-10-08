using System.Text.Json;
using Microsoft.Extensions.Localization;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Saml;

namespace Sangam.Identity.Server.Saml;

/// <summary>
/// What a SAML service provider is given about a person, in one place (V-14): the assertion's attributes and the
/// consent screen's "will be shared" list are both built from <see cref="Release"/>, so the screen can never name
/// something the assertion does not carry, or leave out something it does.
/// </summary>
public static class SamlRelease
{
    /// <summary>The attributes released to a service provider, each with the setting it comes from.</summary>
    /// <param name="provider">The service provider.</param>
    /// <param name="user">The person.</param>
    /// <param name="orgs">The person's organisations and roles in the provider's application.</param>
    public static IReadOnlyList<SamlReleased> Release(SamlServiceProvider provider, UserSummary user, IReadOnlyList<OrgClaim> orgs)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(orgs);
        HashSet<string> released = [.. provider.Attributes.Split(',', StringSplitOptions.RemoveEmptyEntries)];
        List<SamlReleased> attributes = [];
        if (released.Contains("name"))
        {
            attributes.Add(new("name", new("urn:oid:2.16.840.1.113730.3.1.241", "displayName", [user.DisplayName])));
        }

        if (released.Contains("given_name"))
        {
            attributes.Add(new("given_name", new("urn:oid:2.5.4.42", "givenName", [user.FirstName])));
        }

        if (released.Contains("family_name"))
        {
            attributes.Add(new("family_name", new("urn:oid:2.5.4.4", "sn", [user.LastName])));
        }

        if (released.Contains("email"))
        {
            attributes.Add(new("email", new("urn:oid:0.9.2342.19200300.100.1.3", "mail", [user.Email])));
        }

        if (released.Contains("roles") && orgs.Count > 0)
        {
            attributes.Add(new("roles", new("urn:sangam:attribute:roles", "roles", [.. orgs.Select(o => o.Role).Distinct(StringComparer.Ordinal)])));
        }

        if (released.Contains("orgs") && orgs.Count > 0)
        {
            attributes.Add(new("orgs", new("urn:sangam:attribute:orgs", "orgs", [.. orgs.Select(o => JsonSerializer.Serialize(new Dictionary<string, string> { ["id"] = o.Id.ToString("D"), ["name"] = o.Name, ["role"] = o.Role }))])));
        }

        return attributes;
    }

    /// <summary>
    /// The consent screen's "will be shared" rows for a service provider: exactly the released attributes with their real
    /// values, plus the e-mail address when it is the NameID.
    /// </summary>
    /// <param name="text">The text catalogue.</param>
    /// <param name="provider">The service provider.</param>
    /// <param name="user">The person.</param>
    /// <param name="orgs">The person's organisations and roles in the provider's application.</param>
    public static IReadOnlyList<(string Label, string Value)> Describe(IStringLocalizer text, SamlServiceProvider provider, UserSummary user, IReadOnlyList<OrgClaim> orgs)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(provider);
#pragma warning disable IDE1006 // Named L, as on every page, so the i18n lint finds the keys below.
        IStringLocalizer L = text;
#pragma warning restore IDE1006
        List<(string, string)> rows = [];
        IReadOnlyList<SamlReleased> released = Release(provider, user, orgs);
        foreach (SamlReleased attribute in released)
        {
            string label = attribute.Key switch
            {
                "name" => L["Your name"],
                "given_name" => L["First name"],
                "family_name" => L["Last name"],
                "email" => L["Email address"],
                "roles" => L["Roles"],
                _ => L["Organisations and roles"],
            };
            string value = attribute.Key == "orgs"
                ? string.Join(", ", orgs.Select(o => o.Name + " (" + o.Role + ")"))
                : string.Join(", ", attribute.Claim.Values);
            rows.Add((label, value));
        }

        if (provider.NameIdFormat == "email" && !released.Any(a => a.Key == "email"))
        {
            rows.Add((L["Email address"], user.Email));
        }

        // Roles and organisations are asked for even before the person has any: say so rather than show nothing.
        HashSet<string> configured = [.. provider.Attributes.Split(',', StringSplitOptions.RemoveEmptyEntries)];
        if (configured.Overlaps(["roles", "orgs"]) && orgs.Count == 0)
        {
            rows.Add((L["Organisations and roles"], L["None yet"]));
        }

        return rows;
    }
}

/// <summary>One released attribute and the setting it comes from.</summary>
/// <param name="Key">The setting: name, given_name, family_name, email, roles or orgs.</param>
/// <param name="Claim">The SAML attribute.</param>
public sealed record SamlReleased(string Key, SamlClaim Claim);
