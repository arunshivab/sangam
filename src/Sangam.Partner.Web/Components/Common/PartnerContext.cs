using Sangam.Identity.Application.Partners;

namespace Sangam.Partner.Web.Components.Common;

/// <summary>The signed-in administrator, cascaded to every page once the gate is cleared.</summary>
/// <param name="UserId">Their Sangam user id.</param>
/// <param name="Name">Their display name.</param>
/// <param name="Apps">The applications they administer, with their rank over each, as of this visit.</param>
public sealed record PartnerContext(Guid UserId, string Name, IReadOnlyList<PartnerAppRow> Apps);
