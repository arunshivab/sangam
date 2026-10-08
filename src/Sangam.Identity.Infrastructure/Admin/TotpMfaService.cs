using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Admin;

/// <summary>
/// Authenticator-app second factor, on ASP.NET Core Identity's own TOTP provider and token
/// store — no new tables and no hand-rolled cryptography. The shared secret never leaves the
/// server except in the enrolment URI the user scans once.
/// </summary>
public sealed class TotpMfaService : IMfaService
{
    private const string Issuer = "Sangam";
    private const int RecoveryCodeCount = 10;

    private readonly UserManager<SangamUser> _users;
    private readonly SangamDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly Accounts.SecurityNotices? _notices;

    /// <summary>Initialises the service.</summary>
    /// <param name="users">Identity user manager.</param>
    /// <param name="db">Database, to check operator status.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="notices">Security notices to the person (R7); none when null.</param>
    public TotpMfaService(UserManager<SangamUser> users, SangamDbContext db, IAuditWriter audit, Accounts.SecurityNotices? notices = null)
    {
        _notices = notices;
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <inheritdoc />
    public async Task<bool> IsEnrolledAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        SangamUser? user = await FindAsync(userId).ConfigureAwait(false);
        return user is not null && await _users.GetTwoFactorEnabledAsync(user).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MfaEnrolment> BeginEnrolmentAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        SangamUser user = await FindAsync(userId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The user does not exist.");

        string? key = await _users.GetAuthenticatorKeyAsync(user).ConfigureAwait(false);
        if (string.IsNullOrEmpty(key))
        {
            await _users.ResetAuthenticatorKeyAsync(user).ConfigureAwait(false);
            key = await _users.GetAuthenticatorKeyAsync(user).ConfigureAwait(false);
        }

        string account = user.Email ?? user.UserName ?? userId.ToString("D");
        string uri = string.Create(
            CultureInfo.InvariantCulture,
            $"otpauth://totp/{UrlEncoder.Default.Encode(Issuer)}:{UrlEncoder.Default.Encode(account)}?secret={key}&issuer={UrlEncoder.Default.Encode(Issuer)}&digits=6");

        return new MfaEnrolment(FormatKey(key!), uri, QrCodeSvg(uri));
    }

    /// <inheritdoc />
    public async Task<MfaConfirmation> ConfirmEnrolmentAsync(Guid userId, string code, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        SangamUser? user = await FindAsync(userId).ConfigureAwait(false);
        if (user is null)
        {
            return new MfaConfirmation(false, []);
        }

        bool valid = await _users.VerifyTwoFactorTokenAsync(user, _users.Options.Tokens.AuthenticatorTokenProvider, Normalise(code)).ConfigureAwait(false);
        if (!valid)
        {
            return new MfaConfirmation(false, []);
        }

        await _users.SetTwoFactorEnabledAsync(user, true).ConfigureAwait(false);
        IEnumerable<string>? codes = await _users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount).ConfigureAwait(false);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.UserMfaEnable, AuditActorType.User, userId, TargetType: "user", TargetId: userId, IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        if (_notices is not null)
        {
            await _notices.SendAsync(MessageTemplateKinds.AuthenticatorAddedNotice, user, cancellationToken).ConfigureAwait(false);
        }

        return new MfaConfirmation(true, [.. codes ?? []]);
    }

    /// <inheritdoc />
    public async Task<MfaResult> VerifyAsync(Guid userId, string code, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        SangamUser? user = await FindAsync(userId).ConfigureAwait(false);
        if (user is null)
        {
            return MfaResult.Invalid;
        }

        if (await _users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            return MfaResult.LockedOut;
        }

        // Two different shapes: an authenticator code is six digits (spaces and dashes are typing
        // noise), but a recovery code is stored as "XXXXX-XXXXX" and must keep its dash, or no
        // recovery code could ever match and a lost phone would lock the person out for good.
        bool valid = await _users.VerifyTwoFactorTokenAsync(user, _users.Options.Tokens.AuthenticatorTokenProvider, Normalise(code)).ConfigureAwait(false)
            || (await _users.RedeemTwoFactorRecoveryCodeAsync(user, code.Replace(" ", string.Empty, StringComparison.Ordinal).Trim()).ConfigureAwait(false)).Succeeded;

        if (valid)
        {
            await _users.ResetAccessFailedCountAsync(user).ConfigureAwait(false);
            return MfaResult.Valid;
        }

        // Wrong codes count towards the same lockout as wrong passwords.
        await _users.AccessFailedAsync(user).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.UserMfaFail, AuditActorType.User, userId, TargetType: "user", TargetId: userId, IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);

        return await _users.IsLockedOutAsync(user).ConfigureAwait(false) ? MfaResult.LockedOut : MfaResult.Invalid;
    }

    /// <inheritdoc />
    public async Task<bool> DisableAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        SangamUser? user = await FindAsync(userId).ConfigureAwait(false);
        if (user is null)
        {
            return false;
        }

        // A platform operator may not turn off their second factor; it is the point of the console.
        bool isOperator = await _db.PlatformOperators.AnyAsync(o => o.UserId == userId && o.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (isOperator)
        {
            return false;
        }

        await _users.SetTwoFactorEnabledAsync(user, false).ConfigureAwait(false);
        await _users.ResetAuthenticatorKeyAsync(user).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.UserMfaDisable, AuditActorType.User, userId, TargetType: "user", TargetId: userId, IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        if (_notices is not null)
        {
            await _notices.SendAsync(MessageTemplateKinds.AuthenticatorRemovedNotice, user, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// The enrolment URI as an inline SVG, generated on this server. A hosted QR service would
    /// mean posting the shared secret to a third party, which would defeat the point.
    /// </summary>
    private static string QrCodeSvg(string uri)
    {
        using QRCoder.QRCodeGenerator generator = new();
        using QRCoder.QRCodeData data = generator.CreateQrCode(uri, QRCoder.QRCodeGenerator.ECCLevel.Q);
        using QRCoder.SvgQRCode code = new(data);
        return code.GetGraphic(4, "#15302E", "#FFFDF8", drawQuietZones: true);
    }

    private Task<SangamUser?> FindAsync(Guid userId) => _users.FindByIdAsync(userId.ToString("D"));

    private static string Normalise(string code) => code.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).Trim();

    /// <summary>
    /// Groups the base32 key in fours and shows it in lowercase, so it can be typed by hand when a
    /// camera fails. Lowercase matters: base32 has no 0, 1, 8 or 9, but in capitals a monospace
    /// "O" reads as zero and "B" as eight, and people type the digit. In lowercase o/0, b/8, s/5
    /// and z/2 are visibly different, and authenticator apps accept lowercase and spaces.
    /// </summary>
    private static string FormatKey(string key)
    {
        StringBuilder result = new();
        for (int i = 0; i < key.Length; i += 4)
        {
            result.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');
        }

        return result.ToString().TrimEnd().ToLowerInvariant();
    }
}
