using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Sms;

namespace Sangam.Identity.Infrastructure.Monitoring;

/// <summary>
/// Sends alerts to the founder (D-H, D-K). Recipients come from <c>Sangam:Alerts:Emails</c> and
/// <c>Sangam:Alerts:Mobiles</c> (comma-separated); when none are set, every active Owner operator's e-mail and
/// verified mobile. E-mail always; SMS when SMS is switched on (until DLT is registered, e-mail only). When the
/// database itself is down — the alert that matters most — the alert still goes out: plain English text, no
/// template lookup, no SMS limits or record, and the audit line is skipped.
/// </summary>
public sealed partial class PlatformAlerts : IPlatformAlerts
{
    private readonly SangamDbContext _db;
    private readonly IEmailSender _email;
    private readonly IMessageTemplates _templates;
    private readonly SmsNoticeSender _sms;
    private readonly IAuditWriter _audit;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PlatformAlerts> _logger;

    /// <summary>Initialises the alerts.</summary>
    /// <param name="db">Database.</param>
    /// <param name="email">E-mail (Anjal).</param>
    /// <param name="templates">Message templates.</param>
    /// <param name="sms">SMS notices (Anjal).</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="configuration">Configuration (<c>Sangam:Alerts</c>).</param>
    /// <param name="logger">Logger.</param>
    public PlatformAlerts(SangamDbContext db, IEmailSender email, IMessageTemplates templates, SmsNoticeSender sms, IAuditWriter audit, IConfiguration configuration, ILogger<PlatformAlerts> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _sms = sms ?? throw new ArgumentNullException(nameof(sms));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task SendAsync(string summary, string details, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(details);
        (IReadOnlyList<string> emails, IReadOnlyList<string> mobiles) = await RecipientsAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["summary"] = summary, ["details"] = details };
        int emailed = 0;
        int texted = 0;
        bool databaseDown = false;
        foreach (string address in emails)
        {
            EmailMessage message;
            try
            {
                message = await _templates.EmailAsync(MessageTemplateKinds.OperatorAlert, "en-IN", null, null, values, address, "Sangam operations", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsDatabaseFailure(ex))
            {
                databaseDown = true;
                message = new EmailMessage(address, "Sangam operations", "Sangam alert: " + summary, summary + "\n\n" + details); // i18n-ignore: alerts to the founder are English (D-H)
            }

            try
            {
                await _email.SendAsync(message, cancellationToken).ConfigureAwait(false);
                emailed++;
            }
            catch (InvalidOperationException)
            {
                // Counted by the sender; an alert must never break the action that raised it.
            }
        }

        foreach (string mobile in mobiles)
        {
            bool sent;
            try
            {
                sent = !databaseDown && await _sms.TrySendAlertAsync(mobile, summary, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsDatabaseFailure(ex))
            {
                databaseDown = true;
                sent = false;
            }

            if (!sent && databaseDown)
            {
                sent = await _sms.TrySendAlertDirectAsync(mobile, summary, cancellationToken).ConfigureAwait(false);
            }

            if (sent)
            {
                texted++;
            }
        }

        LogAlert(summary, emailed, texted);
        try
        {
            await _audit.WriteAsync(
                new AuditEntry(AuditActions.PlatformAlert, AuditActorType.System,
                    Metadata: System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["summary"] = summary, ["emailed"] = emailed, ["texted"] = texted })),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDatabaseFailure(ex))
        {
            // The database is down; the log line above is the record.
        }
    }

    private async Task<(IReadOnlyList<string> Emails, IReadOnlyList<string> Mobiles)> RecipientsAsync(CancellationToken cancellationToken)
    {
        string[] emails = Split(_configuration["Sangam:Alerts:Emails"]);
        string[] mobiles = Split(_configuration["Sangam:Alerts:Mobiles"]);
        if (emails.Length > 0 || mobiles.Length > 0)
        {
            return (emails, mobiles);
        }

        try
        {
            var owners = await _db.PlatformOperators
                .Where(o => o.RevokedAt == null && o.Role == PlatformRole.Owner)
                .Join(_db.Users, o => o.UserId, u => u.Id, (o, u) => new { u.Email, u.PhoneNumber, u.PhoneNumberConfirmed })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return ([.. owners.Where(o => !string.IsNullOrEmpty(o.Email)).Select(o => o.Email!)],
                    [.. owners.Where(o => o.PhoneNumberConfirmed && !string.IsNullOrEmpty(o.PhoneNumber)).Select(o => o.PhoneNumber!)]);
        }
        catch (Exception ex) when (IsDatabaseFailure(ex))
        {
            // Without the database the owners cannot be looked up: Sangam:Alerts:Emails must be set in production.
            return ([], []);
        }
    }

    /// <summary>Whether an exception means the database could not be reached.</summary>
    /// <param name="exception">The exception.</param>
    internal static bool IsDatabaseFailure(Exception exception)
        => exception is Npgsql.NpgsqlException or DbUpdateException or TimeoutException
            || exception.InnerException is Npgsql.NpgsqlException or TimeoutException
            || (exception is InvalidOperationException && exception.Message.Contains("transient", StringComparison.OrdinalIgnoreCase));

    private static string[] Split(string? value)
        => string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [LoggerMessage(EventId = 1401, Level = LogLevel.Warning, Message = "Platform alert: {Summary} (e-mailed {Emailed}, texted {Texted})")]
    private partial void LogAlert(string summary, int emailed, int texted);
}
