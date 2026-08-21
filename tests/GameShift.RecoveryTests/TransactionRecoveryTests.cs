using GameShift.Core.Recovery;
using GameShift.Core.Transactions;
using GameShift.Data.Journal;
using GameShift.RecoveryTests.Support;

namespace GameShift.RecoveryTests;

[TestClass]
public sealed class TransactionRecoveryTests
{
    private static readonly TestTargetState Running = new("Running");
    private static readonly TestTargetState Stopped = new("Stopped");

    [TestMethod]
    public async Task RepeatedExecutionWithSameIdempotencyKeyAppliesOnlyOnce()
    {
        using RecoveryTestContext testContext = new();
        using AppendOnlyRecoveryJournal journal = new(testContext.JournalPath);
        FakeReversibleAction action = new(testContext.ActionId, Running, Stopped);
        TransactionCoordinator<TestTargetState> coordinator = new(journal);

        ActionExecutionResult first = await coordinator.ExecuteAsync(
            action,
            testContext.ExecutionContext,
            CancellationToken.None);
        ActionExecutionResult replay = await coordinator.ExecuteAsync(
            action,
            testContext.ExecutionContext,
            CancellationToken.None);

        Assert.AreEqual(ActionExecutionStatus.AppliedAndVerified, first.Status);
        Assert.AreEqual(ActionExecutionStatus.AlreadyCompleted, replay.Status);
        Assert.AreEqual(1, action.ApplyCount);
    }

    [TestMethod]
    public async Task CrashBeforeApplyLeavesTargetUntouchedAndRecoverable()
    {
        using RecoveryTestContext testContext = new();
        using AppendOnlyRecoveryJournal journal = new(testContext.JournalPath);
        FakeReversibleAction action = new(testContext.ActionId, Running, Stopped);
        TransactionCoordinator<TestTargetState> coordinator = new(
            journal,
            new ThrowingCheckpointObserver(ExecutionCheckpoint.BeforeApply));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => coordinator
                .ExecuteAsync(
                    action,
                    testContext.ExecutionContext,
                    CancellationToken.None)
                .AsTask());

        Assert.AreEqual(Running, action.CurrentState);
        Assert.AreEqual(0, action.ApplyCount);

        ActionRecoveryCoordinator<TestTargetState> recovery = new(journal);
        ActionRecoveryResult result = await recovery.RecoverAsync(
            action,
            testContext.ExecutionContext,
            CancellationToken.None);

