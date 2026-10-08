namespace Sangam.Identity.Domain.Enums;

/// <summary>Whether a second factor is required (PR-16, SGM-209 §7). Ordered from weakest to strictest.</summary>
public enum MfaRequirement
{
    /// <summary>Each person decides.</summary>
    Optional = 0,

    /// <summary>Required for the application's administrators.</summary>
    RequiredForAdministrators = 1,

    /// <summary>Required for everyone.</summary>
    Required = 2,
}
