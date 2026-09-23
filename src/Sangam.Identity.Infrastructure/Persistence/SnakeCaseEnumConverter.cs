using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Sangam.Identity.Infrastructure.Persistence;

/// <summary>
/// Stores an enum as lowercase snake_case text (<c>DeletedHard</c> ↔ <c>deleted_hard</c>) so the
/// database reads like the design document and check constraints stay human-readable.
/// </summary>
/// <typeparam name="TEnum">The enum type.</typeparam>
internal sealed class SnakeCaseEnumConverter<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    public SnakeCaseEnumConverter()
        : base(v => ToSnake(v), s => FromSnake(s))
    {
    }

    /// <summary>All storage values for <typeparamref name="TEnum"/>, quoted and comma-joined for a CHECK constraint.</summary>
    public static string SqlList()
    {
        IEnumerable<string> values = Enum.GetValues<TEnum>().Select(v => "'" + ToSnake(v) + "'");
        return string.Join(",", values);
    }

    /// <summary>Converts one enum value to its storage form.</summary>
    public static string ToSnake(TEnum value)
    {
        string name = value.ToString();
        StringBuilder sb = new(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static TEnum FromSnake(string stored)
    {
        string pascal = string.Concat(stored.Split('_').Select(p => p.Length == 0 ? p : char.ToUpper(p[0], CultureInfo.InvariantCulture) + p[1..]));
        return Enum.Parse<TEnum>(pascal, ignoreCase: true);
    }
}
