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
}
