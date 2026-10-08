using Microsoft.Extensions.Localization;
using Sangam.Shared;
using Sangam.Web.Shared.Localization;

namespace Sangam.SelfService.Web.Components.Common;

/// <summary>Human-friendly timestamps for the portal, in the reader's language (PR-18).</summary>
public static class Relative
{
    private static IStringLocalizer L => CatalogueStringLocalizer.Shared;

    /// <summary>"just now", "12 minutes ago", "yesterday", "14 Mar 2026".</summary>
    /// <param name="value">The instant.</param>
    /// <param name="now">Current time.</param>
    public static string Describe(DateTimeOffset value, DateTimeOffset now)
    {
        TimeSpan age = now - value;
        return age switch
        {
            { TotalSeconds: < 90 } => L["just now"],
            { TotalMinutes: < 60 } => L["{0} minutes ago", (int)age.TotalMinutes],
            { TotalHours: < 24 } => L["{0} hours ago", (int)age.TotalHours],
            { TotalDays: < 2 } => L["yesterday"],
            { TotalDays: < 30 } => L["{0} days ago", (int)age.TotalDays],
            _ => IndiaTime.Date(value),
        };
    }

    /// <summary>A full timestamp for titles and tables.</summary>
    /// <param name="value">The instant.</param>
    public static string Full(DateTimeOffset value) => IndiaTime.Stamp(value);
}
