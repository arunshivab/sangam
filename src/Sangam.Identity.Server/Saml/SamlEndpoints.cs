using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Consents;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Saml;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Authorization;
using Sangam.Shared.Constants;

namespace Sangam.Identity.Server.Saml;

/// <summary>
/// Sangam as a SAML 2.0 identity provider (PR-22, SGM-215): <c>/saml/metadata</c>; <c>/saml/sso</c> for SP-initiated
/// Web Browser SSO (AuthnRequest by HTTP-Redirect or HTTP-POST, Response by HTTP-POST); <c>/saml/continue</c>, where
/// the person signs in and consents under the same rules as an OpenID Connect application; <c>/saml/launch/{id}</c>
/// for IdP-initiated sign-in, only where the service provider allows it; and <c>/saml/slo</c> for SP-initiated logout.
/// </summary>
public static class SamlEndpoints
{
    /// <summary>How long an assertion is valid.</summary>
    public static readonly TimeSpan AssertionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Maps the endpoints.</summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    public static IEndpointRouteBuilder MapSamlEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/saml/metadata", Metadata);
        endpoints.MapMethods("/saml/sso", [HttpMethods.Get, HttpMethods.Post], SingleSignOnAsync).DisableAntiforgery().RequireRateLimiting(AuthRateLimiting.TokenPolicy);
        endpoints.MapGet("/saml/continue", ContinueAsync);
        endpoints.MapGet("/saml/launch/{id:guid}", LaunchAsync);
        endpoints.MapGet("/saml/slo", LogoutAsync).RequireRateLimiting(AuthRateLimiting.TokenPolicy);
        return endpoints;
    }

    /// <summary>
    /// Sangam's own address: the configured issuer (<c>Sangam:Issuer</c>) when there is one, as for OpenID Connect, so the
    /// entity id and the addresses checked in requests never depend on the Host header; the request's own otherwise.
    /// </summary>
    private static string Origin(HttpContext http)
    {
        string? issuer = http.RequestServices.GetRequiredService<IConfiguration>()["Sangam:Issuer"];
        return !string.IsNullOrWhiteSpace(issuer) && Uri.TryCreate(issuer, UriKind.Absolute, out Uri? configured)
            ? configured.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : http.Request.Scheme + "://" + http.Request.Host + http.Request.PathBase;
    }

    private static IResult Metadata(HttpContext http, SamlOptions options)
    {
        if (!options.Enabled)
        {
            return Results.NotFound();
        }

        string origin = Origin(http);
        return Results.Text(SamlProtocol.Metadata(options.EntityIdFor(origin), origin + "/saml/sso", origin + "/saml/slo", options.Certificates), "application/samlmetadata+xml", Encoding.UTF8);
    }

    private static async Task<IResult> SingleSignOnAsync(HttpContext http, SamlOptions options, SamlIdentityProvider idp, IAccountService accounts, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return Results.NotFound();
        }

        bool post = HttpMethods.IsPost(http.Request.Method);
        string message;
        string? relayState;
        if (post)
        {
            IFormCollection form = await http.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
            message = form["SAMLRequest"].ToString();
            relayState = form["RelayState"].ToString() is { Length: > 0 } r ? r : null;
        }
        else
        {
            message = http.Request.Query["SAMLRequest"].ToString();
            relayState = http.Request.Query["RelayState"].ToString() is { Length: > 0 } r ? r : null;
        }

        XmlDocument document;
        AuthnRequestMessage request;
        try
        {
            string xml = post ? Encoding.UTF8.GetString(Convert.FromBase64String(message)) : SamlProtocol.InflateRedirect(message);
            document = SamlProtocol.Load(xml);
            request = SamlProtocol.ReadAuthnRequest(document);
        }
        catch (Exception ex) when (ex is FormatException or XmlException or InvalidDataException)
        {
            return Problem(http, "This is not a SAML sign-in request Sangam can read."); // i18n-key
        }

        // The signature, if the SP sent one: by the query (HTTP-Redirect) or inside the message (HTTP-POST).
        bool signatureValid = false;
        if (await idp.FindAsync(request.Issuer, cancellationToken).ConfigureAwait(false) is var (registered, _) && SamlIdentityProvider.Certificate(registered.SigningCertificate) is X509Certificate2 spCertificate)
        {
            using (spCertificate)
            {
                signatureValid = post
                    ? SamlProtocol.VerifyEnvelopedSignature(document, spCertificate)
                    : http.Request.Query.ContainsKey("Signature") && SamlProtocol.VerifyRedirectSignature(http.Request.QueryString.Value ?? string.Empty, spCertificate);
            }
        }

        string origin = Origin(http);
        SamlAcceptance accepted = await idp.AcceptAsync(request, origin + "/saml/sso", signatureValid, relayState, cancellationToken).ConfigureAwait(false);
        if (accepted.Request is null)
        {
            // A refusal the SP should hear goes back to its registered address; anything else stays here.
            return accepted.StatusCode is string status && accepted.RefusalAcs is string acs && accepted.Provider is SamlServiceProvider sp
                ? PostResponse(http, options, origin, sp, acs, request.Id, relayState, SamlProtocol.Requester, status, subject: null)
                : Problem(http, accepted.Problem ?? "The request was refused."); // i18n-key
        }

        if (request.IsPassive && !(await http.AuthenticateAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false)).Succeeded)
        {
            await idp.MarkAnsweredAsync(accepted.Request.Handle, cancellationToken).ConfigureAwait(false);
            return PostResponse(http, options, origin, accepted.Provider!, accepted.Request.AcsUrl, request.Id, relayState, SamlProtocol.Requester, "urn:oasis:names:tc:SAML:2.0:status:NoPassive", subject: null);
        }

        return Results.Redirect(ContinueUrl(accepted.App!, accepted.Provider!, accepted.Request));
    }

    private static async Task<IResult> LaunchAsync(Guid id, HttpContext http, SamlOptions options, SamlIdentityProvider idp, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return Results.NotFound();
        }

        SamlAcceptance accepted = await idp.LaunchAsync(id, cancellationToken).ConfigureAwait(false);
        return accepted.Request is null ? Problem(http, accepted.Problem ?? "The request was refused.") : Results.Redirect(ContinueUrl(accepted.App!, accepted.Provider!, accepted.Request)); // i18n-key
    }

    private static async Task<IResult> ContinueAsync(
        HttpContext http,
        SamlOptions options,
        SamlIdentityProvider idp,
        IAccountService accounts,
        ISecurityPolicyService policies,
        IConsentService consents,
        ITenancyQuery tenancy,
        ISessionService sessions,
        IAuditWriter audit,
        CancellationToken cancellationToken)
    {
        string handle = http.Request.Query["handle"].ToString();
        if (!options.Enabled || await idp.PendingAsync(handle, cancellationToken).ConfigureAwait(false) is not SamlAcceptance pending)
        {
            return Problem(http, "This sign-in has expired or was completed already. Go back to the application and start again."); // i18n-key
        }

        SamlRequest request = pending.Request!;
        SamlServiceProvider provider = pending.Provider!;
        App app = pending.App!;
        string origin = Origin(http);
        string returnUrl = ContinueUrl(app, provider, request);

        // Declined at the consent page: tell the service provider.
        PendingFlow? denied = await SangamAuthentication.ReadPendingAsync(http, SangamAuthentication.Pending.ConsentDenied).ConfigureAwait(false);
        if (denied is not null && string.Equals(denied.Email, app.ClientId, StringComparison.Ordinal))
        {
            await SangamAuthentication.ClearPendingAsync(http).ConfigureAwait(false);
            await idp.MarkAnsweredAsync(handle, cancellationToken).ConfigureAwait(false);
            return PostResponse(http, options, origin, provider, request.AcsUrl, Blank(request.RequestId), request.RelayState, "urn:oasis:names:tc:SAML:2.0:status:Responder", SamlProtocol.RequestDenied, subject: null);
        }

        (UserSummary? user, ClaimsPrincipal? session, string? redirect) = await SignInGate.CheckAsync(
            http, accounts, policies, app.Id, returnUrl, request.RequiredLevel, request.ForceAuthn ? request.CreatedAt : null, cancellationToken).ConfigureAwait(false);
        if (redirect is not null)
        {
            return Results.Redirect(redirect);
        }

        string[] scopes = Scopes(provider);
        if (!await consents.HasValidConsentAsync(user!.Id, app.Id, scopes, cancellationToken).ConfigureAwait(false))
        {
            return Results.Redirect("/consent?returnUrl=" + Uri.EscapeDataString(returnUrl));
        }

        if (!await idp.MarkAnsweredAsync(handle, cancellationToken).ConfigureAwait(false))
        {
            return Problem(http, "This sign-in has expired or was completed already. Go back to the application and start again."); // i18n-key
        }

        AuthenticationProof proof = AuthenticationProof.FromSession(session!);
        int level = AuthenticationAssurance.Level(proof.Acr);
        IReadOnlyList<OrgClaim> orgs = await tenancy.GetOrgClaimsAsync(user.Id, app.Id, cancellationToken).ConfigureAwait(false);
        Guid? sid = SangamAuthentication.SessionId(session!);
        SamlSubjectContent subject = new(
            provider.NameIdFormat == "email" ? user.Email : options.PairwiseNameId(user.Id, provider.EntityId),
            provider.NameIdFormat == "email" ? SamlProtocol.EmailNameId : SamlProtocol.PersistentNameId,
            proof.AuthenticatedAt ?? DateTimeOffset.UtcNow,
            "_" + (sid?.ToString("N") ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))),
            SamlProtocol.ClassFor(level),
            [.. SamlRelease.Release(provider, user, orgs).Select(a => a.Claim)]);
        if (sid is Guid sessionId)
        {
            await sessions.RecordAppAsync(sessionId, app.Id, user.Id, cancellationToken).ConfigureAwait(false);
        }

        await audit.WriteAsync(
            new AuditEntry(AuditActions.SamlAssertion, AuditActorType.User, user.Id, app.Id, "app", app.Id,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["entity_id"] = provider.EntityId,
                    ["idp_initiated"] = request.RequestId.Length == 0,
                    ["attributes"] = provider.Attributes,
                    ["acr"] = proof.Acr,
                }),
                IpAddress: http.Connection.RemoteIpAddress?.ToString()),
            cancellationToken).ConfigureAwait(false);
        return PostResponse(http, options, origin, provider, request.AcsUrl, Blank(request.RequestId), request.RelayState, SamlProtocol.Success, null, subject);
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, SamlOptions options, SamlIdentityProvider idp, IAuditWriter audit, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return Results.NotFound();
        }

        LogoutRequestMessage request;
        try
        {
            request = SamlProtocol.ReadLogoutRequest(SamlProtocol.Load(SamlProtocol.InflateRedirect(http.Request.Query["SAMLRequest"].ToString())));
        }
        catch (Exception ex) when (ex is FormatException or XmlException or InvalidDataException)
        {
            return Problem(http, "This is not a SAML logout request Sangam can read."); // i18n-key
        }

        if (await idp.FindAsync(request.Issuer, cancellationToken).ConfigureAwait(false) is not var (provider, app) || provider.SloUrl is null)
        {
            return Problem(http, "This service provider is not registered for logout."); // i18n-key
        }

        if (provider.RequireSignedRequests)
        {
            using X509Certificate2? certificate = SamlIdentityProvider.Certificate(provider.SigningCertificate);
            if (certificate is null || !SamlProtocol.VerifyRedirectSignature(http.Request.QueryString.Value ?? string.Empty, certificate))
            {
                return Problem(http, "This service provider's requests must be signed, and this one is not, or not validly."); // i18n-key
            }
        }

        AuthenticateResult session = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
        Guid? userId = session.Succeeded && session.Principal is not null ? SangamAuthentication.UserId(session.Principal) : null;
        await http.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
        await audit.WriteAsync(
            new AuditEntry(AuditActions.SamlLogout, userId is null ? AuditActorType.Anonymous : AuditActorType.User, userId, app.Id, "app", app.Id,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, string> { ["entity_id"] = provider.EntityId }), IpAddress: http.Connection.RemoteIpAddress?.ToString()),
            cancellationToken).ConfigureAwait(false);

        string origin = Origin(http);
        string xml = SamlProtocol.BuildLogoutResponse(options.EntityIdFor(origin), provider.SloUrl, request.Id, DateTimeOffset.UtcNow, options.Certificates[0]);
        string relay = http.Request.Query["RelayState"].ToString();
        return Results.Redirect(provider.SloUrl + (provider.SloUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?") + SignedRedirectQuery("SAMLResponse", SamlProtocol.DeflateRedirect(xml), relay.Length > 0 ? relay : null, options.Certificates[0]));
    }

    /// <summary>
    /// An HTTP-Redirect query signed as SAML Bindings §3.4.4.1 says: the URL-encoded message, RelayState and SigAlg,
    /// signed with RSA-SHA256.
    /// </summary>
    /// <param name="parameter">SAMLRequest or SAMLResponse.</param>
    /// <param name="deflated">The deflated, base64 message.</param>
    /// <param name="relayState">RelayState, if any.</param>
    /// <param name="signing">Signing certificate.</param>
    public static string SignedRedirectQuery(string parameter, string deflated, string? relayState, X509Certificate2 signing)
    {
        ArgumentNullException.ThrowIfNull(signing);
        string query = parameter + "=" + Uri.EscapeDataString(deflated)
            + (relayState is null ? string.Empty : "&RelayState=" + Uri.EscapeDataString(relayState))
            + "&SigAlg=" + Uri.EscapeDataString(System.Security.Cryptography.Xml.SignedXml.XmlDsigRSASHA256Url);
        using RSA rsa = signing.GetRSAPrivateKey() ?? throw new InvalidOperationException("The SAML signing certificate has no RSA private key.");
        return query + "&Signature=" + Uri.EscapeDataString(Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(query), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
    }

    private static string ContinueUrl(App app, SamlServiceProvider provider, SamlRequest request)
        => "/saml/continue?client_id=" + Uri.EscapeDataString(app.ClientId)
            + "&scope=" + Uri.EscapeDataString(string.Join(' ', Scopes(provider)))
            + (request.RequiredLevel > 0 ? "&acr_values=" + Uri.EscapeDataString("urn:sangam:acr:" + request.RequiredLevel.ToString(CultureInfo.InvariantCulture)) : string.Empty)
            + "&handle=" + request.Handle;

    /// <summary>The scopes consent is asked for, from the attributes released (SGM-215 §3).</summary>
    private static string[] Scopes(SamlServiceProvider provider)
    {
        HashSet<string> attributes = [.. provider.Attributes.Split(',', StringSplitOptions.RemoveEmptyEntries)];
        List<string> scopes = [SangamScopes.OpenId];
        if (attributes.Overlaps(["name", "given_name", "family_name"]))
        {
            scopes.Add(SangamScopes.Profile);
        }

        if (attributes.Contains("email") || provider.NameIdFormat == "email")
        {
            scopes.Add(SangamScopes.Email);
        }

        if (attributes.Overlaps(["roles", "orgs"]))
        {
            scopes.Add(SangamScopes.OrgsRead);
        }

        return [.. scopes];
    }

    private static IResult PostResponse(HttpContext http, SamlOptions options, string origin, SamlServiceProvider provider, string acs, string? inResponseTo, string? relayState, string status, string? subStatus, SamlSubjectContent? subject)
    {
        using X509Certificate2? encryptFor = subject is null ? null : SamlIdentityProvider.Certificate(provider.EncryptionCertificate);
        string xml = SamlProtocol.BuildResponse(
            new SamlResponseContent(options.EntityIdFor(origin), acs, provider.EntityId, inResponseTo, DateTimeOffset.UtcNow, AssertionLifetime, status, subStatus, subject, encryptFor),
            options.Certificates[0]);
        HtmlEncoder html = HtmlEncoder.Default;
        string page = "<!doctype html><html><head><meta charset=\"utf-8\"><title>Sangam</title></head><body>"
            + "<form method=\"post\" action=\"" + html.Encode(acs) + "\">"
            + "<input type=\"hidden\" name=\"SAMLResponse\" value=\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(xml)) + "\">"
            + (relayState is null ? string.Empty : "<input type=\"hidden\" name=\"RelayState\" value=\"" + html.Encode(relayState) + "\">")
            + "<noscript><button type=\"submit\">" + html.Encode(Pages.PageText.For(http)["Continue"]) + "</button></noscript></form>"
            + "<script src=\"/js/saml-post.js\"></script></body></html>";
        http.Response.Headers.CacheControl = "no-store";
        return Results.Content(page, "text/html; charset=utf-8");
    }

    private static IResult Problem(HttpContext http, string message)
    {
        string text = Pages.PageText.For(http)[message];
        return Results.Content("<!doctype html><html><head><meta charset=\"utf-8\"><title>Sangam</title></head><body><p>" + HtmlEncoder.Default.Encode(text) + "</p></body></html>", "text/html; charset=utf-8", Encoding.UTF8, StatusCodes.Status400BadRequest);
    }

    private static string? Blank(string value) => value.Length == 0 ? null : value;
}
