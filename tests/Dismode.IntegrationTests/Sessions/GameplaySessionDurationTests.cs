using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Core.History;
using Dismode.Core.Journal;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests.Sessions;

[TestClass]
public sealed class GameplaySessionDurationTests
{
    [TestMethod]
    public async Task SlowRestoreDoesNotExtendDurationWhileMonitorIsRunning()
    {
        SlowCoordinator coordinator = new();
        await using SyntheticSession session = new(systemProfileCoordinator: coordinator);
        await session.StartAsync(systemProfileActive: true);
        session.Clock.Advance(10);
        Task restore = session.Orchestrator.RestoreAsync(session.Id, CancellationToken.None).AsTask();
        try
        {
            await coordinator.Restoring.Task.WaitAsync(TimeSpan.FromSeconds(5));
            session.Clock.Advance(600);
            await Task.Delay(30);
        }
        finally
        {
            coordinator.Continue.TrySetResult();
        }
        await restore;
        var history = await session.Store.ListRecentAsync(10, CancellationToken.None);
        Assert.AreEqual(10L, history.Single().DurationSeconds);
    }

    private sealed class SlowCoordinator : ISystemGameProfileCoordinator
    {
        internal TaskCompletionSource Restoring { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<SystemGameProfileOperationResult> ActivateAsync(GameProfileId profileId,
            ProcessIdentity gameIdentity, CancellationToken cancellationToken) =>
            ValueTask.FromResult(SystemGameProfileOperationResult.Applied("test"));
        public async ValueTask<SystemGameProfileOperationResult> RestoreAsync(GameProfileId profileId, CancellationToken cancellationToken)
        {
            Restoring.TrySetResult();
            await Continue.Task;
            return SystemGameProfileOperationResult.Restored("test");
        }
    }

    [TestMethod]
    public async Task ExitExcludesHandoffWaitFromPersistedDuration()
    {
        await using SyntheticSession session = new();
        await session.StartAsync();
        session.Clock.Advance(15);
        await session.WaitForCheckpointAsync(SessionCheckpoint.GameplayDurationObserved);
        session.Running.Clear();
        session.Clock.Advance(4);
        await session.WaitForCheckpointAsync(SessionCheckpoint.GameHandoffWaiting);
        session.Clock.Advance(30);
        await session.WaitForCheckpointAsync(SessionCheckpoint.ReconciliationComplete);
        var history = await session.Store.ListRecentAsync(10, CancellationToken.None);
        Assert.AreEqual(15L, history.Single().DurationSeconds);
        Assert.AreEqual(session.Root.RuntimeKey.StartedAtUtc.AddSeconds(25), history.Single().EndedAtUtc);
    }

    [TestMethod]
    public async Task CrashRecoveryUsesLastCheckpointInsteadOfDowntime()
    {
        await using SyntheticSession session = new();
        await session.StartAsync();
        session.Clock.Advance(42);
        await session.WaitForCheckpointAsync(SessionCheckpoint.GameplayDurationObserved);
        await session.Orchestrator.DisposeAsync();
        session.Running.Clear();
        session.Clock.Advance(3600);
        await using LocalGameSessionOrchestrator recovery = new(session.Store, session.Store,
            session.Journal, identityProvider: session, processParentMapProvider: session,
            timeProvider: session.Clock, gameplayClock: session.Clock);
        await recovery.InitializeAsync(CancellationToken.None);
        var history = await session.Store.ListRecentAsync(10, CancellationToken.None);
        Assert.AreEqual(42L, history.Single().DurationSeconds);
    }

    [TestMethod]
    public async Task OldRecordsKeepTheirDurationAndNewShortSessionsSumWithoutRounding()
    {
        using UserDataTestContext context = new();
        DateTimeOffset start = DateTimeOffset.UnixEpoch;
        SessionSummary old = new(SessionId.Create(), GameProfileId.Create(), "Old",
            start, start.AddSeconds(90), SessionCompletionStatus.Completed, 0, 0, 0, 0);
        await context.Store.AddAsync(old, CancellationToken.None);
        for (int index = 0; index < 3; index++)
        {
            await context.Store.AddAsync(new(SessionId.Create(), old.ProfileId, "Short",
                start, start.AddSeconds(300), SessionCompletionStatus.Completed, 0, 0, 0, 0,
                durationSeconds: 20), CancellationToken.None);
        }
        var records = await context.Store.ListRecentAsync(10, CancellationToken.None);
        Assert.IsNull(records.Single(record => record.SessionId == old.SessionId).DurationSeconds);
        Assert.AreEqual(150d, records.Sum(record => record.Duration.TotalSeconds));
    }
}
