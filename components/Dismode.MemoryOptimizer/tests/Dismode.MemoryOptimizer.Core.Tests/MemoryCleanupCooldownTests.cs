using Dismode.MemoryOptimizer.Core.Automation;
using Dismode.MemoryOptimizer.Core.Models;
using Dismode.MemoryService;

namespace Dismode.MemoryOptimizer.Core.Tests;

[TestClass]
public sealed class MemoryCleanupCooldownTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
    private static readonly MemorySnapshot LowMemory = new(Now, 1000, 100, 2000, 1000, 90);

    [TestMethod]
    [DataRow(OptimizationState.Completed, true)]
    [DataRow(OptimizationState.Failed, true)]
    [DataRow(OptimizationState.PartiallyCompleted, true)]
    [DataRow(OptimizationState.Blocked, false)]
    [DataRow(OptimizationState.AlreadyRunning, false)]
    public void ManualResultControlsAutomaticCooldown(OptimizationState state, bool startsCooldown)
    {
        AutoOptimizationPolicy policy = new();
        MemoryOptimizerSettings settings = new() { IdleCpuMinutes = 1, CooldownMinutes = 5 };
        _ = policy.Evaluate(settings, LowMemory, 1, Now.AddMinutes(-1));
        Assert.IsTrue(policy.Evaluate(settings, LowMemory, 1, Now).ShouldRun);

        MemoryUserRuntime.RecordIfFinished(policy, Result(state));
        int automaticRuns = 0;
        for (int minute = 0; minute < 5; minute++)
        {
            if (policy.Evaluate(settings, LowMemory, 1, Now.AddMinutes(minute)).ShouldRun)
            {
                automaticRuns++;
            }
        }

        Assert.AreEqual(startsCooldown ? 0 : 5, automaticRuns);
        _ = policy.Evaluate(settings, LowMemory, 1, Now.AddMinutes(5));
        if (policy.Evaluate(settings, LowMemory, 1, Now.AddMinutes(6)).ShouldRun)
        {
            automaticRuns++;
        }

        Assert.AreEqual(startsCooldown ? 1 : 6, automaticRuns);
    }

    [TestMethod]
    public void ManualCompletionPostponesSchedule()
    {
        AutoOptimizationPolicy policy = new();
        MemoryOptimizerSettings settings = new()
        {
            ScheduleEnabled = true,
            ScheduleIntervalMinutes = 60,
            CooldownMinutes = 5,
            IdleCpuMinutes = 1,
        };
        MemorySnapshot memory = LowMemory with { AvailablePhysicalBytes = 800 };
        policy.RecordCompleted(Now.AddMinutes(-59));
        MemoryUserRuntime.RecordIfFinished(policy, Result(OptimizationState.Completed));
        _ = policy.Evaluate(settings, memory, 1, Now.AddMinutes(5));
        Assert.IsFalse(policy.Evaluate(settings, memory, 1, Now.AddMinutes(59)).ShouldRun);
        AutoOptimizationDecision due = policy.Evaluate(settings, memory, 1, Now.AddMinutes(60));
        Assert.IsTrue(due.ShouldRun);
        Assert.AreEqual(OptimizationTrigger.Schedule, due.Trigger);
    }

    [TestMethod]
    public async Task ConcurrentCompletionAndEvaluationKeepCooldownVisible()
    {
        AutoOptimizationPolicy policy = new();
        MemoryOptimizerSettings settings = new();
        await Task.WhenAll(
            Task.Run(() =>
            {
                for (int i = 0; i < 10000; i++)
                {
                    MemoryUserRuntime.RecordIfFinished(policy, Result(OptimizationState.Completed));
                }
            }),
            Task.Run(() =>
            {
                for (int i = 0; i < 10000; i++)
                {
                    _ = policy.Evaluate(settings, LowMemory, 1, Now.AddSeconds(1));
                }
            }));
        Assert.IsFalse(policy.Evaluate(settings, LowMemory, 1, Now.AddMinutes(1)).ShouldRun);
    }

    private static OptimizationResult Result(OptimizationState state) => new(
        Guid.NewGuid(), OptimizationTrigger.Manual, MemoryOptimizerSettings.BasicAreas,
        state, Now.AddSeconds(-1), Now, LowMemory, LowMemory, [], "");
}
