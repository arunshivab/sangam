namespace Sangam.Partner.Web;

/// <summary>R7: how many evidence packs one administrator may build in a window.</summary>
internal static class EvidenceLimit
{
    /// <summary>The rate-limit policy name.</summary>
    public const string PolicyName = "evidence";

    /// <summary>Packs per window.</summary>
    public const int PerWindow = 6;

    /// <summary>The window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
}
