namespace Sangam.Identity.Domain.Entities;

/// <summary>The states of an operator's review of a recovery (rc.6, SGM-914 section 7).</summary>
public static class MfaResetReview
{
    /// <summary>Waiting for an operator.</summary>
    public const string Waiting = "waiting";

    /// <summary>An operator approved it; the cooling-off period runs.</summary>
    public const string Approved = "approved";

    /// <summary>An operator refused it.</summary>
    public const string Refused = "refused";
}
