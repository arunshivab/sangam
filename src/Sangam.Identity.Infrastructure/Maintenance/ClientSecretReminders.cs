using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Maintenance;

/// <summary>
/// rc.15 (founder decision of 22 Oct 2026, ASVS V2.10.1): reminds an application's owners, by e-mail, to replace a client
/// secret before it becomes a standing risk. Part of the hourly sweep.
/// <list type="bullet">
/// <item>The secret Sangam issued from its configuration has no end date: once it is twelve months old (or its age is not
/// recorded and the application is), the owners are told, and told again each month until it is replaced or retired.</item>
/// <item>A secret an owner made ends after twelve months by itself: the owners are told once, 30 days before.</item>
/// </list>
/// Each e-mail says how to replace the secret, and suggests signing in with a key instead. Sangam's own applications are
/// left out: their secrets are the deployment's, rotated by the operator (SGM-911). When no owner can be e-mailed, the
/// reminder is tried again at the next sweep.
/// </summary>
public sealed partial class ClientSecretReminders
{
    /// <summary>How old the issued secret may get before its owners are told.</summary>
    public static readonly TimeSpan IssuedSecretAge = ClientSecret.Lifetime;

    /// <summary>How often the owners are told again while the issued secret stays.</summary>
    public static readonly TimeSpan Repeat = TimeSpan.FromDays(30);

    private readonly SangamDbContext _db;
    private readonly IEmailSender _email;
    private readonly IMessageTemplates _templates;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClientSecretReminders> _logger;

