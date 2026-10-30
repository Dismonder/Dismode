using Microsoft.Extensions.Hosting;

namespace Dismode.SystemAgent.SystemOptimization;

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
        await RunGuardedAsync(
                _maintenance.RecoverAfterUnexpectedRestartAsync,
                stoppingToken)
            .ConfigureAwait(false);

        using PeriodicTimer timer = new(_checkInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken)
                   .ConfigureAwait(false))
        {
            await RunGuardedAsync(
                    _maintenance.RecoverTimedOutRebootExperimentAsync,
                    stoppingToken)
                .ConfigureAwait(false);
        }
    }

    // A transient store or journal failure must not stop the host (the
    // default BackgroundService behaviour) and take the pipe down with it;
    // the next tick retries.
    private static async Task RunGuardedAsync(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken stoppingToken)
    {
        try
        {
            await operation(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
        }
    }
}
