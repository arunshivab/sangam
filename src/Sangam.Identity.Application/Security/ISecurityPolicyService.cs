using Sangam.Identity.Domain;

namespace Sangam.Identity.Application.Security;

/// <summary>What applies to one person signing in to one application (PR-16).</summary>
/// <param name="Policy">The combined policy.</param>
/// <param name="IsAdministrator">Whether the person administers the application.</param>
public sealed record PersonPolicy(SecurityPolicy Policy, bool IsAdministrator)
{
    /// <summary>Whether a second factor (an authenticator, or a passkey) is required.</summary>
    public bool RequiresSecondFactor => Policy.RequiresSecondFactor(IsAdministrator);
}

/// <summary>
/// Resolves security policies (PR-16, SGM-209 §7): platform → application → the person's organisations in that
/// application, each with its ancestors. Every level can only make the policy stricter.
/// </summary>
public interface ISecurityPolicyService
{
    /// <summary>The platform's own policy.</summary>
    SecurityPolicy Platform { get; }

    /// <summary>Whether known-breach checking is available on this platform at all.</summary>
    bool BreachCheckAvailable { get; }

    /// <summary>The policy for an application before the person is known (the sign-in page, registration).</summary>
    /// <param name="appId">The application, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SecurityPolicy> ForAppAsync(Guid? appId, CancellationToken cancellationToken = default);

    /// <summary>The policy for a person in an application.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="appId">The application, or <see langword="null"/> for a sign-in to Sangam itself.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PersonPolicy> ForPersonAsync(Guid userId, Guid? appId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The password rules a new password must meet: the strictest over every application and organisation the
    /// person belongs to, because one password serves them all.
    /// </summary>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SecurityPolicy> ForPasswordAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Checks a password against known breaches (PR-16, CAP-024; rc.5, D-J revised): the built-in list of common
/// passwords, then the Pwned Passwords range service by k-anonymity — only five characters of the password's SHA-1
/// leave the server.
/// </summary>
public interface IBreachedPasswordChecker
{
    /// <summary>Whether the check is switched on (rc.5: the built-in list is always there, so on means available).</summary>
    bool Available { get; }

    /// <summary>
    /// <see langword="true"/> if the password is known to be breached, <see langword="false"/> if not, and
    /// <see langword="null"/> when it could not be checked (no service, or the service did not answer).
    /// </summary>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool?> IsBreachedAsync(string password, CancellationToken cancellationToken = default);
}

/// <summary>The state of the breached-password check, for the monitoring page (rc.5, D-J revised).</summary>
/// <param name="Enabled">Whether the check is switched on in settings.</param>
/// <param name="Online">Whether the Pwned Passwords range service is used (otherwise only the built-in list).</param>
/// <param name="FallbackEntries">How many passwords the built-in list holds.</param>
/// <param name="LastOnlineSuccess">When the range service last answered, since this host started.</param>
/// <param name="OnlineFailingSince">Since when the range service has not answered; <see langword="null"/> while it does.</param>
/// <param name="Problem">The last failure, while the service is failing.</param>
public sealed record BreachListStatus(bool Enabled, bool Online, int FallbackEntries, DateTimeOffset? LastOnlineSuccess, DateTimeOffset? OnlineFailingSince, string? Problem);

/// <summary>Reports the state of the breached-password check (rc.5).</summary>
public interface IBreachListStatus
{
    /// <summary>The check's state now.</summary>
    BreachListStatus Status { get; }
}
