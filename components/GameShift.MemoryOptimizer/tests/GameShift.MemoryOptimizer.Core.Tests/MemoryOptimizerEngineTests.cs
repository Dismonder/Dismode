using GameShift.MemoryOptimizer.Core.Activity;
using GameShift.MemoryOptimizer.Core.Models;
using GameShift.MemoryOptimizer.Core.Native;
using GameShift.MemoryOptimizer.Core.Optimization;

namespace GameShift.MemoryOptimizer.Core.Tests;

[TestClass]
public sealed class MemoryOptimizerEngineTests
{
    [TestMethod]
    public async Task ExecutesSelectedAreasInStableOrder()
    {
        FakeOperations operations = new();
        using MemoryOptimizerEngine engine = new(
            operations,
            new FakeMetrics(),
            new AllowAllGameActivityGuard());
        MemoryOptimizerSettings settings = new()
        {
            AdvancedModeEnabled = true,
            AdvancedWarningAccepted = true,
            ManualAreas = MemoryArea.WorkingSet |
                MemoryArea.ModifiedPageList |
                MemoryArea.RegistryCache,
        };

        OptimizationResult result = await engine.OptimizeAsync(
            OptimizationTrigger.Manual,
            settings.ManualAreas,
            settings,
            CancellationToken.None);

        Assert.AreEqual(OptimizationState.Completed, result.State);
        CollectionAssert.AreEqual(
            new[]
            {
                MemoryArea.WorkingSet,
                MemoryArea.ModifiedPageList,
                MemoryArea.RegistryCache,
            },
            operations.Executed.ToArray());
    }

    [TestMethod]
    public async Task GameGuardBlocksEveryManualOperation()
    {
        FakeOperations operations = new();
        using MemoryOptimizerEngine engine = new(
            operations,
            new FakeMetrics(),
            new BlockingDecisionGuard());

        OptimizationResult result = await engine.OptimizeAsync(
            OptimizationTrigger.Manual,
            MemoryOptimizerSettings.BasicAreas,
            new(),
            CancellationToken.None);

        Assert.AreEqual(OptimizationState.Blocked, result.State);
        Assert.HasCount(0, operations.Executed);
    }

    [TestMethod]
    public async Task ConcurrentRequestReturnsAlreadyRunning()
    {
        PendingGuard guard = new();
        using MemoryOptimizerEngine engine = new(
            new FakeOperations(),
            new FakeMetrics(),
            guard);
        Task<OptimizationResult> first = engine.OptimizeAsync(
            OptimizationTrigger.Manual,
            MemoryOptimizerSettings.BasicAreas,
            new(),
            CancellationToken.None);
        await guard.Entered.Task;

        OptimizationResult second = await engine.OptimizeAsync(
            OptimizationTrigger.Manual,
            MemoryOptimizerSettings.BasicAreas,
            new(),
            CancellationToken.None);
        guard.Release.SetResult(GuardDecision.Allowed);
        _ = await first;

        Assert.AreEqual(OptimizationState.AlreadyRunning, second.State);
    }

    private sealed class FakeOperations : IMemoryAreaOperations
    {
        internal List<MemoryArea> Executed { get; } = [];

        public MemoryAreaResult Execute(
            MemoryArea area,
            MemoryOptimizerSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Executed.Add(area);
            return new(area, true, TimeSpan.Zero, null, "ok");
        }
    }

    private sealed class FakeMetrics : IMemoryMetricsSource
    {
        public MemorySnapshot Capture() => new(
            DateTimeOffset.UtcNow,
            1000,
            500,
            2000,
            1000,
            50);
    }

    private sealed class BlockingDecisionGuard : IGameActivityGuard
    {
        public ValueTask<GuardDecision> EvaluateAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new GuardDecision(true, "game", "blocked"));
    }

    private sealed class PendingGuard : IGameActivityGuard
    {
        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<GuardDecision> Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<GuardDecision> EvaluateAsync(
            CancellationToken cancellationToken)
        {
            Entered.SetResult();
            return new(Release.Task.WaitAsync(cancellationToken));
        }
    }
}
