using GameShift.MemoryOptimizer.Core.Activity;
using GameShift.MemoryOptimizer.Core.Models;
using GameShift.MemoryOptimizer.Core.Native;

namespace GameShift.MemoryOptimizer.Core.Optimization;

public sealed class MemoryOptimizerEngine : IDisposable
{
    private static readonly MemoryArea[] OperationOrder =
    [
        MemoryArea.WorkingSet,
        MemoryArea.SystemFileCache,
        MemoryArea.ModifiedPageList,
        MemoryArea.StandbyList,
        MemoryArea.StandbyListLowPriority,
        MemoryArea.CombinedPageList,
        MemoryArea.RegistryCache,
        MemoryArea.ModifiedFileCache,
    ];

    private readonly IMemoryAreaOperations _operations;
    private readonly IMemoryMetricsSource _metrics;
    private readonly IGameActivityGuard _activityGuard;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);

    public MemoryOptimizerEngine(
        IMemoryAreaOperations operations,
        IMemoryMetricsSource metrics,
        IGameActivityGuard activityGuard)
    {
        _operations = operations ??
            throw new ArgumentNullException(nameof(operations));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _activityGuard = activityGuard ??
            throw new ArgumentNullException(nameof(activityGuard));
    }

    public bool IsRunning => _singleFlight.CurrentCount == 0;

    public void Dispose() => _singleFlight.Dispose();

    public async Task<OptimizationResult> OptimizeAsync(
        OptimizationTrigger trigger,
        MemoryArea requestedAreas,
        MemoryOptimizerSettings settings,
        CancellationToken cancellationToken)
    {
        settings = MemoryOptimizerSettings.Normalize(settings);
        MemoryArea allowedAreas = trigger is OptimizationTrigger.LowMemory or
            OptimizationTrigger.Schedule
                ? settings.AutomaticAreas
                : settings.ManualAreas;
        MemoryArea areas = requestedAreas & allowedAreas;
        if (areas == MemoryArea.None)
        {
            areas = allowedAreas;
        }

        if (!await _singleFlight.WaitAsync(
                TimeSpan.Zero,
                cancellationToken).ConfigureAwait(false))
        {
            MemorySnapshot snapshot = _metrics.Capture();
            return new(
                Guid.NewGuid(),
                trigger,
                areas,
                OptimizationState.AlreadyRunning,
                snapshot.CapturedAtUtc,
                snapshot.CapturedAtUtc,
                snapshot,
                snapshot,
                Array.Empty<MemoryAreaResult>(),
                "Another memory optimization is already running.");
        }

        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        MemorySnapshot before = _metrics.Capture();
        List<MemoryAreaResult> results = [];
        try
        {
            GuardDecision guard = await _activityGuard.EvaluateAsync(
                cancellationToken).ConfigureAwait(false);
            if (guard.IsBlocked)
            {
                return new(
                    Guid.NewGuid(),
                    trigger,
                    areas,
                    OptimizationState.Blocked,
                    startedAtUtc,
                    DateTimeOffset.UtcNow,
                    before,
                    before,
                    results,
                    guard.Message);
            }

            foreach (MemoryArea area in OperationOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((areas & area) == 0)
                {
                    continue;
                }

                results.Add(_operations.Execute(
                    area,
                    settings,
                    cancellationToken));
            }

            MemorySnapshot after = _metrics.Capture();
            bool allSucceeded = results.Count > 0 &&
                results.All(static result => result.Succeeded);
            bool anySucceeded = results.Any(static result => result.Succeeded);
            OptimizationState state = allSucceeded
                ? OptimizationState.Completed
                : anySucceeded
                    ? OptimizationState.PartiallyCompleted
                    : OptimizationState.Failed;
            return new(
                Guid.NewGuid(),
                trigger,
                areas,
                state,
                startedAtUtc,
                DateTimeOffset.UtcNow,
                before,
                after,
                results,
                allSucceeded
                    ? "Memory optimization completed."
                    : "One or more memory areas could not be optimized.");
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            MemorySnapshot after = _metrics.Capture();
            return new(
                Guid.NewGuid(),
                trigger,
                areas,
                OptimizationState.Cancelled,
                startedAtUtc,
                DateTimeOffset.UtcNow,
                before,
                after,
                results,
                "Memory optimization was cancelled between operations.");
        }
        finally
        {
            _singleFlight.Release();
        }
    }
}
