using Sangam.Shared.Constants;

namespace Sangam.Client;

/// <summary>How this application signs people in with Sangam.</summary>
public sealed class SangamOptions
{
    /// <summary>Sangam's address, for example <c>https://id.sangamid.in</c>. Required.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>This application's client id, as registered on Sangam. Required.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>This application's client secret. Keep it out of source control.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// What to ask for. <c>openid</c> is always included. The default adds the person's name and
    /// email and, with <c>orgs.read</c>, their organisations and roles in this application.
    /// </summary>
    public IList<string> Scopes { get; } = [SangamScopes.OpenId, SangamScopes.Profile, SangamScopes.Email, SangamScopes.OrgsRead];

    /// <summary>Where Sangam returns the person after signing in.</summary>
    public string CallbackPath { get; set; } = SangamDefaults.CallbackPath;

    /// <summary>Where Sangam returns the person after signing out.</summary>
    public string SignedOutCallbackPath { get; set; } = SangamDefaults.SignedOutCallbackPath;

    /// <summary>Name of this application's sign-in cookie.</summary>
    public string CookieName { get; set; } = ".Sangam.Session";

    /// <summary>How long a sign-in lasts while the person keeps using the application.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Whether Sangam must be reached over HTTPS. Turn off only for local development.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Whether to keep the tokens in the sign-in cookie. On by default: signing out sends the ID
    /// token back to Sangam as <c>id_token_hint</c>, which is how Sangam knows which application
    /// is asking and returns the person to it afterwards. Turn off only if you never sign out.
    /// </summary>
    public bool SaveTokens { get; set; } = true;

    /// <summary>Throws when a required setting is missing or inconsistent.</summary>
    /// <exception cref="InvalidOperationException">The options cannot work.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Authority) || !Uri.TryCreate(Authority, UriKind.Absolute, out Uri? authority))
        {
            throw new InvalidOperationException("Sangam: set Authority to Sangam's absolute address, such as https://id.sangamid.in.");
        }

        if (RequireHttpsMetadata && authority.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Sangam: Authority must use https. Set RequireHttpsMetadata to false only for local development.");
        }

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            throw new InvalidOperationException("Sangam: set ClientId to this application's client id as registered on Sangam.");
        }

        if (!CallbackPath.StartsWith('/') || !SignedOutCallbackPath.StartsWith('/'))
        {
            throw new InvalidOperationException("Sangam: CallbackPath and SignedOutCallbackPath must start with '/'.");
        }
    }
}
