namespace Sangam.Identity.Application.Accounts;

/// <summary>One-time code policy. Bound from the <c>Sangam:Otp</c> configuration section.</summary>
public sealed class OtpOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Sangam:Otp";

    /// <summary>How long a code stays valid. Default 10 minutes.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Wrong guesses allowed before the code is void. Default 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Minimum gap between two codes for the same user and purpose. Default 60 seconds.</summary>
    public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Codes allowed per user and purpose per <see cref="RateWindow"/>. Default 5.</summary>
    public int MaxPerWindow { get; set; } = 5;

    /// <summary>The window for <see cref="MaxPerWindow"/>. Default 1 hour.</summary>
    public TimeSpan RateWindow { get; set; } = TimeSpan.FromHours(1);
}
