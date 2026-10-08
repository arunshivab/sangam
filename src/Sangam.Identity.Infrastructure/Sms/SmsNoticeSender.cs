using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Sms;

/// <summary>
/// Texts notices that carry no code: to an account's mobile (D-L: "someone tried to register with your number";
/// D-K: "support was asked to reset your two-step sign-in") and alerts to operators (D-H). The same rules as codes apply: SMS switched on, the country allowed, and the per-number and per-IP
/// hourly limits. Returns whether the provider accepted it, so the caller can fall back to e-mail.
/// </summary>
public sealed class SmsNoticeSender
{
    private static readonly TimeSpan LimitWindow = TimeSpan.FromHours(1);

    private readonly SangamDbContext _db;
    private readonly ISmsSender _sender;
    private readonly SmsSettings _settings;
    private readonly IMessageTemplates _templates;
    private readonly IClock _clock;

    /// <summary>Initialises the sender.</summary>
    /// <param name="db">Database.</param>
    /// <param name="sender">The SMS gateway (Anjal, D-M).</param>
    /// <param name="settings">SMS settings.</param>
    /// <param name="templates">Message templates, for the person's language.</param>
    /// <param name="clock">Clock.</param>
    public SmsNoticeSender(SangamDbContext db, ISmsSender sender, SmsSettings settings, IMessageTemplates templates, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Texts "someone tried to register with this number" to <paramref name="user"/>'s mobile, when the rules allow (D-L).</summary>
    /// <param name="user">The account.</param>
    /// <param name="ipAddress">The requester's IP address, for the per-IP limit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the gateway accepted the message.</returns>
    public Task<bool> TrySendRegistrationNoticeAsync(SangamUser user, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        return TrySendAsync(user.PhoneNumber, user.Id, user.Locale, SmsSettings.RegistrationNoticeTemplate, MessageTemplateKinds.SmsRegistrationNotice, [], ipAddress, cancellationToken);
    }

    /// <summary>Texts "support was asked to reset your two-step sign-in" to <paramref name="user"/>'s mobile (D-K).</summary>
    /// <param name="user">The account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the gateway accepted the message.</returns>
    public Task<bool> TrySendResetNoticeAsync(SangamUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        return TrySendAsync(user.PhoneNumber, user.Id, user.Locale, SmsSettings.ResetNoticeTemplate, MessageTemplateKinds.SmsResetNotice, [], null, cancellationToken);
    }

    /// <summary>Texts a short alert to an operator's number (D-H, D-K).</summary>
    /// <param name="number">The number in E.164 form.</param>
    /// <param name="summary">What happened, at most 30 characters (DLT's limit for a variable).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the gateway accepted the message.</returns>
    public Task<bool> TrySendAlertAsync(string number, string summary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(number);
        ArgumentNullException.ThrowIfNull(summary);
        string variable = summary.ReplaceLineEndings(" ");
        variable = variable.Length > DltTemplate.MaxVariableLength ? variable[..DltTemplate.MaxVariableLength] : variable;
        return TrySendAsync(number, null, "en-IN", SmsSettings.OperatorAlertTemplate, MessageTemplateKinds.SmsOperatorAlert, [variable], null, cancellationToken);
    }

    private async Task<bool> TrySendAsync(string? number, Guid? userId, string? language, string templateKey, string kind, string[] variables, string? ipAddress, CancellationToken cancellationToken)
    {
        if (!_settings.Enabled || string.IsNullOrEmpty(number) || !_settings.CountryCodes.Any(c => number.StartsWith(c, StringComparison.Ordinal)))
        {
            return false;
        }

        if (!_settings.Templates.TryGetValue(templateKey, out SmsTemplateSettings? template))
        {
            return false;
        }

        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset windowStart = now - LimitWindow;
        string toHash = SmsNumbers.Hash(_settings.EffectiveHashKey, number);
        string? ipHash = string.IsNullOrEmpty(ipAddress) ? null : SmsNumbers.Hash(_settings.EffectiveHashKey, ipAddress);
        int toNumber = await _db.SmsMessages.CountAsync(m => m.ToHash == toHash && m.CreatedAt > windowStart, cancellationToken).ConfigureAwait(false);
        int fromIp = ipHash is null ? 0 : await _db.SmsMessages.CountAsync(m => m.IpHash == ipHash && m.CreatedAt > windowStart, cancellationToken).ConfigureAwait(false);
        if (toNumber >= _settings.PerNumberPerHour || fromIp >= _settings.PerIpPerHour)
        {
            return false;
        }

        (string text, string dltId) = (template.Text, template.Id ?? string.Empty);
        if (!string.IsNullOrEmpty(language) && await _templates.SmsAsync(kind, language, cancellationToken).ConfigureAwait(false) is (string localText, string localId))
        {
            (text, dltId) = (localText, localId);
        }

        SmsSendResult result = await _sender.SendAsync(new OutgoingSms(number, templateKey, dltId, _settings.SenderHeader, DltTemplate.Render(text, variables)), cancellationToken).ConfigureAwait(false);
        _db.SmsMessages.Add(new SmsMessage
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ToHash = toHash,
            IpHash = ipHash,
            Template = templateKey,
            Provider = result.Provider,
            ProviderMessageId = result.ProviderMessageId,
            Status = result.Accepted ? SmsStatus.Sent : SmsStatus.Failed,
            Error = result.Accepted ? null : result.Error is { Length: > 200 } e ? e[..200] : result.Error,
            CreatedAt = now,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return result.Accepted;
    }
}
