using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Sangam.Identity.Server.Staff;

/// <summary>
/// rc.12: after the right activation code, the person may need to sign in or create their account first. A short-lived,
/// protected ticket in the return address carries "the code was right for this invite" through that, so they do not type
/// the code again, and nobody can make one without the code.
/// </summary>
public static class StaffTicket
{
    /// <summary>How long a ticket lasts: long enough to create an account and verify the e-mail.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(45);

    private const string Purpose = "Sangam.Staff.ActivationTicket.v1";

    /// <summary>Makes a ticket for an invite.</summary>
    /// <param name="protection">Data protection.</param>
    /// <param name="inviteId">The invite the code was right for.</param>
    /// <returns>The ticket.</returns>
    public static string Issue(IDataProtectionProvider protection, Guid inviteId)
    {
        ArgumentNullException.ThrowIfNull(protection);
        return protection.CreateProtector(Purpose).ToTimeLimitedDataProtector().Protect(inviteId.ToString("D", CultureInfo.InvariantCulture), Lifetime);
    }

    /// <summary>The invite a ticket is for, or <see langword="null"/> when it is missing, altered or out of time.</summary>
    /// <param name="protection">Data protection.</param>
    /// <param name="ticket">The ticket.</param>
    /// <returns>The invite.</returns>
    public static Guid? Read(IDataProtectionProvider protection, string? ticket)
    {
        ArgumentNullException.ThrowIfNull(protection);
        if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > 1000)
        {
            return null;
        }

        try
        {
            string text = protection.CreateProtector(Purpose).ToTimeLimitedDataProtector().Unprotect(ticket, out _);
            return Guid.TryParse(text, out Guid id) ? id : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The activation token and ticket in a return address such as <c>/staff/abc?ticket=…</c>, if any.</summary>
    /// <param name="returnUrl">The return address.</param>
    /// <returns>The token and ticket.</returns>
    public static (string Token, string Ticket)? FromReturnUrl(string? returnUrl)
    {
        const string Prefix = "/staff/";
        if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rest = returnUrl[Prefix.Length..];
        int query = rest.IndexOf('?', StringComparison.Ordinal);
        if (query <= 0)
        {
            return null;
        }

        string token = rest[..query];
        string? ticket = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(rest[query..]).TryGetValue("ticket", out Microsoft.Extensions.Primitives.StringValues value) ? value.ToString() : null;
        bool tokenOk = token.Length is > 0 and <= 200 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
        return tokenOk && !string.IsNullOrEmpty(ticket) ? (token, ticket) : null;
    }
}
