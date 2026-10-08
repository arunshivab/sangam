using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Sangam.Identity.Domain;

namespace Sangam.Identity.Infrastructure.Customisation;

/// <summary>
/// Fills and checks message templates (PR-19). E-mail templates use <c>{{variable}}</c>; values are inserted as
/// text. The HTML version is built from the text inside a fixed Sangam layout, so a template changes the words,
/// never the structure (SGM-209 §3): paragraphs stay paragraphs, a line holding only the code becomes the code box,
/// a line holding only the link becomes the button.
/// </summary>
public static partial class TemplateText
{
    /// <summary>The longest subject and body accepted.</summary>
    public const int MaxSubject = 200;

    /// <summary>The longest body accepted.</summary>
    public const int MaxBody = 8000;

    /// <summary>
    /// R7 (ASVS V5.2.3): a filled-in subject on one line. A value placed in it (a person's name, an application's name)
    /// could carry a line break, which a mail system could read as a new header; line breaks and other control characters
    /// become spaces.
    /// </summary>
    /// <param name="template">The subject template.</param>
    /// <param name="values">Variable values.</param>
    public static string FillSubject(string template, IReadOnlyDictionary<string, string> values)
        => new([.. Fill(template, values).Select(c => char.IsControl(c) ? ' ' : c)]);

    /// <summary>Fills <paramref name="template"/> with <paramref name="values"/>; an unknown variable is left empty.</summary>
    /// <param name="template">The template.</param>
    /// <param name="values">Variable values.</param>
    public static string Fill(string template, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        return Variable().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out string? v) ? v : string.Empty);
    }

    /// <summary>The variables a template uses.</summary>
    /// <param name="template">The template.</param>
    public static IReadOnlySet<string> Variables(string? template)
        => template is null ? new HashSet<string>() : Variable().Matches(template).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>Why an e-mail template cannot be saved, or null when it can.</summary>
    /// <param name="kind">The kind of message.</param>
    /// <param name="subject">Subject.</param>
    /// <param name="body">Body.</param>
    public static string? CheckEmail(MessageTemplateKind kind, string? subject, string? body)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
        {
            return "Enter both a subject and a message.";
        }

        if (subject.Length > MaxSubject || subject.Any(char.IsControl))
        {
            return $"The subject must be one line of at most {MaxSubject} characters.";
        }

        if (body.Length > MaxBody)
        {
            return $"The message can be at most {MaxBody} characters.";
        }

        HashSet<string> used = [.. Variables(subject), .. Variables(body)];
        string? unknown = used.FirstOrDefault(v => !kind.Variables.Contains(v));
        if (unknown is not null)
        {
            string allowed = kind.Variables.Count == 0 ? "-" : string.Join(", ", kind.Variables);
            return $"“{unknown}” is not a variable this message can use. It can use: {allowed}.";
        }

        string? missing = kind.Required.FirstOrDefault(v => !Variables(body).Contains(v));
        return missing is null ? null : $"The message must contain the variable “{missing}”.";
    }

    /// <summary>
    /// Why an SMS template cannot be saved, or null: it needs its DLT template id and must have exactly as many
    /// <c>{#var#}</c> as the registered English template, because operators refuse text that does not match.
    /// </summary>
    /// <param name="body">The text.</param>
    /// <param name="dltTemplateId">The DLT template id.</param>
    /// <param name="englishBody">The registered English text.</param>
    public static string? CheckSms(string? body, string? dltTemplateId, string englishBody)
    {
        ArgumentNullException.ThrowIfNull(englishBody);
        if (string.IsNullOrWhiteSpace(body))
        {
            return "Enter the text.";
        }

        if (string.IsNullOrWhiteSpace(dltTemplateId) || !dltTemplateId.Trim().All(char.IsAsciiDigit) || dltTemplateId.Trim().Length > 30)
        {
            return "Enter the DLT template id this text is registered under (digits only).";
        }

        int want = CountSmsVariables(englishBody);
        return CountSmsVariables(body) != want ? $"The text must have exactly {want} variable markers, like the registered English text." : null;
    }

    /// <summary>The HTML version of a filled e-mail: the text inside Sangam's fixed layout.</summary>
    /// <param name="text">The filled text.</param>
    /// <param name="values">The values (to recognise the code and link lines).</param>
    /// <param name="heading">The name shown in the header (an application, or Sangam).</param>
    /// <param name="accent">The header colour, already checked (<c>#RRGGBB</c>).</param>
    /// <param name="language">The message language, for <c>lang</c>.</param>
    public static string Html(string text, IReadOnlyDictionary<string, string> values, string heading, string accent, string language)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(values);
        string? code = values.TryGetValue("code", out string? c) ? c : null;
        string? link = values.TryGetValue("link", out string? l) ? l : null;
        StringBuilder html = new();
        html.Append("<!DOCTYPE html><html lang=\"").Append(WebUtility.HtmlEncode(language)).Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"></head>")
            .Append("<body style=\"margin:0;padding:24px;background:#F5F3EE;font-family:'LiPi Sans','Noto Sans',Arial,sans-serif;color:#1C2B2A\">")
            .Append("<div style=\"max-width:560px;margin:0 auto;background:#FFFFFF;border:1px solid #E4E0D6;border-radius:8px;overflow:hidden\">")
            .Append("<div style=\"padding:14px 24px;background:").Append(accent).Append(";color:#FFFFFF;font-weight:600;font-size:16px\">").Append(WebUtility.HtmlEncode(heading)).Append("</div>")
            .Append("<div style=\"padding:8px 24px 20px;font-size:15px;line-height:1.6\">");
        foreach (string paragraph in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = paragraph.Trim();
            if (code is not null && trimmed == code)
            {
                html.Append("<p style=\"margin:18px 0;font:600 28px/1.2 ui-monospace,Consolas,monospace;letter-spacing:0.2em\">").Append(WebUtility.HtmlEncode(code)).Append("</p>");
            }
            else if (link is not null && trimmed == link)
            {
                string href = WebUtility.HtmlEncode(link);
                html.Append("<p style=\"margin:18px 0\"><a href=\"").Append(href).Append("\" style=\"display:inline-block;padding:10px 18px;background:").Append(accent)
                    .Append(";color:#FFFFFF;text-decoration:none;border-radius:6px\">").Append(href).Append("</a></p>");
            }
            else
            {
                html.Append("<p style=\"margin:12px 0\">").Append(WebUtility.HtmlEncode(trimmed).Replace("\n", "<br>", StringComparison.Ordinal)).Append("</p>");
            }
        }

        html.Append("</div></div></body></html>");
        return html.ToString();
    }

    private static int CountSmsVariables(string text) => text.Split("{#var#}").Length - 1;

    [GeneratedRegex(@"\{\{\s*([a-z_]+)\s*\}\}")]
    private static partial Regex Variable();
}
