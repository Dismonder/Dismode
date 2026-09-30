using System.Collections.Concurrent;
using System.Diagnostics;
using Dismode.Core.Domain.Processes;

namespace Dismode.Windows.Processes;

public sealed class ProcessTelemetrySampler
{
    private readonly IProcessIdentityProvider _identityProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<ProcessRuntimeKey, CounterSnapshot>
        _previousCounters = new();

    public ProcessTelemetrySampler(
        IProcessIdentityProvider? identityProvider = null,
        TimeProvider? timeProvider = null)
    {
        _identityProvider = identityProvider ?? new ProcessIdentityProvider();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ProcessTelemetrySample> SampleAsync(
        ProcessIdentity expectedIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                expectedIdentity,
                _identityProvider,
                cancellationToken)
            .ConfigureAwait(false);
        process.Refresh();

        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        TimeSpan totalProcessorTime = process.TotalProcessorTime;
        CounterSnapshot current = new(observedAt, totalProcessorTime);
        double cpuPercent = 0;

        if (_previousCounters.TryGetValue(
                expectedIdentity.RuntimeKey,
                out CounterSnapshot previous))
        {
            double elapsedMilliseconds =
                (current.ObservedAtUtc - previous.ObservedAtUtc)
                .TotalMilliseconds;
            double processorMilliseconds =
                (current.TotalProcessorTime - previous.TotalProcessorTime)
                .TotalMilliseconds;
            if (elapsedMilliseconds > 0 && processorMilliseconds >= 0)
            {
                cpuPercent = Math.Clamp(
                    processorMilliseconds
                    / elapsedMilliseconds
                    / Environment.ProcessorCount
                    * 100,
                    0,
                    100);
            }
        }

        _previousCounters[expectedIdentity.RuntimeKey] = current;
        return new(
            expectedIdentity.RuntimeKey,
            observedAt,
            cpuPercent,
            totalProcessorTime,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            process.Threads.Count,
            process.HandleCount);
    }

    public void Forget(ProcessRuntimeKey runtimeKey)
    {
        _previousCounters.TryRemove(runtimeKey, out _);
    }

    private readonly record struct CounterSnapshot(
        DateTimeOffset ObservedAtUtc,
        TimeSpan TotalProcessorTime);
}
