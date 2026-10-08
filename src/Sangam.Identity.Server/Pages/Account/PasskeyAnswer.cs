namespace Sangam.Identity.Server.Pages.Account;

/// <summary>The browser's answer to a passkey ceremony (PR-14).</summary>
public sealed class PasskeyAnswer
{
    /// <summary>The ceremony.</summary>
    public Guid ChallengeId { get; set; }

    /// <summary>The PublicKeyCredential, as JSON.</summary>
    public string Credential { get; set; } = string.Empty;

    /// <summary>The person's name for a new passkey.</summary>
    public string? Name { get; set; }
}
