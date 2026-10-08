using System.Text.RegularExpressions;
using System.Xml;

namespace Sangam.Identity.Infrastructure.Customisation;

/// <summary>
/// Checks an uploaded logo (PR-19, SGM-209 §3): a PNG, or an SVG that is only a picture — no scripts, event handlers,
/// embedded HTML, external references or entity tricks. The identity server also serves logos with a sandboxing
/// Content-Security-Policy, so even a missed trick cannot run in Sangam's origin.
/// </summary>
public static partial class LogoCheck
{
    private static readonly string[] ForbiddenElements = ["script", "foreignobject", "iframe", "embed", "object", "audio", "video", "animate", "set", "animatemotion", "animatetransform", "handler", "listener"];

    /// <summary>Whether the bytes are a PNG.</summary>
    /// <param name="content">The bytes.</param>
    public static bool IsPng(byte[] content)
        => content is { Length: > 8 } && content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47 && content[4] == 0x0D && content[5] == 0x0A && content[6] == 0x1A && content[7] == 0x0A;

    /// <summary>Why the logo cannot be used, or null when it can.</summary>
    /// <param name="contentType">The type the browser said it was.</param>
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

        if (IsPng(content))
        {
            return null;
        }

        if (!contentType.Contains("svg", StringComparison.OrdinalIgnoreCase))
        {
            return "Upload a PNG or an SVG image.";
        }

        return SvgProblem(content);
    }

    private static string? SvgProblem(byte[] content)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = content.Length * 2L,
        };
        try
        {
            using MemoryStream stream = new(content);
            using XmlReader reader = XmlReader.Create(stream, settings);
            bool root = true;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                string name = reader.LocalName.ToLowerInvariant();
                if (root)
                {
                    if (name != "svg")
                    {
                        return "That is not an SVG image.";
                    }

                    root = false;
                }

                if (ForbiddenElements.Contains(name))
                {
                    return "The SVG contains something other than a picture (" + reader.LocalName + "). Export it as a plain image.";
                }

                for (bool more = reader.MoveToFirstAttribute(); more; more = reader.MoveToNextAttribute())
                {
                    string attribute = reader.LocalName.ToLowerInvariant();
                    string value = reader.Value;
                    if (attribute.StartsWith("on", StringComparison.Ordinal))
                    {
                        return "The SVG contains a script handler (" + reader.LocalName + "). Export it as a plain image.";
                    }

                    if ((attribute == "href" || attribute == "src") && !value.TrimStart().StartsWith('#'))
                    {
                        return "The SVG refers to something outside itself. Export it with everything embedded as shapes.";
                    }

                    if (UnsafeValue().IsMatch(value))
                    {
                        return "The SVG contains a script or an outside reference. Export it as a plain image.";
                    }
                }

                reader.MoveToElement();
                if (name == "style" && !reader.IsEmptyElement && UnsafeValue().IsMatch(reader.ReadInnerXml()))
                {
                    return "The SVG's styles refer to something outside it. Export it as a plain image.";
                }
            }

            return root ? "That is not an SVG image." : null;
        }
        catch (XmlException)
        {
            return "That SVG could not be read.";
        }
    }

    [GeneratedRegex(@"javascript:|data:(?!image/(png|jpeg|gif|webp);base64)|url\(\s*['""]?(?!#)|@import|expression\(", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeValue();
}
