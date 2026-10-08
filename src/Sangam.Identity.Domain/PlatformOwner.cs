namespace Sangam.Identity.Domain;

/// <summary>
/// Who owns and operates Sangam (founder's decision D-C, 7 October 2026). Sangam started under imagiQa, but all
/// of its resources belong to the founder personally; a new company will be formed later and Sangam transferred
/// to it, at which point these values (and <c>Sangam:Operator</c> in configuration) change.
/// </summary>
public static class PlatformOwner
{
    /// <summary>The owner and operator, as named on screens, consent and the legal pages.</summary>
    public const string Name = "Dr. Arun Shiva Balasubramanian";

    /// <summary>The jurisdiction for the terms of use.</summary>
    public const string Jurisdiction = "Ahmedabad";
}
