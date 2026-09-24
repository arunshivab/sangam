namespace Sangam.Identity.Domain.Enums;

/// <summary>Self-declared gender, carried to apps as the OIDC <c>gender</c> claim. Stored as lowercase snake text.</summary>
public enum Gender
{
    /// <summary>Female.</summary>
    Female = 0,

    /// <summary>Male.</summary>
    Male = 1,

    /// <summary>Other.</summary>
    Other = 2,

    /// <summary>The user chose not to say.</summary>
    PreferNotToSay = 3,
}
