using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Sangam.Identity.Application.Abstractions;

namespace Sangam.Identity.Infrastructure.Services;

/// <summary>
/// Settings for <see cref="SmtpEmailSender"/>, bound from <c>Sangam:Email:Smtp</c>. Anjal’s values
/// (host, port, TLS mode, account) are the founder’s to supply (OI-027); nothing is hard-coded.
/// </summary>
public sealed class SmtpOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Sangam:Email:Smtp";

    /// <summary>SMTP host, for example Anjal’s submission server.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Port: 587 (STARTTLS submission) by default.</summary>
    public int Port { get; set; } = 587;

    /// <summary>StartTls (default), SslOnConnect (port 465) or None (only for a local test server).</summary>
    public string Security { get; set; } = "StartTls";

    /// <summary>Account to authenticate with; none when empty.</summary>
    public string? Username { get; set; }

    /// <summary>Password for <see cref="Username"/>. A secret: never in a settings file in production.</summary>
    public string? Password { get; set; }

    /// <summary>The sender address, on a domain whose SPF, DKIM and DMARC pass (go-live checklist).</summary>
    public string FromAddress { get; set; } = "no-reply@sangamid.in";

    /// <summary>The sender name.</summary>
    public string FromName { get; set; } = "SangamID";

    /// <summary>
    /// Comma-separated recipients (“person@example.com”) or domains (“@example.com”). When set, mail
    /// to anyone else is refused — so staging can never reach real people. Empty in production.
    /// </summary>
    public string? AllowedRecipients { get; set; }

    /// <summary>Timeout for one send, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Sends Sangam’s e-mail by SMTP submission to Anjal (PR-09, D-048, D-089). Logs only a masked
/// recipient and the message id — never the subject or body, which carry one-time codes (OI-038).
/// </summary>
public sealed partial class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;
    private readonly string[] _allowed;

    /// <summary>Initialises the sender.</summary>
    /// <param name="options">SMTP settings.</param>
    /// <param name="logger">Logger.</param>
    public SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _allowed = string.IsNullOrWhiteSpace(_options.AllowedRecipients)
            ? []
            : _options.AllowedRecipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Whether <paramref name="email"/> may receive mail under the allowlist (always, when none is set).</summary>
    /// <param name="email">Recipient.</param>
    public bool IsAllowed(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        if (_allowed.Length == 0)
        {
            return true;
        }

        return _allowed.Any(a => a.StartsWith('@')
            ? email.EndsWith(a, StringComparison.OrdinalIgnoreCase)
            : string.Equals(email, a, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        string masked = LogRedaction.MaskEmail(message.ToEmail);
        if (!IsAllowed(message.ToEmail))
        {
            LogBlocked(masked);
            throw new InvalidOperationException("The recipient is not on the e-mail allowlist (Sangam:Email:Smtp:AllowedRecipients).");
        }

        using MimeMessage mime = new();
        mime.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        mime.To.Add(new MailboxAddress(message.ToName, message.ToEmail));
        mime.Subject = message.Subject;
        BodyBuilder body = new() { TextBody = message.TextBody, HtmlBody = message.HtmlBody };
        mime.Body = body.ToMessageBody();

        using SmtpClient client = new() { Timeout = _options.TimeoutSeconds * 1000 };
        await client.ConnectAsync(_options.Host, _options.Port, ToSocketOptions(_options.Security), cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(_options.Username))
        {
            await client.AuthenticateAsync(_options.Username, _options.Password ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }

        await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        string messageId = mime.MessageId ?? string.Empty;
        LogSent(masked, messageId);
    }

    private static SecureSocketOptions ToSocketOptions(string security) => security switch
    {
        "SslOnConnect" => SecureSocketOptions.SslOnConnect,
        "None" => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls,
    };

    [LoggerMessage(EventId = 1010, Level = LogLevel.Information, Message = "E-mail sent to {To} (message {MessageId}); subject and body are not logged")]
    private partial void LogSent(string to, string messageId);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Warning, Message = "E-mail to {To} refused: not on the recipient allowlist")]
    private partial void LogBlocked(string to);
}
