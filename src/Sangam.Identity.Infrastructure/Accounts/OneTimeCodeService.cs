using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Accounts;

/// <summary>
/// Issues and checks six-digit codes. Codes are random (CSPRNG), stored only as a SHA-256 hash,
/// expire, allow a bounded number of guesses, and are rate-limited per user and purpose.
/// Issuing a new code voids the previous live one for the same purpose.
/// </summary>
public sealed class OneTimeCodeService
{
    private readonly SangamDbContext _db;
    private readonly IClock _clock;
    private readonly OtpOptions _options;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="options">Code policy.</param>
    public OneTimeCodeService(SangamDbContext db, IClock clock, OtpOptions options)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Issues a code, or refuses because of the cooldown or the hourly limit.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="purpose">Purpose.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plaintext code (to email) and the issue result. The code is <see langword="null"/> unless <see cref="OtpIssueStatus.Sent"/>.</returns>
    public async Task<(OtpIssueResult Result, string? Code)> IssueAsync(Guid userId, OneTimeCodePurpose purpose, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset windowStart = now - _options.RateWindow;

        List<DateTimeOffset> recent = await _db.OneTimeCodes
            .Where(c => c.UserId == userId && c.Purpose == purpose && c.CreatedAt > windowStart)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => c.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (recent.Count > 0 && recent[0] + _options.ResendCooldown > now)
        {
            return (new OtpIssueResult(OtpIssueStatus.TooSoon, recent[0] + _options.ResendCooldown), null);
        }

        if (recent.Count >= _options.MaxPerWindow)
        {
            return (new OtpIssueResult(OtpIssueStatus.RateLimited, recent[^1] + _options.RateWindow), null);
        }

        // Void any live code for the same purpose so only the newest one works.
        await _db.OneTimeCodes
            .Where(c => c.UserId == userId && c.Purpose == purpose && c.ConsumedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAt, now), cancellationToken)
            .ConfigureAwait(false);

        string code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        _db.OneTimeCodes.Add(new OneTimeCode
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Purpose = purpose,
            CodeHash = Hash(userId, purpose, code),
            CreatedAt = now,
            ExpiresAt = now + _options.Lifetime,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return (new OtpIssueResult(OtpIssueStatus.Sent, now + _options.ResendCooldown), code);
    }

    /// <summary>Checks and, when valid, consumes the live code for the user and purpose.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="purpose">Purpose.</param>
    /// <param name="code">The typed code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<OtpVerifyStatus> VerifyAsync(Guid userId, OneTimeCodePurpose purpose, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        DateTimeOffset now = _clock.UtcNow;

        OneTimeCode? live = await _db.OneTimeCodes.AsNoTracking()
            .Where(c => c.UserId == userId && c.Purpose == purpose && c.ConsumedAt == null && c.ExpiresAt > now)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (live is null)
        {
            return OtpVerifyStatus.Expired;
        }

        string normalised = new(code.Where(char.IsDigit).ToArray());
        byte[] expected = Encoding.ASCII.GetBytes(live.CodeHash);
        byte[] actual = Encoding.ASCII.GetBytes(Hash(userId, purpose, normalised));

        // R7 (ASVS V11.1.6): both outcomes are single conditional UPDATEs, so guesses sent in parallel cannot each see the
        // same attempt count. Before, every request read the count, added one and saved: n parallel guesses counted as
        // one, so the limit of MaxAttempts per code did not hold.
        int max = _options.MaxAttempts;
        Guid id = live.Id;
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            await _db.OneTimeCodes
                .Where(c => c.Id == id && c.ConsumedAt == null && c.ExpiresAt > now)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(c => c.FailedAttempts, c => c.FailedAttempts + 1)
                        .SetProperty(c => c.ExpiresAt, c => c.FailedAttempts + 1 >= max ? now : c.ExpiresAt),
                    cancellationToken)
                .ConfigureAwait(false);
            bool spent = await _db.OneTimeCodes.AsNoTracking()
                .AnyAsync(c => c.Id == id && (c.ExpiresAt <= now || c.ConsumedAt != null), cancellationToken)
                .ConfigureAwait(false);
            return spent ? OtpVerifyStatus.Expired : OtpVerifyStatus.Invalid;
        }

        // The right code counts only while the code is live and its attempts are not used up: a correct guess racing the
        // last wrong ones loses.
        int consumed = await _db.OneTimeCodes
            .Where(c => c.Id == id && c.ConsumedAt == null && c.ExpiresAt > now && c.FailedAttempts < max)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), cancellationToken)
            .ConfigureAwait(false);
        return consumed == 1 ? OtpVerifyStatus.Valid : OtpVerifyStatus.Expired;
    }

    /// <summary>When the user may next request a code for <paramref name="purpose"/>, or <see langword="null"/> when they may now.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="purpose">Purpose.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<DateTimeOffset?> NextIssueAllowedAtAsync(Guid userId, OneTimeCodePurpose purpose, CancellationToken cancellationToken = default)
    {
        DateTimeOffset? latest = await _db.OneTimeCodes
            .Where(c => c.UserId == userId && c.Purpose == purpose)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => (DateTimeOffset?)c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (latest is null)
        {
            return null;
        }

        DateTimeOffset allowedAt = latest.Value + _options.ResendCooldown;
        return allowedAt > _clock.UtcNow ? allowedAt : null;
    }

    private static string Hash(Guid userId, OneTimeCodePurpose purpose, string code)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{userId:N}|{purpose}|{code}")));
        return Convert.ToHexStringLower(bytes);
    }
}
