namespace Sangam.Identity.Application.Evidence;

/// <summary>
/// PR-32 (CAP-110, SGM-108): the evidence a connected application's auditor asks for about the controls Sangam runs on
/// its behalf — who may sign in and as what, how sign-in is protected, who changed access and when, and the audit trail
/// with its integrity check — gathered into one zip with a manifest of SHA-256 hashes.
/// </summary>
public interface IEvidencePackService
{
    /// <summary>
    /// Builds the pack for an application over a period, or returns <see langword="null"/> when the user does not
    /// administer it (Sangam's own clients are never a partner's). The download is audited (<c>evidence.export</c>).
    /// </summary>
    /// <param name="userId">The administrator asking.</param>
    /// <param name="appId">The application.</param>
    /// <param name="from">First day of the period (India time).</param>
    /// <param name="to">Last day of the period (India time), at most 366 days after <paramref name="from"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<EvidencePack?> BuildAsync(Guid userId, Guid appId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

/// <summary>A built evidence pack.</summary>
/// <param name="FileName">The zip's file name.</param>
/// <param name="Content">The zip.</param>
/// <param name="Sha256">The zip's SHA-256, lower-case hex; recorded in the audit log.</param>
/// <param name="Files">The files in it, with their SHA-256.</param>
public sealed record EvidencePack(string FileName, byte[] Content, string Sha256, IReadOnlyList<EvidenceFile> Files);

/// <summary>One file in a pack.</summary>
/// <param name="Name">Its name in the zip.</param>
/// <param name="Bytes">Its size.</param>
/// <param name="Sha256">Its SHA-256, lower-case hex.</param>
/// <param name="Description">What it shows.</param>
public sealed record EvidenceFile(string Name, long Bytes, string Sha256, string Description);
