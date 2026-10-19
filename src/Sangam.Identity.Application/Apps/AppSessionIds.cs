using System.Security.Cryptography;

namespace Sangam.Identity.Application.Apps;

/// <summary>
/// rc.14 (ADR-0017): the <c>sid</c> an application is given for a sign-in session. Every application gets its own, so two
/// applications used in one session cannot match their (pairwise) ids for the person through it. It is a SHA-256 of the
/// session id and the application id, which neither application knows, so it cannot be turned back or recomputed outside
/// Sangam; no key is needed. Sangam's own applications (portal and consoles) are given the session id itself, which the
/// portal uses to tell its own session.
/// </summary>
public static class AppSessionIds
{
    private static readonly byte[] Label = "sangam.sid/1"u8.ToArray();

    /// <summary>The <c>sid</c> for <paramref name="sessionId"/> as the application <paramref name="appId"/> sees it.</summary>
    /// <param name="sessionId">The Sangam sign-in session.</param>
    /// <param name="appId">The application.</param>
    /// <param name="platform">Whether it is one of Sangam's own applications.</param>
    public static Guid For(Guid sessionId, Guid appId, bool platform)
    {
        if (platform)
        {
            return sessionId;
        }

        Span<byte> input = stackalloc byte[Label.Length + 32];
        Label.CopyTo(input);
        sessionId.TryWriteBytes(input[Label.Length..], bigEndian: true, out _);
        appId.TryWriteBytes(input[(Label.Length + 16)..], bigEndian: true, out _);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);

        // RFC 9562 version 8 (custom), so it reads as an ordinary UUID.
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }
}