        Assert.AreEqual(ActionRecoveryStatus.Restored, result.Status);
        Assert.AreEqual(0, action.CompensateCount);
    }

    [TestMethod]
    public async Task CrashAfterApplyRestoresOriginalAndRecoveryIsIdempotent()
    {
        using RecoveryTestContext testContext = new();
        FakeReversibleAction action = new(testContext.ActionId, Running, Stopped);

        using (AppendOnlyRecoveryJournal firstProcess = new(testContext.JournalPath))
        {
            TransactionCoordinator<TestTargetState> coordinator = new(
                firstProcess,
                new ThrowingCheckpointObserver(
                    ExecutionCheckpoint.AfterApplyBeforeAppliedJournal));

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => coordinator
                    .ExecuteAsync(
                        action,
                        testContext.ExecutionContext,
                        CancellationToken.None)
                    .AsTask());
        }

        Assert.AreEqual(Stopped, action.CurrentState);
        Assert.AreEqual(1, action.ApplyCount);

        using AppendOnlyRecoveryJournal restartedProcess = new(testContext.JournalPath);
        ActionRecoveryCoordinator<TestTargetState> recovery = new(restartedProcess);
        ActionRecoveryResult firstRecovery = await recovery.RecoverAsync(
            action,
            testContext.ExecutionContext,
            CancellationToken.None);
        ActionRecoveryResult repeatedRecovery = await recovery.RecoverAsync(
            action,
            testContext.ExecutionContext,
            CancellationToken.None);

        Assert.AreEqual(ActionRecoveryStatus.Restored, firstRecovery.Status);
        Assert.AreEqual(ActionRecoveryStatus.AlreadyRestored, repeatedRecovery.Status);
        Assert.AreEqual(Running, action.CurrentState);
        Assert.AreEqual(1, action.CompensateCount);
    }

    [TestMethod]
    public async Task CrashDuringCompensationDoesNotCompensateTwice()
    {
        using RecoveryTestContext testContext = new();
        using AppendOnlyRecoveryJournal journal = new(testContext.JournalPath);
        FakeReversibleAction action = new(testContext.ActionId, Running, Stopped);
        TransactionCoordinator<TestTargetState> coordinator = new(
            journal,
            new ThrowingCheckpointObserver(
                ExecutionCheckpoint.AfterApplyBeforeAppliedJournal));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => coordinator
                .ExecuteAsync(
                    action,
                    testContext.ExecutionContext,
                    CancellationToken.None)
                .AsTask());

        ActionRecoveryCoordinator<TestTargetState> interruptedRecovery = new(
            journal,
            new ThrowingCheckpointObserver(
                ExecutionCheckpoint.AfterCompensationBeforeJournal));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => interruptedRecovery
                .RecoverAsync(
                    action,
                    testContext.ExecutionContext,
                    CancellationToken.None)
                .AsTask());

        ActionRecoveryCoordinator<TestTargetState> resumedRecovery = new(journal);
        ActionRecoveryResult result = await resumedRecovery.RecoverAsync(
            action,
            testContext.ExecutionContext,
            CancellationToken.None);

        Assert.AreEqual(ActionRecoveryStatus.Restored, result.Status);
        Assert.AreEqual(Running, action.CurrentState);
        Assert.AreEqual(1, action.CompensateCount);
    }

    [TestMethod]
    public async Task ExternalChangeIsPreservedAndReportedAsConflict()
    {
        using RecoveryTestContext testContext = new();
        using AppendOnlyRecoveryJournal journal = new(testContext.JournalPath);
        FakeReversibleAction action = new(testContext.ActionId, Running, Stopped);
        TransactionCoordinator<TestTargetState> coordinator = new(
            journal,
            new ThrowingCheckpointObserver(
                ExecutionCheckpoint.AfterApplyBeforeAppliedJournal));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => coordinator
                .ExecuteAsync(
                    action,
                    testContext.ExecutionContext,
                    CancellationToken.None)
                .AsTask());

        TestTargetState externalState = new("ExternallyChanged");
        action.SimulateExternalChange(externalState);
        ActionRecoveryCoordinator<TestTargetState> recovery = new(journal);
        ActionRecoveryResult result = await recovery.RecoverAsync(
            action,
            testContext.ExecutionContext,
            CancellationToken.None);

        Assert.AreEqual(ActionRecoveryStatus.ConflictRequiresDecision, result.Status);
        Assert.AreEqual(externalState, action.CurrentState);
        Assert.AreEqual(0, action.CompensateCount);
    }

    [TestMethod]
    public async Task SessionRecoveryRunsInReverseApplicationOrder()
    {
        List<string> recoveryOrder = [];
        IRecoveryOperation[] applicationOrder =
        [
            new RecordingRecoveryOperation("Service", recoveryOrder),
            new RecordingRecoveryOperation("Process", recoveryOrder),
            new RecordingRecoveryOperation("Power", recoveryOrder),
        ];
        SessionRecoveryResult result =
            await SessionRecoveryCoordinator.RecoverInReverseApplicationOrderAsync(
                applicationOrder,
                CancellationToken.None);
        string[] expectedOrder = ["Power", "Process", "Service"];

        CollectionAssert.AreEqual(expectedOrder, recoveryOrder);
        Assert.HasCount(3, result.Actions);
        Assert.IsFalse(result.RequiresManualIntervention);
    }
}
