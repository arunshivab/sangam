namespace Sangam.Client;

/// <summary>Names and paths the SDK uses unless told otherwise.</summary>
public static class SangamDefaults
{
    /// <summary>The OpenID Connect scheme that talks to Sangam.</summary>
    public const string Scheme = "Sangam";

    /// <summary>The cookie scheme that keeps the person signed in to this application.</summary>
    public const string CookieScheme = "Cookies";

    /// <summary>Where Sangam returns the person after they sign in. Register it on Sangam.</summary>
    public const string CallbackPath = "/signin-sangam";

    /// <summary>Where Sangam returns the person after they sign out. Register it on Sangam.</summary>
    public const string SignedOutCallbackPath = "/signout-sangam";

    /// <summary>The path <see cref="SangamEndpointRouteBuilderExtensions.MapSangamSignOut"/> maps by default.</summary>
    public const string SignOutPath = "/signout";

    /// <summary>rc.12 (A6): the path <c>MapSangamSwitchOrganisation</c> maps by default.</summary>
    public const string SwitchOrganisationPath = "/switch-organisation";

    /// <summary>rc.12: the authentication property that carries an OpenID Connect <c>prompt</c> to Sangam.</summary>
    public const string PromptItem = "sangam.prompt";

    /// <summary>rc.12 (A6): the prompt that asks "Working at which branch?" again.</summary>
    public const string SelectOrganisationPrompt = "select_org";

    /// <summary>rc.12 (T1): the path <c>MapSangamLock</c> maps by default.</summary>
    public const string LockPath = "/lock";

    /// <summary>rc.12 (M12): the path <c>MapSangamEmergency</c> maps by default.</summary>
    public const string EmergencyPath = "/emergency-access";

    /// <summary>rc.12 (M12): the authentication property that carries the case reference to Sangam.</summary>
    public const string EmergencyItem = "sangam.emergency";
}
