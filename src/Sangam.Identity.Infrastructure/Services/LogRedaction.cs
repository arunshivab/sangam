namespace Sangam.Identity.Infrastructure.Services;

/// <summary>Helpers that keep personal data out of logs.</summary>
public static class LogRedaction
{
    /// <summary>Masks an e-mail address: the first character and the domain remain (a***@example.com).</summary>
    /// <param name="email">The address.</param>
    public static string MaskEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        int at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0)
        {
            return "***";
        }

        return string.Concat(email.AsSpan(0, 1), "***", email.AsSpan(at));
    }
}
