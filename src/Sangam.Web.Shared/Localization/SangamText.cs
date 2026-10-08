namespace Sangam.Web.Shared.Localization;

/// <summary>
/// The one text catalogue every Sangam screen draws from (PR-18, SGM-209). Inject
/// <c>IStringLocalizer&lt;SangamText&gt;</c> (by convention named <c>L</c>) and write the English text as the key:
/// <c>@L["Sign in"]</c>, or <c>L["Enter your {0}-digit mobile number.", 10]</c> with placeholders. A language
/// without a translation for a key shows the English, so nothing is ever blank.
/// </summary>
public sealed class SangamText
{
    private SangamText()
    {
    }
}
