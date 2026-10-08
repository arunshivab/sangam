using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Accounts;

namespace Sangam.Identity.Infrastructure.Maintenance;

/// <summary>
/// Applies support resets of two-step sign-in once their cooling-off period has passed uncancelled (D-K). Runs every
/// minute; each request is claimed atomically, so several hosts running this never apply one twice.
/// </summary>
public sealed partial class MfaResetApplier : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MfaResetApplier> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;

    /// <summary>Initialises the service.</summary>
    /// <param name="scopeFactory">Scope factory.</param>
    /// <param name="configuration">Reads <c>Sangam:Maintenance:Enabled</c> and <c>Sangam:Recovery:CheckInterval</c>.</param>
    /// <param name="logger">Logger.</param>
    public MfaResetApplier(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<MfaResetApplier> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enabled = configuration.GetValue("Sangam:Maintenance:Enabled", true);
        _interval = configuration.GetValue("Sangam:Recovery:CheckInterval", TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            return;
        }

        using PeriodicTimer timer = new(_interval);
        do
        {
            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                int applied = await scope.ServiceProvider.GetRequiredService<IMfaResetService>().ApplyDueAsync(stoppingToken).ConfigureAwait(false);
                if (applied > 0)
                {
                    LogApplied(applied);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(EventId = 1501, Level = LogLevel.Information, Message = "Applied {Count} two-step reset(s) whose cooling-off period had passed")]
    private partial void LogApplied(int count);

    [LoggerMessage(EventId = 1502, Level = LogLevel.Error, Message = "Applying due two-step resets failed")]
    private partial void LogFailed(Exception exception);
}
