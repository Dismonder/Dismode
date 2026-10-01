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

    private sealed class FakeRecoveryMaintenance :
        ISystemOptimizerRecoveryMaintenance
    {
        internal TaskCompletionSource StartupRecoveryObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource TimeoutRecoveryObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int StartupRecoveryCount { get; private set; }

        internal int TimeoutRecoveryCount { get; private set; }

        public ValueTask RecoverAfterUnexpectedRestartAsync(
            CancellationToken cancellationToken)
        {
            StartupRecoveryCount++;
            StartupRecoveryObserved.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask RecoverTimedOutRebootExperimentAsync(
            CancellationToken cancellationToken)
        {
            TimeoutRecoveryCount++;
            TimeoutRecoveryObserved.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
