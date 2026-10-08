using System.Security.Cryptography;
using System.Text;
using Sangam.Identity.Application.Sms;
using Sangam.Identity.Infrastructure.Sms;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Endpoints;

/// <summary>
/// The SMS delivery-report webhook (PR-15, SGM-206 §4). It accepts Sangam's provider-neutral report; each
/// provider's own format is mapped to it by that provider's adapter, added with the founder's choice of
/// provider. The provider must present <c>Sangam:Sms:DeliveryReportToken</c>; with no token set the webhook is off.
/// </summary>
public static class SmsEndpoints
{
    /// <summary>The header carrying the shared token.</summary>
    public const string TokenHeader = "X-Sangam-Sms-Token";

    /// <summary>Maps <c>POST /sms/delivery-report</c>.</summary>
    /// <param name="endpoints">Route builder.</param>
    public static IEndpointRouteBuilder MapSmsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/sms/delivery-report", ReceiveAsync)
            .WithName("SmsDeliveryReport")
            .DisableAntiforgery()
            .RequireRateLimiting(AuthRateLimiting.TokenPolicy);
        return endpoints;
    }

    private static async Task<IResult> ReceiveAsync(HttpContext context, SmsSettings settings, ISmsCodeService sms, DeliveryReportBody? body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(settings.DeliveryReportToken))
        {
            return Results.NotFound();
        }

        string presented = context.Request.Headers[TokenHeader].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(settings.DeliveryReportToken)))
        {
            return Results.Unauthorized();
        }

        if (body is null || string.IsNullOrWhiteSpace(body.Provider) || string.IsNullOrWhiteSpace(body.MessageId))
        {
            return Results.BadRequest();
        }

        bool matched = await sms.RecordDeliveryAsync(
            new SmsDeliveryReport(body.Provider, body.MessageId, body.Delivered, body.At ?? DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        return matched ? Results.NoContent() : Results.NotFound();
    }

    /// <summary>Sangam's provider-neutral delivery report.</summary>
    /// <param name="Provider">The provider's name in settings.</param>
    /// <param name="MessageId">The provider's id for the message.</param>
    /// <param name="Delivered">Whether the handset received it.</param>
    /// <param name="At">When, if the provider says.</param>
    public sealed record DeliveryReportBody(string Provider, string MessageId, bool Delivered, DateTimeOffset? At);
}
