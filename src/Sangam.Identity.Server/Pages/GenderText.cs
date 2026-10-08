using Microsoft.Extensions.Localization;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Server.Pages;

/// <summary>A gender as a person reads it, in their language (PR-18).</summary>
internal static class GenderText
{
    /// <summary>The label for <paramref name="gender"/>.</summary>
    /// <param name="text">The text catalogue.</param>
    /// <param name="gender">The value.</param>
    public static string Label(IStringLocalizer text, Gender gender) => gender switch
    {
        Gender.Female => text["Female"],
        Gender.Male => text["Male"],
        Gender.Other => text["Other"],
        _ => text["Prefer not to say"],
    };
}
