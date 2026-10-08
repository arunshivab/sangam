using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Provisioning;

/// <summary>
/// Tells an application's owners, by e-mail, that its SCIM provisioning or a webhook endpoint has stopped working — a
/// delivery gave up after about a day of retries (SGM-217 §4) — with the error and where to look on the partner console.
/// </summary>
public sealed class IntegrationAlerts
{
    private readonly SangamDbContext _db;
    private readonly IEmailSender _email;
    private readonly IMessageTemplates _templates;
    private readonly IConfiguration _configuration;

    /// <summary>Initialises the alerts.</summary>
    /// <param name="db">Database.</param>
    /// <param name="email">E-mail sender.</param>
    /// <param name="templates">Message templates.</param>
    /// <param name="configuration">Reads <c>Sangam:Partner:BaseUrl</c> for the link.</param>
    public IntegrationAlerts(SangamDbContext db, IEmailSender email, IMessageTemplates templates, IConfiguration configuration)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>E-mails every owner of the application; returns how many were told.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="integration">What stopped, for example "SCIM provisioning" or the endpoint address.</param>
    /// <param name="tab">The partner console tab to look at (<c>provisioning</c> or <c>webhooks</c>).</param>
    /// <param name="error">The last error.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> FailingAsync(Guid appId, string integration, string tab, string? error, CancellationToken cancellationToken = default)
    {
        string app = await _db.Apps.AsNoTracking().Where(a => a.Id == appId).Select(a => a.DisplayName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        var owners = await _db.AppAdmins.AsNoTracking()
            .Where(a => a.AppId == appId && a.RevokedAt == null && a.Role == AppAdminRole.Owner && a.User!.Status == UserStatus.Active)
            .Select(a => new { a.User!.Email, a.User.FirstName, a.User.Locale })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        string partner = (_configuration["Sangam:Partner:BaseUrl"] ?? "https://partners.sangamid.in").TrimEnd('/');
        foreach (var owner in owners.Where(o => !string.IsNullOrEmpty(o.Email)))
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal)
            {
                ["name"] = owner.FirstName,
                ["application"] = app,
                ["integration"] = integration,
                ["error"] = error is null ? string.Empty : error.Length > 300 ? error[..300] : error,
                ["link"] = $"{partner}/apps/{appId:D}/{tab}",
            };
            await _email.SendAsync(await _templates.EmailAsync(MessageTemplateKinds.IntegrationFailing, owner.Locale, null, null, values, owner.Email!, owner.FirstName, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        return owners.Count;
    }
}
