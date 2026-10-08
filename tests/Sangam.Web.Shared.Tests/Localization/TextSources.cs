using System.Text;
using System.Text.RegularExpressions;

namespace Sangam.Web.Shared.Tests.Localization;

/// <summary>
/// Reads the repository's sources the way the i18n lint needs them (PR-18): every catalogue key a screen asks for,
/// and every piece of visible text that is not going through the catalogue.
/// </summary>
internal static partial class TextSources
{
    /// <summary>The projects whose screens are translated.</summary>
    public static readonly string[] UiProjects = ["Sangam.Identity.Server", "Sangam.SelfService.Web", "Sangam.Admin.Web", "Sangam.Partner.Web", "Sangam.Web.Shared"];

    /// <summary>Service code whose messages reach a screen.</summary>
    public static readonly string[] ServiceProjects = ["Sangam.Identity.Infrastructure", "Sangam.Identity.Application"];

    /// <summary>
    /// Service files whose sentences never reach a translated screen: start-up guards, seed data, e-mail (PR-19), the
    /// developer API, and the monitoring alerts (D-H), which go to the platform owner in English.
    /// </summary>
    private static readonly string[] ServiceExclusions =
    [
        "/Persistence/", "/Seeding/", "Guard.cs", "KeyRingProtection.cs", "UnavailableEmailSender.cs", "AccountEmails.cs",
        "WebHosting.cs", "/Tenancy/EfManagementService.cs", "/Emails/", "/Sms/Templates",
        "/Monitoring/AlertRules.cs", "/Monitoring/TlsProbe.cs", "/Monitoring/EfMonitoringService.cs", "/Audit/",
    ];

    /// <summary>
    /// UI files that are not screens a person reads in their language: development-only pages, OAuth protocol errors
    /// (<c>error_description</c> is for developers), the management API and start-up refusals.
    /// </summary>
    private static readonly string[] UiExclusions = ["/Pages/Dev/", "/Pages/Index.cshtml", "/Endpoints/", "/Api/", "TokenCertificates.cs", "Program.cs"];

    /// <summary>Visible words that are the same in every language.</summary>
    private static readonly Regex Untranslatable = new(@"^(sangam|Sangam|partners|SangamID|English|imagiQa|LiPi( HIS)?|id\.sangamid\.in|sangamid\.in|OK|PIN|ID|OTP|SMS|DLT|JSON|Aadhaar|ABHA|HIS|DPDP|[A-Z]{2,6}|[a-z0-9.-]+@[a-z0-9.-]+|https?://\S*|[\W\d_]+)$", RegexOptions.CultureInvariant);

    public static string RepoRoot { get; } = FindRoot();

