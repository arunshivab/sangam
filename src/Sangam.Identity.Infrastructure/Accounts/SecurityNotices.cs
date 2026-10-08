using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Accounts;

/// <summary>
/// R7 (ASVS V2.2.3, V2.5.5): tells a person by e-mail when something that protects their account changes — the
/// password, the authenticator app, a new passkey — so a change they did not make does not go unnoticed. A notice that
/// cannot be sent is logged and never undoes or blocks the change itself.
/// </summary>
public sealed partial class SecurityNotices
{
    private readonly IEmailSender _email;
    private readonly IMessageTemplates _templates;
    private readonly ILogger<SecurityNotices> _logger;

    /// <summary>Initialises the notices.</summary>
    /// <param name="email">E-mail sender.</param>
    /// <param name="templates">Message templates.</param>
    /// <param name="logger">Logger.</param>
    public SecurityNotices(IEmailSender email, IMessageTemplates templates, ILogger<SecurityNotices> logger)
    {
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Sends one notice of a kind in <see cref="MessageTemplateKinds"/> to the person's address, if they have one.</summary>
    /// <param name="kind">The template kind.</param>
    /// <param name="user">The person.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task SendAsync(string kind, SangamUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return;
        }

        try
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal) { ["name"] = user.FirstName };
            await _email.SendAsync(
                await _templates.EmailAsync(kind, user.Locale, null, null, values, user.Email, user.FirstName, cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNotSent(_logger, kind, user.Id, ex);
        }
    }

    [LoggerMessage(EventId = 7301, Level = LogLevel.Warning, Message = "Security notice {Kind} could not be sent to user {UserId}.")]
    private static partial void LogNotSent(ILogger logger, string kind, Guid userId, Exception exception);
}
