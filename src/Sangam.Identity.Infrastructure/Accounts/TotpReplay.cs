namespace Sangam.Identity.Infrastructure.Accounts;

/// <summary>
/// rc.8 (ASVS V2.8.5): set by <see cref="SangamAuthenticatorTokenProvider"/> during one request when a correct
/// authenticator code is refused because it was already used, so the caller can record it as a replay and tell the
/// person, rather than as an ordinary wrong code.
/// </summary>
public sealed class TotpReplay
{
    /// <summary>Whether the last check in this request was a correct code used a second time.</summary>
    public bool Detected { get; set; }
}
