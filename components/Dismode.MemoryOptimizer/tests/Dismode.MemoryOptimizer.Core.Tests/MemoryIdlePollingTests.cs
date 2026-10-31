using Dismode.MemoryOptimizer.Core.Automation;
using Dismode.MemoryOptimizer.Core.Models;
using Dismode.MemoryService;

namespace Dismode.MemoryOptimizer.Core.Tests;

[TestClass]
public sealed class MemoryIdlePollingTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public void DisabledAutomationDoesNotSampleHardware(bool enabled, bool paused)
    {
        MemoryOptimizerSettings settings = new() { AutomationEnabled = enabled, AutomationPaused = paused };
        AutoOptimizationDecision decision = MemoryAutomationPolling.Evaluate(
            new(), settings,
            () => throw new AssertFailedException("Memory was sampled."),
            () => throw new AssertFailedException("CPU was sampled."),
            DateTimeOffset.UnixEpoch);
        Assert.IsFalse(decision.ShouldRun);
        Assert.AreEqual(TimeSpan.FromMinutes(1), MemoryAutomationPolling.Interval(settings));
    }

    [TestMethod]
    public void EnabledAutomationKeepsConfiguredCadenceAndSamples()
    {
        int samples = 0;
        MemoryOptimizerSettings settings = new() { PollIntervalSeconds = 5 };
        _ = MemoryAutomationPolling.Evaluate(new(), settings,
            () => { samples++; return new(DateTimeOffset.UnixEpoch, 100, 90, 100, 90, 10); },
            () => { samples++; return 1; }, DateTimeOffset.UnixEpoch);
        Assert.AreEqual(2, samples);
        Assert.AreEqual(TimeSpan.FromSeconds(5), MemoryAutomationPolling.Interval(settings));
    }

    [TestMethod]
    public void CooldownWindowSkipsHardwareSamplingAndExtendsIdleInterval()
    {
        AutoOptimizationPolicy policy = new();
        DateTimeOffset completedAt = DateTimeOffset.UnixEpoch;
        policy.RecordCompleted(completedAt);
        MemoryOptimizerSettings settings = new()
        {
            PollIntervalSeconds = 5,
            CooldownMinutes = 10,
        };

        AutoOptimizationDecision decision = MemoryAutomationPolling.Evaluate(
            policy,
            settings,
            () => throw new AssertFailedException("Memory was sampled during cooldown."),
            () => throw new AssertFailedException("CPU was sampled during cooldown."),
            completedAt.AddMinutes(2));

        Assert.IsFalse(decision.ShouldRun);
        Assert.AreEqual(
            TimeSpan.FromMinutes(1),
            MemoryAutomationPolling.Interval(
                settings,
                policy,
                completedAt.AddMinutes(2)));
        Assert.AreEqual(
            TimeSpan.FromSeconds(30),
            MemoryAutomationPolling.Interval(
                settings,
                policy,
                completedAt.AddMinutes(9).AddSeconds(30)));
    }

    [TestMethod]
    public async Task WakeInterruptsIdleWaitAndNotificationsCoalesce()
    {
        using ServiceWakeSignal signal = new();
        signal.Wake();
        signal.Wake();
        await signal.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Task waiting = signal.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.IsFalse(waiting.IsCompleted);
        signal.Wake();
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task FakeClockExpiresFallbackAndCancellationStopsWaiting()
    {
        using ManualClock clock = new();
        using ServiceWakeSignal signal = new(clock);
        Task waiting = signal.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.IsFalse(waiting.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.IsFalse(waiting.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        using CancellationTokenSource cancellation = new();
        waiting = signal.WaitAsync(TimeSpan.FromMinutes(1), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await waiting);
    }

    private sealed class ManualClock : TimeProvider, IDisposable
    {
        private TimeSpan _elapsed;
        private ClockTimer? _timer;

        public void Dispose() => _timer?.Dispose();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timer = new(callback, state, _elapsed + dueTime);
            return _timer;
        }

        internal void Advance(TimeSpan elapsed)
        {
            _elapsed += elapsed;
            if (_timer is { IsDisposed: false } timer && _elapsed >= timer.Due)
            {
                timer.Callback(timer.State);
            }
        }

        private sealed class ClockTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            internal TimerCallback Callback { get; } = callback;
            internal object? State { get; } = state;
            internal TimeSpan Due { get; } = due;
            internal bool IsDisposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => IsDisposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
