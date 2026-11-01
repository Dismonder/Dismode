using Dismode.SystemAgent.SystemOptimization;

namespace Dismode.IntegrationTests.SystemOptimization;

[TestClass]
public sealed class SystemOptimizerRecoveryHostedServiceTests
{
    [TestMethod]
    public async Task StartAsyncRunsStartupRecoveryBeforeWaiting()
    {
        FakeRecoveryMaintenance maintenance = new();
        using SystemOptimizerRecoveryHostedService service = new(
            maintenance,
            TimeSpan.FromHours(1));

        await service.StartAsync(CancellationToken.None);
        await maintenance.StartupRecoveryObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, maintenance.StartupRecoveryCount);
        await service.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task ExecuteAsyncChecksTimedOutRecoveryPeriodically()
    {
        FakeRecoveryMaintenance maintenance = new();
        using SystemOptimizerRecoveryHostedService service = new(
            maintenance,
            TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        await maintenance.TimeoutRecoveryObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.IsGreaterThanOrEqualTo(maintenance.TimeoutRecoveryCount, 1);
        await service.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task MaintenanceFailuresDoNotStopTheHost()
    {
        FakeRecoveryMaintenance maintenance = new()
        {
            ThrowOnStartup = true,
            ThrowOnFirstTimeoutRecovery = true,
        };
        using SystemOptimizerRecoveryHostedService service = new(
            maintenance,
            TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        await maintenance.SecondTimeoutRecoveryObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.IsNotNull(service.ExecuteTask);
        Assert.IsFalse(service.ExecuteTask.IsFaulted);
        await service.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task ExecuteAsyncWaitsWhileIdleAndWakesWhenPendingTimeoutAppears()
    {
        using ManualClock clock = new();
        TaskCompletionSource wake = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FakeRecoveryMaintenance maintenance = new()
        {
            PendingTimeoutGate = wake.Task,
        };
        using SystemOptimizerRecoveryHostedService service = new(
            maintenance,
            TimeSpan.FromMinutes(1),
            clock);

        await service.StartAsync(CancellationToken.None);
        await maintenance.IdleWaitObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.AreEqual(0, maintenance.TimeoutRecoveryCount);

        wake.SetResult();
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.AreEqual(0, maintenance.TimeoutRecoveryCount);
        clock.Advance(TimeSpan.FromSeconds(1));
        await maintenance.TimeoutRecoveryObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.IsGreaterThanOrEqualTo(maintenance.TimeoutRecoveryCount, 1);
        await service.StopAsync(CancellationToken.None);
    }

    private sealed class FakeRecoveryMaintenance :
        ISystemOptimizerRecoveryMaintenance
    {
        internal TaskCompletionSource StartupRecoveryObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource TimeoutRecoveryObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource SecondTimeoutRecoveryObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task? PendingTimeoutGate { get; init; }

        internal TaskCompletionSource IdleWaitObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool ThrowOnStartup { get; init; }

        internal bool ThrowOnFirstTimeoutRecovery { get; init; }

        internal int StartupRecoveryCount { get; private set; }

        internal int TimeoutRecoveryCount { get; private set; }

        public ValueTask RecoverAfterUnexpectedRestartAsync(
            CancellationToken cancellationToken)
        {
            StartupRecoveryCount++;
            StartupRecoveryObserved.TrySetResult();
            if (ThrowOnStartup)
            {
                throw new IOException("startup failure");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask RecoverTimedOutRebootExperimentAsync(
            CancellationToken cancellationToken)
        {
            TimeoutRecoveryCount++;
            TimeoutRecoveryObserved.TrySetResult();
            if (TimeoutRecoveryCount >= 2)
            {
                SecondTimeoutRecoveryObserved.TrySetResult();
            }

            if (ThrowOnFirstTimeoutRecovery && TimeoutRecoveryCount == 1)
            {
                throw new InvalidOperationException("tick failure");
            }

            return ValueTask.CompletedTask;
        }

        public async ValueTask WaitForPendingTimeoutAsync(
            TimeSpan idleFallback,
            CancellationToken cancellationToken)
        {
            IdleWaitObserved.TrySetResult();
            if (PendingTimeoutGate is not null)
            {
                await PendingTimeoutGate.WaitAsync(idleFallback, cancellationToken);
            }
        }
    }

    private sealed class ManualClock : TimeProvider, IDisposable
    {
        private TimeSpan _elapsed;
        private ClockTimer? _timer;
        public void Dispose() => _timer?.Dispose();
        internal TaskCompletionSource TimerCreated { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timer = new(callback, state, _elapsed + dueTime);
            TimerCreated.TrySetResult();
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
