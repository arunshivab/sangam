using Sangam.Identity.Application.Partners;

namespace Sangam.Partner.Web.Components.Common;

/// <summary>The signed-in administrator, cascaded to every page once the gate is cleared.</summary>
/// <param name="UserId">Their Sangam user id.</param>
/// <param name="Name">Their display name.</param>
/// <param name="Apps">The applications they administer, with their rank over each, as of this visit.</param>
public sealed record PartnerContext(Guid UserId, string Name, IReadOnlyList<PartnerAppRow> Apps)
{
    /// <summary>How strongly this session was signed in (rc.10): 2 two-step sign-in, 3 a passkey.</summary>
    public int SignInLevel { get; init; }

    /// <summary>When the session ends at the latest; <see langword="null"/> when the cookie does not say.</summary>
    public DateTimeOffset? SessionEndsAt { get; init; }
}