    public static IEnumerable<string> Files(string project, params string[] extensions)
    {
        string dir = Path.Combine(RepoRoot, "src", project);
        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    private static bool UiExcluded(string path) => UiExclusions.Any(x => Relative(path).Contains(x, StringComparison.Ordinal));

    public static string Relative(string path) => Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');

    /// <summary>Every key a screen asks the catalogue for, with where it is asked.</summary>
    public static Dictionary<string, string> RequiredKeys()
    {
        Dictionary<string, string> keys = new(StringComparer.Ordinal);
        foreach (string project in UiProjects)
        {
            foreach (string file in Files(project, ".cshtml", ".razor", ".cs").Where(f => !UiExcluded(f)))
            {
                // Comments are not screens.
                string text = string.Join('\n', File.ReadLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
                foreach (Match m in LocalizerKey().Matches(text))
                {
                    keys.TryAdd(Unescape(m.Groups[1].Value), Relative(file));
                }

                foreach (Match m in AnnotationMessage().Matches(text))
                {
                    keys.TryAdd(Unescape(m.Groups[1].Value), Relative(file));
                }
            }
        }

        foreach ((string key, string where) in ServiceSentences())
        {
            keys.TryAdd(key, where);
        }

        // Phrases translated where they are shown, marked `// i18n-key` (interpolations become {0}, {1}…).
        foreach (string project in UiProjects.Concat(ServiceProjects).Append("Sangam.Identity.Domain"))
        {
            foreach (string file in Files(project, ".cs").Where(f => !UiExcluded(f)))
            {
                foreach (string line in File.ReadLines(file).Where(l => l.Contains("// i18n-key", StringComparison.Ordinal)))
                {
                    foreach (Match m in Literal().Matches(line[..line.IndexOf("// i18n-key", StringComparison.Ordinal)]))
                    {
                        string text = Unescape(m.Groups[2].Value);
                        if (m.Groups[1].Value == "$")
                        {
                            int n = 0;
                            text = Interpolation().Replace(text, _ => "{" + (n++).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
                        }

                        keys.TryAdd(text, Relative(file));
                    }
                }
            }
        }

        return keys;
    }

    /// <summary>The sentences services return for a screen to show; interpolations become {0}, {1}…</summary>
    public static IEnumerable<(string Key, string Where)> ServiceSentences()
    {
        foreach (string project in ServiceProjects)
        {
            foreach (string file in Files(project, ".cs"))
            {
                string rel = Relative(file);
                if (ServiceExclusions.Any(x => rel.Contains(x, StringComparison.Ordinal)))
                {
                    continue;
                }

                int lineNumber = 0;
                foreach (string line in File.ReadLines(file))
                {
                    lineNumber++;
                    foreach (string sentence in Sentences(line))
                    {
                        yield return (sentence, rel + ":" + lineNumber);
                    }
                }
            }
        }
    }

    /// <summary>Sentence-like string literals on a line of C# (not logging, not exceptions, not comments).</summary>
    public static IEnumerable<string> Sentences(string line)
    {
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('[') || line.Contains("Exception(", StringComparison.Ordinal)
            || line.Contains("Log", StringComparison.Ordinal) || line.Contains("i18n-ignore", StringComparison.Ordinal) || line.Contains("L[", StringComparison.Ordinal) || line.Contains("L.Html(", StringComparison.Ordinal) || line.Contains("L.Markup(", StringComparison.Ordinal))
        {
            yield break;
        }

        foreach (Match m in Literal().Matches(line))
        {
            string text = Unescape(m.Groups[2].Value);
            if (m.Groups[1].Value == "$")
            {
                int n = 0;
                text = Interpolation().Replace(text, _ => "{" + (n++).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
            }

            if (text.Contains(' ', StringComparison.Ordinal) && SentenceStart().IsMatch(text) && SentenceEnd().IsMatch(text))
            {
                yield return text;
            }
        }
    }

    /// <summary>
    /// In UI code, any capitalised phrase or sentence literal is text a person will read: "Your name", "None yet",
    /// "That code is not correct." (formats, addresses and lines marked <c>i18n-ignore</c> aside).
    /// </summary>
    public static IEnumerable<string> Phrases(string line)
    {
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal) || trimmed.StartsWith('[')
            || line.Contains("Exception(", StringComparison.Ordinal) || line.Contains("Log", StringComparison.Ordinal) || line.Contains("i18n-ignore", StringComparison.Ordinal) || line.Contains("i18n-key", StringComparison.Ordinal))
        {
            yield break;
        }

        string withoutKeys = IndexerKey().Replace(LocalizerKey().Replace(line, " "), " ");
        foreach (Match m in Literal().Matches(withoutKeys))
        {
            string text = Unescape(m.Groups[2].Value);
            if (m.Groups[1].Value == "$")
            {
                int n = 0;
                text = Interpolation().Replace(text, _ => "{" + (n++).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
            }

            if (DateFormat().IsMatch(text))
            {
                continue;
            }

            bool switchArm = line.Contains("=>", StringComparison.Ordinal) && text.Contains(' ', StringComparison.Ordinal) && HasWords(text);
            if (!switchArm && (DateFormat().IsMatch(text) || text.Contains("://", StringComparison.Ordinal) || !SentenceStart().IsMatch(text) || !text.Any(char.IsLower)))
            {
                continue;
            }

            if (switchArm || (text.Contains(' ', StringComparison.Ordinal) && HasWords(text)) || SentenceEnd().IsMatch(text))
            {
                yield return text;
            }
        }
    }

    /// <summary>Visible text in Razor markup, and literals in UI code, that bypass the catalogue.</summary>
    public static List<string> Untranslated()
    {
        List<string> problems = [];
        foreach (string project in UiProjects)
        {
            foreach (string file in Files(project, ".cshtml", ".razor").Where(f => !UiExcluded(f)))
            {
                problems.AddRange(ScanMarkup(File.ReadAllText(file)).Select(p => Relative(file) + ": " + p));
            }

            foreach (string file in Files(project, ".cs").Where(f => !UiExcluded(f)))
            {
                int lineNumber = 0;
                foreach (string line in File.ReadLines(file))
                {
                    lineNumber++;
                    foreach (string phrase in Phrases(line))
                    {
                        problems.Add($"{Relative(file)}:{lineNumber}: \"{phrase}\"");
                    }
                }
            }
        }

        return problems;
    }

    public static IEnumerable<string> ScanMarkup(string source)
    {
        string text = RazorComment().Replace(source, " ");
        text = HtmlComment().Replace(text, " ");
        text = ScriptOrStyle().Replace(text, " ");
        int code = CodeBlockStart().Match(text) is { Success: true } cb ? cb.Index : -1;
        if (code >= 0)
        {
            foreach (string p in ScanCode(text[code..]))
            {
                yield return p;
            }

            text = text[..code];
        }

        // Razor code blocks and expressions: check their string literals, then remove them.
        StringBuilder markup = new();
        List<string> expressions = [];
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '@' && i + 1 < text.Length && text[i + 1] == '@')
            {
                markup.Append("@@");
                i += 2;
                continue;
            }

            // help@sangamid.in is text; Email@(...) is an expression.
            if (text[i] == '@' && i + 1 < text.Length && (i == 0 || !char.IsLetterOrDigit(text[i - 1]) || !char.IsLetterOrDigit(text[i + 1])))
            {
                int end = ExpressionEnd(text, i + 1);
                expressions.Add(text[(i + 1)..end]);
                markup.Append(' ');
                i = end;
                continue;
            }

            markup.Append(text[i]);
            i++;
        }

        foreach (string expression in expressions)
        {
            foreach (string literal in LiteralsOutsideLocalizer(expression))
            {
                yield return "literal in Razor code: \"" + literal + "\"";
            }
        }

        string plain = markup.ToString();
        foreach (Match attribute in VisibleAttribute().Matches(plain))
        {
            string value = attribute.Groups[2].Value.Trim();
            if (HasWords(value))
            {
                yield return $"{attribute.Groups[1].Value}=\"{value}\"";
            }
        }

        plain = AttributeValue().Replace(plain, "=\"\"");
        plain = Tag().Replace(plain, "\n");
        plain = ElseIf().Replace(plain, " ");
        foreach (string piece in plain.Split('\n'))
        {
            if (piece.TrimEnd().EndsWith(';') || CaseLabel().IsMatch(piece))
            {
                // A C# statement inside a Razor block.
                continue;
            }

            string candidate = piece.Replace("{", " ", StringComparison.Ordinal).Replace("}", " ", StringComparison.Ordinal).Replace(";", " ", StringComparison.Ordinal);
            candidate = ElseWord().Replace(candidate, " ").Trim();
            candidate = System.Net.WebUtility.HtmlDecode(candidate).Trim();
            if (HasWords(candidate))
            {
                yield return "text: \"" + candidate + "\"";
            }
        }
    }

