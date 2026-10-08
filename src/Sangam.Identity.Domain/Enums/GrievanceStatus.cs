namespace Sangam.Identity.Domain.Enums;

/// <summary>Where a <see cref="Entities.Grievance"/> stands (D-D).</summary>
public enum GrievanceStatus
{
    /// <summary>Logged; not yet acknowledged to the person.</summary>
    Received = 0,

    /// <summary>Acknowledged; being looked into.</summary>
    Acknowledged = 1,

    /// <summary>Answered and closed: the person's request was met.</summary>
    Resolved = 2,

    /// <summary>Answered and closed: the request could not be met, with the reason given.</summary>
    Declined = 3,
}
