using Dismode.MemoryOptimizer.Core.Models;

namespace Dismode.MemoryOptimizer.Core.Automation;

public sealed record AutoOptimizationDecision(
    bool ShouldRun,
    OptimizationTrigger Trigger,
    MemoryArea Areas,
    string Reason)
{
    public static AutoOptimizationDecision Wait(string reason) =>
        new(false, OptimizationTrigger.LowMemory, MemoryArea.None, reason);
}

public sealed class AutoOptimizationPolicy
{
    private readonly Lock _sync = new();
    private DateTimeOffset? _cpuIdleSinceUtc;
    private DateTimeOffset? _lastOptimizationUtc;

    public AutoOptimizationDecision Evaluate(
        MemoryOptimizerSettings settings,
        MemorySnapshot memory,
        double cpuUsagePercent,
        DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            return EvaluateCore(settings, memory, cpuUsagePercent, nowUtc);
        }
    }

    private AutoOptimizationDecision EvaluateCore(
        MemoryOptimizerSettings settings,
        MemorySnapshot memory,
        double cpuUsagePercent,
        DateTimeOffset nowUtc)
    {
        settings = MemoryOptimizerSettings.Normalize(settings);
        if (!settings.AutomationEnabled || settings.AutomationPaused)
        {
            _cpuIdleSinceUtc = null;
            return AutoOptimizationDecision.Wait("Automation is paused.");
        }

        if (_lastOptimizationUtc is { } last &&
            nowUtc - last < TimeSpan.FromMinutes(settings.CooldownMinutes))
        {
            return AutoOptimizationDecision.Wait("Cooldown is active.");
        }

        if (cpuUsagePercent <= settings.MaximumIdleCpuPercent)
        {
            _cpuIdleSinceUtc ??= nowUtc;
        }
        else
        {
            _cpuIdleSinceUtc = null;
        }

        double availablePercent = memory.TotalPhysicalBytes == 0
            ? 100
            : memory.AvailablePhysicalBytes * 100d /
                memory.TotalPhysicalBytes;
        bool memoryLow = availablePercent <=
            settings.AvailableMemoryThresholdPercent;
        bool cpuIdleLongEnough = _cpuIdleSinceUtc is { } idleSince &&
            nowUtc - idleSince >= TimeSpan.FromMinutes(
                settings.IdleCpuMinutes);

        if (memoryLow && cpuIdleLongEnough)
        {
            return new(
                true,
                OptimizationTrigger.LowMemory,
                settings.AutomaticAreas,
                "Available RAM is below the threshold and CPU is idle.");
        }

        if (settings.ScheduleEnabled &&
            (_lastOptimizationUtc is null ||
             nowUtc - _lastOptimizationUtc >= TimeSpan.FromMinutes(
                 settings.ScheduleIntervalMinutes)) &&
            cpuIdleLongEnough)
        {
            return new(
                true,
                OptimizationTrigger.Schedule,
                settings.AutomaticAreas,
                "The enabled schedule is due and CPU is idle.");
        }

        return AutoOptimizationDecision.Wait(
            memoryLow
                ? "Waiting for the configured CPU idle window."
                : "Available RAM is above the threshold.");
    }

    public TimeSpan RemainingCooldown(
        MemoryOptimizerSettings settings,
        DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            settings = MemoryOptimizerSettings.Normalize(settings);
            if (_lastOptimizationUtc is not { } last)
            {
                return TimeSpan.Zero;
            }

            TimeSpan remaining =
                TimeSpan.FromMinutes(settings.CooldownMinutes) - (nowUtc - last);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public void RecordCompleted(DateTimeOffset completedAtUtc)
    {
        lock (_sync)
        {
            _lastOptimizationUtc = completedAtUtc;
            _cpuIdleSinceUtc = null;
        }
    }
}