    private static IEnumerable<string> ScanCode(string code)
    {
        foreach (string line in code.Split('\n'))
        {
            foreach (string phrase in Phrases(line))
            {
                yield return "literal in @code: \"" + phrase + "\"";
            }

            if (SwitchArmText().Match(line) is { Success: true } arm && !line.Contains("i18n-ignore", StringComparison.Ordinal) && !line.Contains("L[", StringComparison.Ordinal))
            {
                yield return "literal in @code: \"" + arm.Groups[1].Value + "\"";
            }
        }
    }

    private static IEnumerable<string> LiteralsOutsideLocalizer(string expression)
    {
        string withoutKeys = IndexerKey().Replace(LocalizerKey().Replace(expression, " "), " ");
        foreach (Match m in Literal().Matches(withoutKeys))
        {
            string value = m.Groups[2].Value;
            if (DateFormat().IsMatch(value))
            {
                continue;
            }

            if (value.Contains(' ', StringComparison.Ordinal) ? HasWords(value) : (value.Length > 1 && char.IsUpper(value[0]) && value.Any(char.IsLower) && HasWords(value)))
            {
                yield return value;
            }
        }
    }

    private static bool HasWords(string value)
    {
        // Already in an Indian script (the language picker names each language in its own), or only CSS classes.
        if (!Word().IsMatch(value) || IndianScript().IsMatch(value) || CssClasses().IsMatch(value))
        {
            return false;
        }

        string stripped = Separators().Replace(value, " ").Trim();
        return stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => !Untranslatable.IsMatch(w) && Word().IsMatch(w))
            && !Untranslatable.IsMatch(value.Trim());
    }

    // Finds where a Razor transition starting at `start` (just after '@') ends.
    private static int ExpressionEnd(string text, int start)
    {
        int i = start;
        if (i < text.Length && (text[i] == '(' || text[i] == '{'))
        {
            return Balanced(text, i);
        }

        while (i < text.Length)
        {
            if (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.' || text[i] == '?' || text[i] == '!')
            {
                i++;
            }
            else if (text[i] == '(' || text[i] == '[')
            {
                i = Balanced(text, i);
            }
            else if (text[i] == ' ' && Keyword().IsMatch(text[start..i]))
            {
                // @if (...) / @foreach (...) / @switch (...) / @using … : take the parenthesised part too.
                int j = i;
                while (j < text.Length && text[j] == ' ')
                {
                    j++;
                }

                return j < text.Length && text[j] == '(' ? Balanced(text, j) : LineEnd(text, j);
            }
            else
            {
                break;
            }
        }

        // A trailing '.' or '?' belongs to the text, not the expression.
        while (i > start && (text[i - 1] == '.' || text[i - 1] == '?'))
        {
            i--;
        }

        return i;
    }

    private static int LineEnd(string text, int i)
    {
        int n = text.IndexOf('\n', i);
        return n < 0 ? text.Length : n;
    }

    private static int Balanced(string text, int open)
    {
        char o = text[open];
        char c = o switch { '(' => ')', '[' => ']', _ => '}' };
        int depth = 0;
        bool inString = false;
        for (int i = open; i < text.Length; i++)
        {
            char ch = text[i];
            if (inString)
            {
                if (ch == '\\')
                {
                    i++;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == o)
            {
                depth++;
            }
            else if (ch == c && --depth == 0)
            {
                return i + 1;
            }
        }

        return text.Length;
    }

    private static string FindRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Sangam.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Sangam.sln not found above " + AppContext.BaseDirectory);
    }

    private static string Unescape(string value) => value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

    [GeneratedRegex(@"\bL(?:\[|\.Html\(|\.Markup\()\s*""((?:[^""\\]|\\.)*)""")]
    public static partial Regex LocalizerKey();

    [GeneratedRegex(@"\[\s*""[^""]*""\s*\]")]
    private static partial Regex IndexerKey();

    [GeneratedRegex(@"^([dMyHhmsftzK :/.,-]|'[^']*')+$")]
    private static partial Regex DateFormat();

    [GeneratedRegex(@"\bErrorMessage\s*=\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex AnnotationMessage();

    [GeneratedRegex(@"(\$?)""((?:[^""\\]|\\.)*)""")]
    private static partial Regex Literal();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Interpolation();

    [GeneratedRegex(@"^[A-Z{“]")]
    private static partial Regex SentenceStart();

    [GeneratedRegex(@"[.!?…]$")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex RazorComment();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"<(script|style)\b.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"^@(code|functions)\s*\{", RegexOptions.Multiline)]
    private static partial Regex CodeBlockStart();

    [GeneratedRegex(@"\b(placeholder|title|aria-label|alt|label|Title|Subtitle|Label|Text|Heading|Description|Message|Caption|Note|Hint|Placeholder|ConfirmText|CancelText)\s*=\s*""([^""]*)""")]
    private static partial Regex VisibleAttribute();

    [GeneratedRegex(@"=\s*""[^""]*""")]
    private static partial Regex AttributeValue();

    [GeneratedRegex(@"^\s*(case\s.*|default\s*):\s*$")]
    private static partial Regex CaseLabel();

    [GeneratedRegex(@"<[^<>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\belse\s+if\s*\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)")]
    private static partial Regex ElseIf();

    [GeneratedRegex(@"\belse\b")]
    private static partial Regex ElseWord();

    [GeneratedRegex(@"^(if|foreach|for|while|switch|using|await|lock|inject|model|page|layout|namespace|attribute|implements|inherits|rendermode|typeparam|addTagHelper|section|else|try|catch|finally|do)$")]
    private static partial Regex Keyword();

    [GeneratedRegex(@"=>\s*""([A-Z][^""]*[a-z][^""]*)""")]
    private static partial Regex SwitchArmText();

    [GeneratedRegex(@"[\u0900-\u0DFF]")]
    private static partial Regex IndianScript();

    [GeneratedRegex(@"^\s*(sg-[a-z0-9-]+\s*)+$")]
    private static partial Regex CssClasses();

    [GeneratedRegex(@"\p{L}{2,}")]
    private static partial Regex Word();

    [GeneratedRegex(@"[·•—–|/()\[\],:;!?.“”""'’…×→←↑↓✓+*#=<>-]")]
    private static partial Regex Separators();
}
