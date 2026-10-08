using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Infrastructure.Provisioning;

namespace Sangam.Identity.Server.Provisioning;

/// <summary>
/// Runs on the identity server: every few seconds hands new application events to their consumers and delivers what is
/// due (SCIM, PR-23; webhooks, PR-24); about once a minute reconciles the SCIM targets not reconciled for a day. Turned
/// off with <c>Sangam:Provisioning:DeliverInBackground=false</c>, so tests can drive each step themselves.
/// </summary>
public sealed partial class ProvisioningWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProvisioningWorker> _logger;

    /// <summary>Initialises the worker.</summary>
    /// <param name="scopes">Scope factory.</param>
    /// <param name="configuration">Configuration.</param>
    /// <param name="logger">Logger.</param>
    public ProvisioningWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<ProvisioningWorker> logger)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>One round: hand out events, deliver what is due, and (when <paramref name="reconcile"/>) reconcile.</summary>
    /// <param name="reconcile">Whether to reconcile targets that are due.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RunOnceAsync(bool reconcile, CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopes.CreateScope();
        if (reconcile)
        {
            // PR-25: time-limited memberships end within a minute of their time (they stop counting at once).
            await scope.ServiceProvider.GetRequiredService<Sangam.Identity.Infrastructure.Tenancy.MembershipExpiry>().RunAsync(cancellationToken).ConfigureAwait(false);
        }

        await scope.ServiceProvider.GetRequiredService<AppEventDispatcher>().DispatchAsync(cancellationToken).ConfigureAwait(false);
        ScimProvisioner scim = scope.ServiceProvider.GetRequiredService<ScimProvisioner>();
        if (reconcile)
        {
            await scim.ReconcileDueAsync(cancellationToken).ConfigureAwait(false);
        }

        await scim.DeliverDueAsync(cancellationToken).ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<WebhookSender>().DeliverDueAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Sangam:Provisioning:DeliverInBackground", true))
        {
            return;
        }

        using PeriodicTimer timer = new(Interval);
        int round = 0;
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RunOnceAsync(round++ % 12 == 0, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
            {
                LogRoundFailed(ex.GetType().Name, ex.Message);
            }
        }
    }

    [LoggerMessage(EventId = 2100, Level = LogLevel.Warning, Message = "Provisioning round failed: {Error}: {Message}")]
    private partial void LogRoundFailed(string error, string message);
}
