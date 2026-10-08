using System.Text;

namespace Sangam.Identity.Infrastructure.Sms;

/// <summary>
/// Fills a DLT content template. Operators match the delivered text against the registered template, so the
/// text is used exactly as registered and each <c>{#var#}</c> is replaced, in order, by one variable.
/// </summary>
public static class DltTemplate
{
    /// <summary>The DLT variable placeholder.</summary>
    public const string Placeholder = "{#var#}";

    /// <summary>The longest value a DLT variable may hold.</summary>
    public const int MaxVariableLength = 30;

    /// <summary>How many variables <paramref name="text"/> has.</summary>
    /// <param name="text">Template text.</param>
    public static int VariableCount(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int count = 0;
        int at = text.IndexOf(Placeholder, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = text.IndexOf(Placeholder, at + Placeholder.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>Replaces each placeholder in order.</summary>
    /// <param name="text">Template text.</param>
    /// <param name="variables">One value per placeholder.</param>
    /// <exception cref="ArgumentException">When the number of values does not match, or a value is too long or spans lines.</exception>
    public static string Render(string text, params string[] variables)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(variables);
        int expected = VariableCount(text);
        if (expected != variables.Length)
        {
            throw new ArgumentException($"The template has {expected} variable(s) but {variables.Length} value(s) were given.", nameof(variables));
        }

        StringBuilder builder = new(text.Length + 32);
        int from = 0;
        foreach (string value in variables)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length > MaxVariableLength || value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
            {
                throw new ArgumentException($"A template value must be one line of at most {MaxVariableLength} characters.", nameof(variables));
            }

            int at = text.IndexOf(Placeholder, from, StringComparison.Ordinal);
            builder.Append(text, from, at - from).Append(value);
            from = at + Placeholder.Length;
        }

        builder.Append(text, from, text.Length - from);
        return builder.ToString();
    }
}
