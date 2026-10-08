namespace Sangam.Identity.Domain.Entities;

/// <summary>A pending change of e-mail address, confirmed by a code sent to the new address (OI-022).</summary>
public class EmailChangeRequest
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The person changing their address.</summary>
    public Guid UserId { get; set; }

    /// <summary>The new address, as typed.</summary>
    public string NewEmail { get; set; } = string.Empty;

    /// <summary>The new address, normalised for uniqueness checks.</summary>
    public string NormalizedNewEmail { get; set; } = string.Empty;

    /// <summary>When the request was made.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the change was confirmed.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>When the request was cancelled or replaced.</summary>
    public DateTimeOffset? CancelledAt { get; set; }
}
