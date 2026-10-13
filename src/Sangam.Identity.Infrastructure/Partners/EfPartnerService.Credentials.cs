using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using Sangam.Identity.Application.Partners;

namespace Sangam.Identity.Infrastructure.Partners;

/// <summary>rc.11 (R5): the application's connection details, shown to its administrators. No secret is ever shown.</summary>
public sealed partial class EfPartnerService
{
    /// <inheritdoc />
    public async Task<PartnerCredentials?> GetCredentialsAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
    {
        if (await CheckRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        var app = await _db.Apps.AsNoTracking()
            .Where(a => a.Id == appId)
            .Select(a => new { a.ClientId, a.SecretSetAt })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (app is null)
        {
            return null;
        }

        var client = await _db.Set<OpenIddictEntityFrameworkCoreApplication>().AsNoTracking()
            .Where(c => c.ClientId == app.ClientId)
            .Select(c => new { c.ClientType, c.RedirectUris, c.PostLogoutRedirectUris })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        bool confidential = string.Equals(client?.ClientType, OpenIddictConstants.ClientTypes.Confidential, StringComparison.Ordinal);
        return new PartnerCredentials(
            app.ClientId,
            confidential,
            confidential ? app.SecretSetAt : null,
            Uris(client?.RedirectUris),
            Uris(client?.PostLogoutRedirectUris));
    }

    /// <summary>OpenIddict keeps an application's addresses as a JSON array.</summary>
    private static List<string> Uris(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
