using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Sangam.Identity.Server.Endpoints;

/// <summary>The OAuth 2 / OpenID Connect protocol endpoints under <c>/connect</c>.</summary>
public static class ConnectEndpoints
{
    /// <summary>Maps <c>POST /connect/token</c>. PR-02 handles the client-credentials grant; the
    /// authorization-code and refresh-token grants are added with the consent screen in PR-04.</summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapConnectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/connect/token", ExchangeAsync)
            .WithName("Token")
            .DisableAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> ExchangeAsync(
        HttpContext httpContext,
        IOpenIddictApplicationManager applications,
        IOpenIddictScopeManager scopes,
        CancellationToken cancellationToken)
    {
        OpenIddictRequest request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict server request cannot be retrieved.");

        if (request.IsClientCredentialsGrantType())
        {
            // The client credentials were already validated by OpenIddict; the application exists.
            object application = await applications.FindByClientIdAsync(request.ClientId!, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The application details cannot be found in the database.");

            ClaimsIdentity identity = new(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
            identity.SetClaim(Claims.Subject, await applications.GetClientIdAsync(application, cancellationToken).ConfigureAwait(false));
            identity.SetClaim(Claims.Name, await applications.GetDisplayNameAsync(application, cancellationToken).ConfigureAwait(false));
            identity.SetScopes(request.GetScopes());

            List<string> resources = [];
            await foreach (string resource in scopes.ListResourcesAsync(identity.GetScopes(), cancellationToken).ConfigureAwait(false))
            {
                resources.Add(resource);
            }

            identity.SetResources(resources);
            identity.SetDestinations(static claim => claim.Type switch
            {
                Claims.Name or Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
                _ => [Destinations.AccessToken],
            });

            return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        return Results.Forbid(
            authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme],
            properties: new Microsoft.AspNetCore.Authentication.AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.UnsupportedGrantType,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The specified grant type is not supported.",
            }));
    }
}
