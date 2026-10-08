namespace Sangam.Identity.Application.Abstractions;

/// <summary>The client behind the current request or circuit: its address and browser.</summary>
public interface IClientContext
{
    /// <summary>Client IP address, as seen after forwarded headers.</summary>
    string? IpAddress { get; }

    /// <summary>Client user agent.</summary>
    string? UserAgent { get; }
}

/// <summary>
/// Scoped <see cref="IClientContext"/>: set once per HTTP request by middleware, and once per Blazor
/// circuit by the root component, so that every audit entry carries client details (OI-039).
/// </summary>
public sealed class ClientContext : IClientContext
{
    /// <inheritdoc />
    public string? IpAddress { get; private set; }

    /// <inheritdoc />
    public string? UserAgent { get; private set; }

    /// <summary>Records the client details, truncated to the audit column sizes.</summary>
    /// <param name="ipAddress">Client IP address.</param>
    /// <param name="userAgent">Client user agent.</param>
    public void Set(string? ipAddress, string? userAgent)
    {
        IpAddress = Truncate(ipAddress, 45);
        UserAgent = Truncate(userAgent, 500);
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= max ? value : value[..max];
    }
}
