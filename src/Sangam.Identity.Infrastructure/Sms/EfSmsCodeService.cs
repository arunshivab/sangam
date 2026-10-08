using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Sms;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Accounts;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Sms;

/// <summary>
/// <see cref="ISmsCodeService"/> on the shared one-time-code store (PR-15, SGM-206). Per account, the e-mail
/// rules apply (cooldown, hourly limit, tries, lifetime). On top: a per-number and a per-IP hourly limit and a
/// country allowlist against SMS pumping, keyed hashes instead of numbers, and a daily volume alert.
/// </summary>
public sealed partial class EfSmsCodeService : ISmsCodeService
{
    private static readonly TimeSpan LimitWindow = TimeSpan.FromHours(1);

    private readonly SangamDbContext _db;
    private readonly OneTimeCodeService _codes;
    private readonly IAccountService _accounts;
    private readonly ISmsSender _sender;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly SmsSettings _settings;
    private readonly ILogger<EfSmsCodeService> _logger;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="codes">The one-time-code store.</param>
    /// <param name="accounts">Accounts, for checking codes (and auditing wrong ones) the same way as e-mailed codes.</param>
    /// <param name="sender">The SMS provider (or providers, with failover).</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="settings">SMS settings.</param>
    /// <param name="logger">Logger.</param>
    public EfSmsCodeService(
        SangamDbContext db,
        OneTimeCodeService codes,
        IAccountService accounts,
        ISmsSender sender,
        IAuditWriter audit,
        IClock clock,
        SmsSettings settings,
        ILogger<EfSmsCodeService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _codes = codes ?? throw new ArgumentNullException(nameof(codes));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool Enabled => _settings.Enabled;

    /// <inheritdoc />
    public bool IsCountryAllowed(string? e164)
        => !string.IsNullOrEmpty(e164) && _settings.CountryCodes.Any(code => e164.StartsWith(code, StringComparison.Ordinal));

    /// <inheritdoc />
    public Task<SmsIssueResult> SendMobileVerificationAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default)
        => SendAsync(userId, OneTimeCodePurpose.MobileVerification, SmsSettings.MobileVerificationTemplate, requireVerified: false, ipAddress, cancellationToken);

