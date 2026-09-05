using Microsoft.Extensions.Hosting;

namespace GameShift.SystemAgent.SystemOptimization;

internal interface ISystemOptimizerRecoveryMaintenance
{
    ValueTask RecoverAfterUnexpectedRestartAsync(
        CancellationToken cancellationToken);

    ValueTask RecoverTimedOutRebootExperimentAsync(
        CancellationToken cancellationToken);
}

internal sealed class SystemOptimizerRecoveryHostedService : BackgroundService
{
    private static readonly TimeSpan DefaultCheckInterval =
        TimeSpan.FromMinutes(1);

    private readonly ISystemOptimizerRecoveryMaintenance _maintenance;
    private readonly TimeSpan _checkInterval;

    internal SystemOptimizerRecoveryHostedService(
        ISystemOptimizerRecoveryMaintenance maintenance)
        : this(maintenance, DefaultCheckInterval)
    {
    }

    internal SystemOptimizerRecoveryHostedService(
        ISystemOptimizerRecoveryMaintenance maintenance,
        TimeSpan checkInterval)
    {
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            checkInterval,
            TimeSpan.Zero);

        _maintenance = maintenance;
        _checkInterval = checkInterval;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await _maintenance.RecoverAfterUnexpectedRestartAsync(stoppingToken)
            .ConfigureAwait(false);

        using PeriodicTimer timer = new(_checkInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken)
                   .ConfigureAwait(false))
        {
            await _maintenance
                .RecoverTimedOutRebootExperimentAsync(stoppingToken)
                .ConfigureAwait(false);
        }
    }
}
