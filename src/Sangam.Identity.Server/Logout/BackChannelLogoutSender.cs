using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Server.Logout;

/// <summary>
/// Delivers queued back-channel logouts (PR-20, OpenID Connect Back-Channel Logout 1.0): a signed logout token
/// (<c>typ</c> <c>logout+jwt</c>, with <c>sub</c>, <c>sid</c> and the logout event) posted to each application's
/// back-channel address. An application that does not answer 200 is tried again with growing pauses, six times in
/// all; nothing it does can hold up a sign-out.
/// </summary>
public sealed partial class BackChannelLogoutSender : BackgroundService
{
    /// <summary>The named HTTP client.</summary>
    public const string HttpClientName = "sangam.backchannel-logout";

    /// <summary>The logout event (§2.4).</summary>
    public const string LogoutEvent = "http://schemas.openid.net/event/backchannel-logout";

    /// <summary>How many times a notification is tried.</summary>
    public const int MaxAttempts = 6;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _http;
    private readonly IOptionsMonitor<OpenIddictServerOptions> _server;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BackChannelLogoutSender> _logger;

    /// <summary>Initialises the sender.</summary>
    /// <param name="scopes">Scope factory, for the database.</param>
    /// <param name="http">HTTP clients.</param>
    /// <param name="server">OpenIddict options, for the signing key and issuer.</param>
    /// <param name="configuration">Configuration (<c>Sangam:Issuer</c>), for the issuer when OpenIddict has none set.</param>
    /// <param name="logger">Logger.</param>
    public BackChannelLogoutSender(IServiceScopeFactory scopes, IHttpClientFactory http, IOptionsMonitor<OpenIddictServerOptions> server, IConfiguration configuration, ILogger<BackChannelLogoutSender> logger)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Delivers every notification that is due now; returns how many were delivered. Also used by tests.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> DeliverDueAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopes.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        int delivered = 0;

        // R7 (PR-33): one replica at a time, so a logout is never posted twice.
        await Sangam.Identity.Infrastructure.Maintenance.ClusterLock.TryRunAsync(db, Sangam.Identity.Infrastructure.Maintenance.ClusterLock.BackChannelLogout,
            async ct => delivered = await DeliverAsync(db, ct).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return delivered;
    }

    private async Task<int> DeliverAsync(SangamDbContext db, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var due = await db.LogoutNotifications
            .Where(n => n.SentAt == null && n.Attempts < MaxAttempts && n.NextAttemptAt <= now)
            .OrderBy(n => n.CreatedAt)
            .Take(20)
            .Join(db.Apps, n => n.AppId, a => a.Id, (n, a) => new { Notification = n, a.ClientId, a.BackChannelLogoutUri })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int delivered = 0;
        HttpClient client = _http.CreateClient(HttpClientName);
        foreach (var item in due)
        {
            LogoutNotification n = item.Notification;
            n.Attempts++;
            string? error = item.BackChannelLogoutUri is null ? "no back-channel address any more" : await PostAsync(client, item.BackChannelLogoutUri, Token(n, item.ClientId), cancellationToken).ConfigureAwait(false);
            if (error is null)
            {
                n.SentAt = DateTimeOffset.UtcNow;
                n.LastError = null;
                delivered++;
            }
            else
            {
                n.LastError = error.Length > 200 ? error[..200] : error;
                n.NextAttemptAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30 * Math.Pow(2, n.Attempts - 1));
                if (n.Attempts >= MaxAttempts)
                {
                    LogGaveUp(item.ClientId, n.Attempts, n.LastError);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return delivered;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Sangam:Logout:DeliverInBackground", true))
        {
            // Tests call DeliverDueAsync themselves, so the background loop cannot race them.
            return;
        }

        using PeriodicTimer timer = new(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await DeliverDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
            {
                LogRoundFailed(ex.GetType().Name);
            }
        }
    }

    private string Token(LogoutNotification n, string clientId)
    {
        SigningCredentials credentials = _server.CurrentValue.SigningCredentials.FirstOrDefault()
            ?? throw new InvalidOperationException("No token-signing credentials are configured.");
        // Production always sets Sangam:Issuer; with none set, OpenIddict uses the request's own address, which a
        // background job does not have, so Development and tests fall back to localhost.
        string issuer = _server.CurrentValue.Issuer?.AbsoluteUri
            ?? (_configuration["Sangam:Issuer"] is { Length: > 0 } configured ? new Uri(configured, UriKind.Absolute).AbsoluteUri : "http://localhost/");
        DateTime now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = clientId,
            TokenType = "logout+jwt",
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(2),
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object>
            {
                ["jti"] = n.Id.ToString("D"),
                ["sub"] = n.UserId.ToString("D"),
                ["sid"] = n.SessionId.ToString("D"),
                ["events"] = new Dictionary<string, object> { [LogoutEvent] = new Dictionary<string, object>() },
            },
        });
    }

    private static async Task<string?> PostAsync(HttpClient client, string uri, string token, CancellationToken cancellationToken)
    {
        try
        {
            using FormUrlEncodedContent body = new(new Dictionary<string, string> { ["logout_token"] = token });
            using HttpResponseMessage response = await client.PostAsync(new Uri(uri), body, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? null : "HTTP " + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return ex.GetType().Name;
        }
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "Back-channel logout to {ClientId} given up after {Attempts} attempts: {Error}")]
    private partial void LogGaveUp(string clientId, int attempts, string error);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Back-channel logout round failed: {Error}")]
    private partial void LogRoundFailed(string error);
}
