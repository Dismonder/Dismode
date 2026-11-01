using Dismode.Core.SystemOptimization;
using Microsoft.Extensions.Hosting;

namespace Dismode.SystemAgent.SystemOptimization;

internal interface ISystemOptimizerRecoveryMaintenance
{
    ValueTask RecoverAfterUnexpectedRestartAsync(
        CancellationToken cancellationToken);

    ValueTask RecoverTimedOutRebootExperimentAsync(
        CancellationToken cancellationToken);

    ValueTask WaitForPendingTimeoutAsync(
        TimeSpan idleFallback,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

internal sealed class SystemOptimizerRecoveryHostedService : BackgroundService
{
    private static readonly TimeSpan DefaultCheckInterval =
        TimeSpan.FromMinutes(1);

    private readonly ISystemOptimizerRecoveryMaintenance _maintenance;
    private readonly TimeSpan _checkInterval;
    private readonly TimeProvider _clock;

    internal SystemOptimizerRecoveryHostedService(
        ISystemOptimizerRecoveryMaintenance maintenance)
        : this(maintenance, DefaultCheckInterval)
    {
    }

    internal SystemOptimizerRecoveryHostedService(
        ISystemOptimizerRecoveryMaintenance maintenance,
        TimeSpan checkInterval,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            checkInterval,
            TimeSpan.Zero);

        _maintenance = maintenance;
        _checkInterval = checkInterval;
        _clock = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await RunGuardedAsync(
                _maintenance.RecoverAfterUnexpectedRestartAsync,
                stoppingToken)
            .ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunGuardedAsync(
                    token => _maintenance.WaitForPendingTimeoutAsync(
                        SystemOptimizerRecoveryStateMachine.ConfirmationTimeout,
                        token),
                    stoppingToken)
                .ConfigureAwait(false);
            await Task.Delay(_checkInterval, _clock, stoppingToken)
                .ConfigureAwait(false);
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
