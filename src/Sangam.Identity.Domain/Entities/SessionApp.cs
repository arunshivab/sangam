namespace Sangam.Identity.Domain.Entities;

/// <summary>An application that received tokens in a Sangam browser session, so it can be told when that session ends (PR-20).</summary>
public sealed class SessionApp
{
    /// <summary>The Sangam session (the OIDC <c>sid</c>).</summary>
    public Guid SessionId { get; set; }

    /// <summary>The application.</summary>
    public Guid AppId { get; set; }

    /// <summary>The person.</summary>
    public Guid UserId { get; set; }

    /// <summary>When the application first received tokens in this session (UTC).</summary>
    public DateTimeOffset FirstSeenAt { get; set; }
}
