using System.Globalization;

namespace Sangam.SelfService.Web.Components.Common;

/// <summary>Human-friendly timestamps for the portal.</summary>
public static class Relative
{
    /// <summary>"just now", "12 minutes ago", "Yesterday", "14 Mar 2026".</summary>
    /// <param name="value">The instant.</param>
    /// <param name="now">Current time.</param>
    public static string Describe(DateTimeOffset value, DateTimeOffset now)
    {
        TimeSpan age = now - value;
        return age switch
        {
            { TotalSeconds: < 90 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)age.TotalMinutes} minutes ago",
            { TotalHours: < 24 } => $"{(int)age.TotalHours} hours ago",
            { TotalDays: < 2 } => "yesterday",
            { TotalDays: < 30 } => $"{(int)age.TotalDays} days ago",
            _ => value.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>A full timestamp for titles and tables.</summary>
    /// <param name="value">The instant.</param>
    public static string Full(DateTimeOffset value) => value.ToString("d MMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
