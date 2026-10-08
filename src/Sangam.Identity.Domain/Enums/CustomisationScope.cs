namespace Sangam.Identity.Domain.Enums;

/// <summary>Where a customisation applies (SGM-209 §2): the most specific level that sets something wins.</summary>
public enum CustomisationScope
{
    /// <summary>Sangam's own default, set in the operator console.</summary>
    Platform = 0,

    /// <summary>One application, set by its partner.</summary>
    App = 1,

    /// <summary>One organisation (tenant) and everything under it.</summary>
    Organisation = 2,
}