    /// <inheritdoc />
    public Task<SmsIssueResult> SendSignInCodeAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default)
        => SendAsync(userId, OneTimeCodePurpose.SmsSignIn, SmsSettings.SignInTemplate, requireVerified: true, ipAddress, cancellationToken);

    /// <inheritdoc />
    public async Task<OtpVerifyStatus> VerifyMobileAsync(Guid userId, string code, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        SangamUser? user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        if (user?.PhoneNumber is null)
        {
            return OtpVerifyStatus.Expired;
        }

        // The code proves the number it was sent to. If the number changed since, the code proves nothing.
        string currentHash = SmsNumbers.Hash(_settings.EffectiveHashKey, user.PhoneNumber);
        string? sentTo = await _db.SmsMessages
            .Where(m => m.UserId == userId && m.Template == SmsSettings.MobileVerificationTemplate && m.Status != SmsStatus.Failed)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.ToHash)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (sentTo != currentHash)
        {
            return OtpVerifyStatus.Expired;
        }

        OtpVerifyStatus status = await _accounts.VerifyCodeAsync(userId, OneTimeCodePurpose.MobileVerification, code, ipAddress, cancellationToken).ConfigureAwait(false);
        if (status != OtpVerifyStatus.Valid)
        {
            return status;
        }

        user.PhoneNumberConfirmed = true;
        user.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.UserMobileVerify, AuditActorType.User, userId, TargetType: "user", TargetId: userId, IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        return OtpVerifyStatus.Valid;
    }

    /// <inheritdoc />
    public Task<DateTimeOffset?> NextSendAllowedAtAsync(Guid userId, OneTimeCodePurpose purpose, CancellationToken cancellationToken = default)
        => _codes.NextIssueAllowedAtAsync(userId, purpose, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> RecordDeliveryAsync(SmsDeliveryReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        SmsStatus status = report.Delivered ? SmsStatus.Delivered : SmsStatus.Failed;
        DateTimeOffset? deliveredAt = report.Delivered ? report.At : null;
        int updated = await _db.SmsMessages
            .Where(m => m.Provider == report.Provider && m.ProviderMessageId == report.ProviderMessageId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(m => m.Status, status)
                      .SetProperty(m => m.DeliveredAt, deliveredAt)
                      .SetProperty(m => m.Error, report.Delivered ? null : "reported undelivered"),
                cancellationToken)
            .ConfigureAwait(false);
        return updated > 0;
    }

    private async Task<SmsIssueResult> SendAsync(Guid userId, OneTimeCodePurpose purpose, string templateKey, bool requireVerified, string? ipAddress, CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            return new SmsIssueResult(SmsIssueStatus.Disabled);
        }

        SangamUser? user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.Status != UserStatus.Active || !user.EmailConfirmed)
        {
            return new SmsIssueResult(SmsIssueStatus.NoAccount);
        }

        if (string.IsNullOrEmpty(user.PhoneNumber))
        {
            return new SmsIssueResult(SmsIssueStatus.NoMobile);
        }

        if (requireVerified && !user.PhoneNumberConfirmed)
        {
            return new SmsIssueResult(SmsIssueStatus.MobileNotVerified);
        }

        if (!IsCountryAllowed(user.PhoneNumber))
        {
            return new SmsIssueResult(SmsIssueStatus.CountryNotAllowed);
        }

        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset windowStart = now - LimitWindow;
        string toHash = SmsNumbers.Hash(_settings.EffectiveHashKey, user.PhoneNumber);
        string? ipHash = string.IsNullOrEmpty(ipAddress) ? null : SmsNumbers.Hash(_settings.EffectiveHashKey, ipAddress);

        int toNumber = await _db.SmsMessages.CountAsync(m => m.ToHash == toHash && m.CreatedAt > windowStart, cancellationToken).ConfigureAwait(false);
        int fromIp = ipHash is null ? 0 : await _db.SmsMessages.CountAsync(m => m.IpHash == ipHash && m.CreatedAt > windowStart, cancellationToken).ConfigureAwait(false);
        if (toNumber >= _settings.PerNumberPerHour || fromIp >= _settings.PerIpPerHour)
        {
            await AuditSendAsync(userId, templateKey, "limited", null, ipAddress, cancellationToken).ConfigureAwait(false);
            return new SmsIssueResult(SmsIssueStatus.RateLimited, now + LimitWindow);
        }

        (OtpIssueResult issued, string? code) = await _codes.IssueAsync(userId, purpose, cancellationToken).ConfigureAwait(false);
        if (issued.Status != OtpIssueStatus.Sent || code is null)
        {
            return new SmsIssueResult(issued.Status == OtpIssueStatus.TooSoon ? SmsIssueStatus.TooSoon : SmsIssueStatus.RateLimited, issued.RetryAfter);
        }

        SmsTemplateSettings template = _settings.Templates[templateKey];
        OutgoingSms message = new(user.PhoneNumber, templateKey, template.Id ?? string.Empty, _settings.SenderHeader, DltTemplate.Render(template.Text, code));
        SmsSendResult result = await _sender.SendAsync(message, cancellationToken).ConfigureAwait(false);

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
            Error = result.Accepted ? null : Truncate(result.Error),
            CreatedAt = now,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditSendAsync(userId, templateKey, result.Accepted ? "sent" : "refused", result.Provider, ipAddress, cancellationToken).ConfigureAwait(false);
        await WatchVolumeAsync(now, cancellationToken).ConfigureAwait(false);

        return result.Accepted
            ? new SmsIssueResult(SmsIssueStatus.Sent, issued.RetryAfter)
            : new SmsIssueResult(SmsIssueStatus.ProviderFailed, issued.RetryAfter);
    }

    private Task AuditSendAsync(Guid userId, string templateKey, string outcome, string? provider, string? ipAddress, CancellationToken cancellationToken)
    {
        string metadata = provider is null
            ? $"{{\"template\":\"{templateKey}\",\"outcome\":\"{outcome}\"}}"
            : $"{{\"template\":\"{templateKey}\",\"outcome\":\"{outcome}\",\"provider\":\"{provider}\"}}";
        return _audit.WriteAsync(
            new AuditEntry(AuditActions.UserSmsSend, AuditActorType.User, userId, TargetType: "user", TargetId: userId, Metadata: metadata, IpAddress: ipAddress),
            cancellationToken);
    }

    private async Task WatchVolumeAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_settings.DailyAlertThreshold is not int threshold || threshold <= 0)
        {
            return;
        }

        DateTimeOffset dayStart = new(now.UtcDateTime.Date, TimeSpan.Zero);
        int today = await _db.SmsMessages.CountAsync(m => m.CreatedAt >= dayStart, cancellationToken).ConfigureAwait(false);
        if (today != threshold)
        {
            return;
        }

        LogVolumeAlert(today, threshold);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.SmsVolumeAlert, AuditActorType.System, Metadata: $"{{\"count\":{today},\"threshold\":{threshold}}}"),
            cancellationToken).ConfigureAwait(false);
    }

    private static string? Truncate(string? text) => text is null || text.Length <= 200 ? text : text[..200];

    [LoggerMessage(EventId = 1102, Level = LogLevel.Warning, Message = "SMS volume alert: {Count} messages today reached the threshold of {Threshold}. Check for SMS pumping.")]
    private partial void LogVolumeAlert(int count, int threshold);
}
