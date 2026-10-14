using System.Security.Cryptography;
using System.Text;

namespace Sangam.Identity.Infrastructure.Tenancy;

/// <summary>
/// rc.12: activation and join codes. Eight characters in two groups (<c>K7Q2-M9XR</c>) from an alphabet without the letters
/// and digits people confuse (no I, L, O, 0 or 1), typed in any case, with or without the dash or spaces.
/// </summary>
public static class StaffCodes
{
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>A new random code.</summary>
    /// <returns>The code, with its dash.</returns>
    public static string New()
    {
        Span<char> chars = stackalloc char[8];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars[..4]) + "-" + new string(chars[4..]);
    }

    /// <summary>The code as typed, reduced to its eight characters (upper case, no dash or spaces).</summary>
    /// <param name="typed">What the person typed.</param>
    /// <returns>The normalised code; empty when it cannot be one.</returns>
    public static string Normalise(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            return string.Empty;
        }

        StringBuilder text = new(8);
        foreach (char c in typed)
        {
            if (char.IsLetterOrDigit(c))
            {
                text.Append(char.ToUpperInvariant(c));
            }
        }

        return text.Length == 8 ? text.ToString() : string.Empty;
    }

    /// <summary>SHA-256 of a normalised code or token, in lower-case hex.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The hash.</returns>
    public static string Hash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>A link token: 32 random bytes, URL-safe.</summary>
    /// <returns>The token.</returns>
    public static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Today in India, the day a start date is counted in.</summary>
    /// <param name="now">The current time.</param>
    /// <returns>The date.</returns>
    public static DateOnly IndiaToday(DateTimeOffset now) => DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
}
