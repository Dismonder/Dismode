using Dismode.MemoryOptimizer.Core.Automation;
using Dismode.MemoryOptimizer.Core.Models;

namespace Dismode.MemoryService;

internal static class MemoryAutomationPolling
{
    internal static TimeSpan Interval(
        MemoryOptimizerSettings settings,
        AutoOptimizationPolicy? policy = null,
        DateTimeOffset? now = null)
    {
        if (!settings.AutomationEnabled || settings.AutomationPaused)
        {
            return TimeSpan.FromMinutes(1);
        }

        if (policy is not null && now is { } nowUtc)
        {
            TimeSpan cooldown = policy.RemainingCooldown(settings, nowUtc);
            if (cooldown > TimeSpan.Zero)
            {
                return cooldown < TimeSpan.FromMinutes(1)
                    ? cooldown
                    : TimeSpan.FromMinutes(1);
            }
        }

        return TimeSpan.FromSeconds(settings.PollIntervalSeconds);
    }

    internal static AutoOptimizationDecision Evaluate(
        AutoOptimizationPolicy policy,
        MemoryOptimizerSettings settings,
        Func<MemorySnapshot> captureMemory,
        Func<double> sampleCpu,
        DateTimeOffset now)
    {
        bool active = settings.AutomationEnabled
            && !settings.AutomationPaused
            && policy.RemainingCooldown(settings, now) <= TimeSpan.Zero;
        return policy.Evaluate(settings,
            active ? captureMemory() : new(now, 0, 0, 0, 0, 0),
            active ? sampleCpu() : 0,
            now);
    }
}