    /// <summary>Initialises the reminders.</summary>
    /// <param name="db">Database.</param>
    /// <param name="email">E-mail sender.</param>
    /// <param name="templates">Message templates.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="configuration">Reads <c>Sangam:Partner:BaseUrl</c> for the link.</param>
    /// <param name="logger">Logger.</param>
    public ClientSecretReminders(SangamDbContext db, IEmailSender email, IMessageTemplates templates, IAuditWriter audit, IClock clock, IConfiguration configuration, ILogger<ClientSecretReminders> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Runs one sweep.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many secrets the owners were told about.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        int told = 0;

        // The secret Sangam issued: twelve months old and still live, told monthly. Public clients have no SecretSetAt and
        // no secret; an age not recorded (set before rc.10) counts from when the application was registered.
        DateTimeOffset oldBefore = now - IssuedSecretAge;
        DateTimeOffset repeatBefore = now - Repeat;
        List<string> confidential = await _db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreApplication>().AsNoTracking()
            .Where(c => c.ClientType == OpenIddict.Abstractions.OpenIddictConstants.ClientTypes.Confidential && c.ClientId != null)
            .Select(c => c.ClientId!)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var issued = await _db.Apps.AsNoTracking()
            .Where(a => a.Status == AppStatus.Active && !a.IsPlatform && a.IssuedSecretRetiredAt == null && confidential.Contains(a.ClientId))
            .Where(a => (a.SecretSetAt ?? a.CreatedAt) < oldBefore)
            .Where(a => a.IssuedSecretReminderAt == null || a.IssuedSecretReminderAt < repeatBefore)
            .Select(a => new { a.Id, Since = a.SecretSetAt ?? a.CreatedAt, Before = a.IssuedSecretReminderAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var app in issued)
        {
            int marked = await _db.Apps.Where(a => a.Id == app.Id && (a.IssuedSecretReminderAt == null || a.IssuedSecretReminderAt < repeatBefore))
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.IssuedSecretReminderAt, now), cancellationToken).ConfigureAwait(false);
            if (marked == 0)
            {
                continue;
            }

            if (await TellOwnersAsync(app.Id, null, app.Since, null, cancellationToken).ConfigureAwait(false))
            {
                told++;
            }
            else
            {
                // No owner could be e-mailed (the mail service is down, say): try again at the next sweep.
                await _db.Apps.Where(a => a.Id == app.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.IssuedSecretReminderAt, app.Before), cancellationToken).ConfigureAwait(false);
            }
        }

        // A secret an owner made: ends within 30 days, told once.
        DateTimeOffset soon = now + ClientSecret.Warning;
        var ending = await _db.ClientSecrets.AsNoTracking()
            .Where(s => s.RevokedAt == null && s.ExpiresAt > now && s.ExpiresAt <= soon && s.ReminderSentAt == null)
            .Where(s => _db.Apps.Any(a => a.Id == s.AppId && a.Status == AppStatus.Active && !a.IsPlatform))
            .Select(s => new { s.Id, s.AppId, s.Hint, s.ExpiresAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var secret in ending)
        {
            int marked = await _db.ClientSecrets.Where(s => s.Id == secret.Id && s.ReminderSentAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.ReminderSentAt, now), cancellationToken).ConfigureAwait(false);
            if (marked == 0)
            {
                continue;
            }

            if (await TellOwnersAsync(secret.AppId, secret.Hint, null, secret.ExpiresAt, cancellationToken).ConfigureAwait(false))
            {
                told++;
            }
            else
            {
                await _db.ClientSecrets.Where(s => s.Id == secret.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.ReminderSentAt, (DateTimeOffset?)null), cancellationToken).ConfigureAwait(false);
            }
        }

        return told;
    }

    /// <summary>E-mails the owners; false when there were owners and none could be e-mailed, so the reminder is tried again.</summary>
    private async Task<bool> TellOwnersAsync(Guid appId, string? hint, DateTimeOffset? since, DateTimeOffset? ends, CancellationToken cancellationToken)
    {
        string app = await _db.Apps.AsNoTracking().Where(a => a.Id == appId).Select(a => a.DisplayName).FirstAsync(cancellationToken).ConfigureAwait(false);
        var owners = await _db.AppAdmins.AsNoTracking()
            .Where(a => a.AppId == appId && a.RevokedAt == null && a.Role == AppAdminRole.Owner && a.User!.Status == UserStatus.Active)
            .Select(a => new { a.UserId, a.User!.Email, a.User.FirstName, a.User.Locale })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        string partner = (_configuration["Sangam:Partner:BaseUrl"] ?? "https://partners.sangamid.in").TrimEnd('/');
        int sent = 0;
        foreach (var owner in owners.Where(o => !string.IsNullOrEmpty(o.Email)))
        {
            (string secret, string date) = Phrases(owner.Locale, hint, ends);
            Dictionary<string, string> values = new(StringComparer.Ordinal)
            {
                ["name"] = owner.FirstName,
                ["application"] = app,
                ["secret"] = secret,
                ["date"] = date,
                ["link"] = $"{partner}/apps/{appId:D}/credentials",
            };
            try
            {
                await _email.SendAsync(await _templates.EmailAsync(MessageTemplateKinds.ClientSecretAgeing, owner.Locale, null, null, values, owner.Email!, owner.FirstName, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogNotSent(appId, owner.UserId, ex);
            }
        }

        if (sent == 0 && owners.Count > 0)
        {
            return false;
        }

        Dictionary<string, object?> metadata = new(StringComparer.Ordinal)
        {
            ["secret"] = hint ?? "issued",
            ["owners"] = sent,
        };
        if (since is DateTimeOffset set)
        {
            metadata["set_at"] = set.ToString("O", CultureInfo.InvariantCulture);
        }

        if (ends is DateTimeOffset end)
        {
            metadata["expires_at"] = end.ToString("O", CultureInfo.InvariantCulture);
        }

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AppSecretReminder, AuditActorType.System, ActorAppId: appId, TargetType: "app", TargetId: appId, Metadata: JsonSerializer.Serialize(metadata)),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The two phrases the template puts together, in the owner's language.</summary>
    private static (string Secret, string Date) Phrases(string? locale, string? hint, DateTimeOffset? ends)
    {
        string day = ends is DateTimeOffset end ? end.ToOffset(Sangam.Shared.IndiaTime.Offset).ToString("d MMMM yyyy", CultureInfo.InvariantCulture) : string.Empty;
        return locale switch
        {
            "hi-IN" => hint is null
                ? ("Sangam द्वारा जारी क्लाइंट सीक्रेट", "बारह महीने से अधिक पुराना है")
                : ($"क्लाइंट सीक्रेट {hint}", $"{day} को समाप्त हो जाएगा"),
            "ml-IN" => hint is null
                ? ("Sangam നൽകിയ ക്ലയന്റ് സീക്രട്ട്", "പന്ത്രണ്ട് മാസത്തിലധികം പഴക്കമുള്ളതാണ്")
                : ($"ക്ലയന്റ് സീക്രട്ട് {hint}", $"{day}-ന് അവസാനിക്കും"),
            _ => hint is null
                ? ("The client secret issued by Sangam", "is over twelve months old")
                : ($"The client secret {hint}", $"stops working on {day}"),
        };
    }

    [LoggerMessage(EventId = 7312, Level = LogLevel.Warning, Message = "Client secret reminder for application {AppId} could not be sent to owner {UserId}.")]
    private partial void LogNotSent(Guid appId, Guid userId, Exception exception);
}
