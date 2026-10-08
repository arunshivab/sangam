namespace Sangam.Identity.Domain.Enums;

/// <summary>Where a <see cref="Entities.SignatureRequest"/> stands.</summary>
public enum SignatureStatus
{
    /// <summary>Waiting for the person.</summary>
    Pending = 0,

    /// <summary>Signed.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "\"Signed\" is the domain word for a given signature, not the integer type.")]
    Signed = 1,

    /// <summary>The person declined.</summary>
    Declined = 2,
}
