namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// rc.14 (decision of 20 Oct 2026, decision record 0017): the id one application knows a person by. Every application gets
/// its own, unrelated to the person's Sangam id and to every other application's, so applications cannot match people up
/// by id. Random, not derived from a key, so there is no key whose loss or leak exposes them; kept for as long as the
/// account lives, so it never changes for that application. Sangam's own applications (portal and consoles) use the
/// person's Sangam id and have no rows here.
/// </summary>
public sealed class AppSubject
{
    /// <summary>The application.</summary>
    public Guid AppId { get; set; }

    /// <summary>The person (their Sangam id).</summary>
    public Guid UserId { get; set; }

    /// <summary>The id the application knows the person by (the <c>sub</c> of its tokens).</summary>
    public Guid Subject { get; set; }

    /// <summary>When it was first given out (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
