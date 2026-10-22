using Microsoft.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Sangam.Identity.Infrastructure.Persistence;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Sangam.Identity.Server.Endpoints;

/// <summary>
/// rc.15 (ASVS V2.10.1): what Sangam requires of a client assertion (<c>private_key_jwt</c>, RFC 7523) beyond OpenIddict's
/// own checks of its signature, type, issuer and subject (the client id), audience (Sangam's issuer, exactly, as
/// draft-ietf-oauth-rfc7523bis asks) and expiry:
/// <list type="bullet">
/// <item>it lives five minutes at most (<c>iat</c> to <c>exp</c>), has not expired, and has a <c>jti</c>;</item>
/// <item>it is used once: an assertion seen before is refused, on every host (the database keeps each accepted <c>jti</c>
/// until ten minutes after the assertion expires, longer than it can be accepted).</item>
/// </list>
/// A refusal is <c>invalid_client</c>, audited like any other. When it is accepted, the key it was signed with is marked
/// as used, to the hour, for the partner console.
/// </summary>
public static class ClientAssertionChecks
{
    /// <summary>The longest an assertion may live.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The clock difference allowed between the application's servers and Sangam.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>How long after its expiry an accepted assertion's <c>jti</c> is kept: longer than any clock allowance.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan UseResolution = TimeSpan.FromHours(1);

    /// <summary>Registers the checks.</summary>
    /// <param name="builder">OpenIddict's server builder.</param>
    public static OpenIddictServerBuilder AddClientAssertionChecks(this OpenIddictServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddEventHandler<OpenIddictServerEvents.ProcessAuthenticationContext>(h => h
            .UseInlineHandler(CheckAsync)
            .SetOrder(OpenIddictServerHandlers.ValidateClientAssertionIssuer.Descriptor.Order + 500)
            .SetType(OpenIddictServerHandlerType.Custom));
        return builder;
    }

    private static async ValueTask CheckAsync(OpenIddictServerEvents.ProcessAuthenticationContext context)
    {
        if (context.ClientAssertionPrincipal is not { } assertion || context.Transaction.GetHttpRequest()?.HttpContext is not { } http)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string? clientId = assertion.GetClaim(Claims.Issuer);

        // Its whole life (iat to exp) is five minutes at most, it was made no later than now, and it has not expired. The
        // last is checked here as well as by OpenIddict, whose allowance for clock difference is five minutes: Sangam allows
        // one, and keeps each accepted jti well past that, so an assertion cannot come back once its record has gone.
        DateTimeOffset? expires = assertion.GetExpirationDate();
        DateTimeOffset? issued = assertion.GetCreationDate();
        if (expires is null || issued is null || expires - issued > MaxLifetime || issued > now + ClockSkew || expires < now - ClockSkew)
        {
            context.Reject(Errors.InvalidClient, "The client assertion must say when it was made (iat) and expire (exp) within five minutes of it, and must not have expired.");
            return;
        }

        string? jti = assertion.GetClaim(Claims.JwtId);
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(jti) || jti.Length > 200)
        {
            context.Reject(Errors.InvalidClient, "The client assertion needs a unique id (jti) of at most 200 characters.");
            return;
        }

        SangamDbContext db = http.RequestServices.GetRequiredService<SangamDbContext>();
        CancellationToken cancellationToken = http.RequestAborted;
        await db.ClientAssertionUses.Where(u => u.ExpiresAt < now - KeepFor).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        int recorded = await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO client_assertion_uses (client_id, token_id, expires_at) VALUES ({clientId}, {jti}, {expires.Value}) ON CONFLICT DO NOTHING",
            cancellationToken).ConfigureAwait(false);
        if (recorded == 0)
        {
            context.Reject(Errors.InvalidClient, "That client assertion was already used. Sign a new one for each request.");
            return;
        }

        string? kid = KeyIdOf(context.ClientAssertion);
        if (kid is not null)
        {
            DateTimeOffset stale = now - UseResolution;
            await db.ClientKeys
                .Where(k => k.KeyId == kid && k.RevokedAt == null && (k.LastUsedAt == null || k.LastUsedAt < stale) && db.Apps.Any(a => a.Id == k.AppId && a.ClientId == clientId))
                .ExecuteUpdateAsync(u => u.SetProperty(k => k.LastUsedAt, now), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? KeyIdOf(string? assertion)
    {
        try
        {
            return string.IsNullOrEmpty(assertion) ? null : new JsonWebToken(assertion).Kid;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
