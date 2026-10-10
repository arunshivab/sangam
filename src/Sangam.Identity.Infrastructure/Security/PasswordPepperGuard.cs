using Microsoft.Extensions.Configuration;

namespace Sangam.Identity.Infrastructure.Security;

/// <summary>
/// Start-up rule for the password pepper (rc.5, ASVS V2.4.5): outside Development and Testing every host refuses to
/// start without a pepper of at least 32 random bytes, because passwords would otherwise be hashed without it.
/// </summary>
public static class PasswordPepperGuard
{
    /// <summary>Returns why the host must not start, or <see langword="null"/> when the pepper is in place.</summary>
    /// <param name="environmentName">The host environment name.</param>
    /// <param name="configuration">Configuration.</param>
    public static string? Validate(string environmentName, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
            || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Decode(configuration[Argon2idOptions.PepperKey]) is null
            ? $"The password pepper ({Argon2idOptions.PepperKey}, the secret file Sangam__PasswordHashing__Pepper) is missing, or shorter than {Argon2idOptions.MinimumPepperBytes} bytes, in the '{environmentName}' environment. See docs/go-live-checklist.md. Refusing to start."
            : null;
    }

    /// <summary>Decodes a pepper from base64; <see langword="null"/> when it is missing, malformed or too short.</summary>
    /// <param name="value">The configured value.</param>
    public static byte[]? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(value.Trim());
            return bytes.Length >= Argon2idOptions.MinimumPepperBytes ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
