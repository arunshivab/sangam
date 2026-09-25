using Microsoft.AspNetCore.WebUtilities;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Domain.Enums;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Sangam.Identity.Server.Authorization;

/// <summary>
/// The partner app in the flow, when a screen was reached from <c>/connect/authorize</c>.
/// Resolved from the <c>client_id</c> inside the local return URL, so the auth screens can
/// show the partner chip and apply the app's sign-in policy without trusting anything else in
/// the query string.
/// </summary>
public static class PartnerContext
{
    /// <summary>
    /// Optional authorization-request parameter by which an app names the machine it runs on
    /// ("First Floor Radiology"), shown verbatim in the portal's session list. Sangam never
    /// invents this: with no parameter, the session shows only the browser and the IP address.
    /// </summary>
    public const string DeviceParameter = "sangam_device";

    /// <summary>Extracts <c>client_id</c> from a local return URL that points at the authorization endpoint.</summary>
    /// <param name="returnUrl">Local return URL.</param>
    /// <returns>The client id, or <see langword="null"/>.</returns>
    public static string? ClientIdFromReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith("/connect/authorize", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int q = returnUrl.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            return null;
        }

        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(returnUrl[q..]);
        return query.TryGetValue(Parameters.ClientId, out Microsoft.Extensions.Primitives.StringValues value) ? value.ToString() : null;
    }

    /// <summary>The device label the app supplied on the authorization request (<c>sangam_device</c>), trimmed to 100 characters.</summary>
    /// <param name="returnUrl">Local return URL.</param>
    public static string? DeviceLabelFromReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl))
        {
            return null;
        }

        int q = returnUrl.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            return null;
        }

        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(returnUrl[q..]);
        if (!query.TryGetValue(DeviceParameter, out Microsoft.Extensions.Primitives.StringValues value))
        {
            return null;
        }

        string label = value.ToString().Trim();
        return label.Length == 0 ? null : label.Length > 100 ? label[..100] : label;
    }

    /// <summary>Resolves the partner app for a return URL, or <see langword="null"/> when none is in the flow.</summary>
    /// <param name="apps">App directory.</param>
    /// <param name="returnUrl">Local return URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<AppSummary?> ResolveAsync(IAppDirectory apps, string? returnUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apps);
        string? clientId = ClientIdFromReturnUrl(returnUrl);
        if (clientId is null)
        {
            return null;
        }

        AppSummary? app = await apps.FindByClientIdAsync(clientId, cancellationToken).ConfigureAwait(false);
        return app is { Status: AppStatus.Active } ? app : null;
    }

    /// <summary>Whether a session established with <paramref name="sessionMode"/> satisfies <paramref name="required"/>.</summary>
    /// <param name="required">Effective mode the app demands.</param>
    /// <param name="sessionMode">Mode the current session was established with.</param>
    public static bool Satisfies(SignInMode required, SignInMode sessionMode) => required switch
    {
        SignInMode.Password => true,
        SignInMode.PasswordAndOtp => sessionMode == SignInMode.PasswordAndOtp,
        SignInMode.OtpOnly => sessionMode is SignInMode.OtpOnly or SignInMode.PasswordAndOtp,
        _ => true,
    };
}
