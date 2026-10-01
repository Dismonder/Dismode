using Dismode.MemoryOptimizer.Core.Automation;
using Dismode.MemoryOptimizer.Core.Models;

namespace Dismode.MemoryOptimizer.Core.Tests;

[TestClass]
public sealed class AutoOptimizationPolicyTests
{
    [TestMethod]
    public void LowMemoryRequiresFiveMinuteCpuIdleWindow()
    {
        AutoOptimizationPolicy policy = new();
        MemoryOptimizerSettings settings = new();
        DateTimeOffset now = new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);
        MemorySnapshot memory = Snapshot(availablePercent: 15);

        AutoOptimizationDecision first = policy.Evaluate(
            settings,
            memory,
            cpuUsagePercent: 3,
            now);
        AutoOptimizationDecision ready = policy.Evaluate(
            settings,
            memory,
            cpuUsagePercent: 3,
            now.AddMinutes(5));

        Assert.IsFalse(first.ShouldRun);
        Assert.IsTrue(ready.ShouldRun);
        Assert.AreEqual(OptimizationTrigger.LowMemory, ready.Trigger);
        Assert.AreEqual(
            MemoryOptimizerSettings.BasicAreas,
            ready.Areas);
    }

    [TestMethod]
    public void CooldownPreventsRepeatedAutomaticRun()
    {
        AutoOptimizationPolicy policy = new();
        MemoryOptimizerSettings settings = new();
        DateTimeOffset now = new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);
        MemorySnapshot memory = Snapshot(availablePercent: 10);
        _ = policy.Evaluate(settings, memory, 1, now);
        AutoOptimizationDecision ready = policy.Evaluate(
            settings,
            memory,
            1,
            now.AddMinutes(5));
        Assert.IsTrue(ready.ShouldRun);

        policy.RecordCompleted(now.AddMinutes(5));
        AutoOptimizationDecision cooldown = policy.Evaluate(
            settings,
            memory,
            1,
            now.AddMinutes(20));

        Assert.IsFalse(cooldown.ShouldRun);
        StringAssert.Contains(cooldown.Reason, "Cooldown");
    }

    [TestMethod]
    public void ScheduleRemainsDisabledByDefault()
    {
        AutoOptimizationPolicy policy = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MemorySnapshot memory = Snapshot(availablePercent: 80);
        _ = policy.Evaluate(new(), memory, 1, now);

        AutoOptimizationDecision decision = policy.Evaluate(
            new(),
            memory,
            1,
            now.AddHours(24));

        Assert.IsFalse(decision.ShouldRun);
    }

    private static MemorySnapshot Snapshot(int availablePercent)
    {
        const ulong total = 1000;
        return new(
            DateTimeOffset.UtcNow,
            total,
            checked((ulong)(availablePercent * 10)),
            2000,
            1000,
            checked((uint)(100 - availablePercent)));
    }
}
