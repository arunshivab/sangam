namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// rc.15 (ASVS V2.10.1): a client assertion Sangam accepted, kept until ten minutes after it expires (longer than it can be
/// accepted) so the same assertion is never accepted twice (RFC 7523 section 3, item 7). Assertions live five minutes at
/// most, so the table stays small.
/// </summary>
public sealed class ClientAssertionUse
{
    /// <summary>The application's client id.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The assertion's <c>jti</c>.</summary>
    public string TokenId { get; set; } = string.Empty;

    /// <summary>When the assertion expires; the row goes ten minutes after this.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
