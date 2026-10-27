namespace Sangam.Identity.Infrastructure.Customisation;

/// <summary>
/// Checks an uploaded logo (PR-19, SGM-209 §3). rc.16 (ASVS V1.12.2, founder decision of 26 Oct 2026): only a PNG is
/// accepted. An SVG can carry script, so Sangam no longer takes one at all, rather than cleaning it; nothing scriptable is
/// ever served from Sangam's origin. The identity server still serves every logo with <c>nosniff</c> and a sandboxing
/// Content-Security-Policy.
/// </summary>
public static class LogoCheck
{
    /// <summary>The only type a logo is stored and served as.</summary>
    public const string ContentType = "image/png";

    /// <summary>
    /// Whether the bytes are a PNG: the eight-byte signature, then the image header chunk (<c>IHDR</c>) that every PNG
    /// starts with.
    /// </summary>
    /// <param name="content">The bytes.</param>
    public static bool IsPng(byte[] content)
        => content is { Length: >= 16 }
            && content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47
            && content[4] == 0x0D && content[5] == 0x0A && content[6] == 0x1A && content[7] == 0x0A
            && content[12] == (byte)'I' && content[13] == (byte)'H' && content[14] == (byte)'D' && content[15] == (byte)'R';

    /// <summary>Why the logo cannot be used, or null when it can.</summary>
    /// <param name="contentType">The type the browser said it was (not trusted; the bytes decide).</param>
    /// <param name="content">The bytes.</param>
    /// <param name="maxBytes">The size limit.</param>
    public static string? Problem(string contentType, byte[] content, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0)
        {
            return "The file is empty.";
        }

        if (content.Length > maxBytes)
        {
            return $"The logo can be at most {maxBytes / 1024} KB.";
        }

        return IsPng(content) ? null : "Upload the logo as a PNG image.";
    }
}
