namespace Sangam.Identity.Domain.Entities;

/// <summary>A pending passkey ceremony: the options sent to the browser, usable once, for five minutes (PR-14).</summary>
public class PasskeyChallenge
{
    /// <summary>Identifier, returned to the browser with the options.</summary>
    public Guid Id { get; set; }

    /// <summary>The person adding a passkey; empty for a sign-in.</summary>
    public Guid? UserId { get; set; }

    /// <summary>"register" or "assert".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The options as JSON, needed to verify the browser's answer.</summary>
    public string OptionsJson { get; set; } = string.Empty;

    /// <summary>When it was issued.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it stops working.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When it was used.</summary>
    public DateTimeOffset? UsedAt { get; set; }
}
