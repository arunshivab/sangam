namespace Sangam.Identity.Application.Accounts;

/// <summary>Registration settings under <c>Sangam:Registration</c> (V-09, D-L, D-I).</summary>
public sealed class RegistrationOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Sangam:Registration";

    /// <summary>
    /// Founder's decision D-L (on by default): registering with an address or mobile that already has an account
    /// carries on to the same "check your email" screen as a real registration; nothing is created, and the real
    /// owner is told instead (by e-mail, or by SMS for a mobile number when SMS is on). Off: the form says an
    /// account already exists, which lets anyone test whether someone has one.
    /// </summary>
    public bool ConcealExistingAccounts { get; set; } = true;

    /// <summary>
    /// At most this many "someone tried to register with your address or number" notices to one account in a
    /// rolling 24 hours (D-L), so the form cannot be used to flood an owner. Further attempts are audited only.
    /// </summary>
    public int AttemptNoticesPerDay { get; set; } = 3;

    /// <summary>
    /// Founder's decision D-I (off by default): registration only through an invitation, for the private pilot.
    /// The register screen then explains that Sangam is invitation-only; an invitation link still lets its
    /// recipient register.
    /// </summary>
    public bool InvitationOnly { get; set; }

    /// <summary>
    /// With <see cref="InvitationOnly"/>: addresses that may register without an invitation link, for the pilot's
    /// testers — comma-separated, each a full address or <c>@domain</c> for a whole domain.
    /// </summary>
    public string AllowedEmails { get; set; } = string.Empty;

    /// <summary>Whether <paramref name="email"/> is on <see cref="AllowedEmails"/>.</summary>
    /// <param name="email">The address.</param>
    public bool IsAllowed(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        string address = email.Trim();
        return AllowedEmails.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(entry => entry.StartsWith('@')
                ? address.EndsWith(entry, StringComparison.OrdinalIgnoreCase)
                : string.Equals(address, entry, StringComparison.OrdinalIgnoreCase));
    }
}
