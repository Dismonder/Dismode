using System.Diagnostics;
using Dismode.Contracts.Protocol;
using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Core.Journal;
using Dismode.Core.Recovery;
using Dismode.Core.Transactions;
using Dismode.Data.Journal;
using Dismode.Windows.Processes;

namespace Dismode.IntegrationTests.Cpu;

[TestClass]
public sealed class ProcessCpuSetsActionTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly IdentityProvider _identities = new();
    private readonly FakeCpuSetsPort _port = new();
    private readonly ActionExecutionContext _context = new(SessionId.Create(), ActionId.Create(),
        IdempotencyKey.Create(), DateTimeOffset.UtcNow);

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private AppendOnlyRecoveryJournal OpenJournal()
    {
        Directory.CreateDirectory(_directory);
        return new(Path.Combine(_directory, "recovery.jsonl"));
    }

    private ProcessCpuSetsAction Action() => new(_context.ActionId, _identities.Identity, [1, 2], _identities, _port);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExternalChangeBetweenPrepareAndValidateIsBlockedIncludingNoOp(bool noOp)
    {
        using AppendOnlyRecoveryJournal journal = OpenJournal();
        _port.Current = noOp ? [1, 2] : [];
        _port.BeforeQuery = count => { if (count == 2) { _port.Current = [9]; } };
        ActionExecutionResult result = await new TransactionCoordinator<ProcessCpuSetsState>(journal)
            .ExecuteAsync(Action(), _context, CancellationToken.None);
        Assert.AreEqual(ActionExecutionStatus.Blocked, result.Status);
        Assert.AreEqual(0, _port.Writes);
    }

    [TestMethod]
    public async Task RefusedWriteIsJournaledAndRecoveryDoesNotWriteAgain()
    {
        using AppendOnlyRecoveryJournal journal = OpenJournal();
        _port.ApplyFails = true;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new TransactionCoordinator<ProcessCpuSetsState>(journal)
                .ExecuteAsync(Action(), _context, CancellationToken.None).AsTask());
        Assert.IsTrue((await journal.ReadAllAsync(CancellationToken.None))
            .Any(entry => entry.EventKind == JournalEventKind.ActionApplyFailed));
        await new ActionRecoveryCoordinator<ProcessCpuSetsState>(journal)
            .RecoverAsync(Action(), _context, CancellationToken.None);
        Assert.AreEqual(1, _port.Writes);
    }

    [TestMethod]
    public async Task OriginalAlreadyRestoredRequiresNoSetter()
    {
        using AppendOnlyRecoveryJournal journal = OpenJournal();
        await new TransactionCoordinator<ProcessCpuSetsState>(journal)
            .ExecuteAsync(Action(), _context, CancellationToken.None);
        _port.Current = [];
        ActionRecoveryResult result = await new ActionRecoveryCoordinator<ProcessCpuSetsState>(journal)
            .RecoverAsync(Action(), _context, CancellationToken.None);
        Assert.AreEqual(ActionRecoveryStatus.Restored, result.Status);
        Assert.AreEqual(1, _port.Writes);
    }

    [TestMethod]
    public async Task LegacyRestoreComparesSetsAndPreservesNonEmptyOriginal()
    {
        using AppendOnlyRecoveryJournal journal = OpenJournal();
        _port.Current = [2, 1];
        ActionRecoveryResult result = await ProcessCpuSetsAction.RestoreLegacyAsync(
            _identities.Identity, [5], [1, 2], _identities, _port, CancellationToken.None);
        Assert.AreEqual(ActionRecoveryStatus.Restored, result.Status);
        CollectionAssert.AreEqual(new uint[] { 5 }, _port.Current);
    }

    [TestMethod]
    public async Task LegacyRestoreRefusesUnreadableState()
    {
        using AppendOnlyRecoveryJournal journal = OpenJournal();
        _port.QueryFails = true;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ProcessCpuSetsAction.RestoreLegacyAsync(
            _identities.Identity, [], [1, 2], _identities, _port, CancellationToken.None).AsTask());
        Assert.AreEqual(0, _port.Writes);
    }

    [TestMethod]
    public async Task EmptyAssignmentIsJournaledVerifiedAndRestoredAsASet()
    {
        using AppendOnlyRecoveryJournal journal = OpenJournal();
        ActionExecutionResult result = await new TransactionCoordinator<ProcessCpuSetsState>(journal)
            .ExecuteAsync(Action(), _context, CancellationToken.None);
        Assert.AreEqual(ActionExecutionStatus.AppliedAndVerified, result.Status);
        Assert.IsTrue((await journal.ReadAllAsync(CancellationToken.None))
            .Any(entry => entry.EventKind == JournalEventKind.ActionVerified));
        _port.Current = [2, 1, 1];
        ActionRecoveryResult restored = await new ActionRecoveryCoordinator<ProcessCpuSetsState>(journal)
            .RecoverAsync(Action(), _context, CancellationToken.None);
        Assert.AreEqual(ActionRecoveryStatus.Restored, restored.Status);
        Assert.IsEmpty(_port.Current);
        Assert.AreEqual(2, _port.Writes);
    }

    [TestMethod]
    public async Task ForeignAssignmentIsBlockedWithoutWriting()
    {
        using AppendOnlyRecoveryJournal journal = OpenJournal();
        _port.Current = [9];
        ActionExecutionResult result = await new TransactionCoordinator<ProcessCpuSetsState>(journal)
            .ExecuteAsync(Action(), _context, CancellationToken.None);
        Assert.AreEqual(ActionExecutionStatus.Blocked, result.Status);
        Assert.AreEqual(0, _port.Writes);
    }

    [TestMethod]
    public async Task EqualAssignmentIsUnchangedWithoutJournalOrRestore()
    {
        using AppendOnlyRecoveryJournal journal = OpenJournal();
        _port.Current = [2, 1];
        ActionExecutionResult result = await new TransactionCoordinator<ProcessCpuSetsState>(journal)
            .ExecuteAsync(Action(), _context, CancellationToken.None);
        Assert.AreEqual(ActionExecutionStatus.Unchanged, result.Status);
        Assert.AreEqual(0, _port.Writes);
        Assert.IsEmpty(await journal.ReadAllAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task QueryFailureIsBlockedWithoutWriting()
    {
        using AppendOnlyRecoveryJournal journal = OpenJournal();
        _port.QueryFails = true;
        ActionExecutionResult result = await new TransactionCoordinator<ProcessCpuSetsState>(journal)
            .ExecuteAsync(Action(), _context, CancellationToken.None);
        Assert.AreEqual(ActionExecutionStatus.Blocked, result.Status);
        Assert.AreEqual(0, _port.Writes);
    }

    [TestMethod]
    public async Task MismatchedReadBackPreservesForeignStateDuringRecovery()
    {
        using AppendOnlyRecoveryJournal journal = OpenJournal();
        _port.ReadBackAfterApply = [9];
        ActionExecutionResult result = await new TransactionCoordinator<ProcessCpuSetsState>(journal)
            .ExecuteAsync(Action(), _context, CancellationToken.None);
        Assert.AreEqual(ActionExecutionStatus.VerificationFailed, result.Status);
        ActionRecoveryResult recovery = await new ActionRecoveryCoordinator<ProcessCpuSetsState>(journal)
            .RecoverAsync(Action(), _context, CancellationToken.None);
        Assert.AreEqual(ActionRecoveryStatus.ConflictRequiresDecision, recovery.Status);
        Assert.AreEqual(1, _port.Writes);
        CollectionAssert.AreEqual(new uint[] { 9 }, _port.Current);
    }

    [TestMethod]
    public async Task FailedReadBackDoesNotBlindlyClearAndCanBeRetried()
    {
        using AppendOnlyRecoveryJournal journal = OpenJournal();
        _port.QueryFailsAfterApply = true;
        ActionExecutionResult result = await new TransactionCoordinator<ProcessCpuSetsState>(journal)
            .ExecuteAsync(Action(), _context, CancellationToken.None);
        Assert.AreEqual(ActionExecutionStatus.VerificationFailed, result.Status);
        Assert.AreEqual(1, _port.Writes);
        _port.QueryFailsAfterApply = false;
        ActionRecoveryResult recovery = await new ActionRecoveryCoordinator<ProcessCpuSetsState>(journal)
            .RecoverAsync(Action(), _context, CancellationToken.None);
        Assert.AreEqual(ActionRecoveryStatus.Restored, recovery.Status);
        Assert.IsEmpty(_port.Current);
    }

    [TestMethod]
    public async Task ReusedPidIsNeverWrittenDuringRestore()
    {
        using AppendOnlyRecoveryJournal journal = OpenJournal();
        await new TransactionCoordinator<ProcessCpuSetsState>(journal)
            .ExecuteAsync(Action(), _context, CancellationToken.None);
        _identities.Matches = false;
        ActionRecoveryResult recovery = await new ActionRecoveryCoordinator<ProcessCpuSetsState>(journal)
            .RecoverAsync(Action(), _context, CancellationToken.None);
        Assert.AreEqual(ActionRecoveryStatus.Restored, recovery.Status);
        Assert.AreEqual(1, _port.Writes);
    }

    [TestMethod]
    public async Task CrashAfterSetterReopensJournalAndRestoresOnlyOurAssignment()
    {
        using (AppendOnlyRecoveryJournal journal = OpenJournal())
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                new TransactionCoordinator<ProcessCpuSetsState>(journal, new CrashAfterApply())
                    .ExecuteAsync(Action(), _context, CancellationToken.None).AsTask());
        }

        using AppendOnlyRecoveryJournal restarted = OpenJournal();
        ActionRecoveryResult recovered = await new ActionRecoveryCoordinator<ProcessCpuSetsState>(restarted)
            .RecoverAsync(Action(), _context, CancellationToken.None);
        Assert.AreEqual(ActionRecoveryStatus.Restored, recovered.Status);
        Assert.IsEmpty(_port.Current);
        Assert.AreEqual(2, _port.Writes);
        Assert.AreEqual(ActionRecoveryStatus.AlreadyRestored,
            (await new ActionRecoveryCoordinator<ProcessCpuSetsState>(restarted)
                .RecoverAsync(Action(), _context, CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task ExternalChangeBeforeRestoreIsPreserved()
    {
        using AppendOnlyRecoveryJournal journal = OpenJournal();
        await new TransactionCoordinator<ProcessCpuSetsState>(journal)
            .ExecuteAsync(Action(), _context, CancellationToken.None);
        _port.Current = [8];
        ActionRecoveryResult recovery = await new ActionRecoveryCoordinator<ProcessCpuSetsState>(journal)
            .RecoverAsync(Action(), _context, CancellationToken.None);
        Assert.AreEqual(ActionRecoveryStatus.ConflictRequiresDecision, recovery.Status);
        Assert.AreEqual(1, _port.Writes);
    }

    private sealed class CrashAfterApply : IExecutionCheckpointObserver
    {
        public ValueTask OnCheckpointAsync(ExecutionCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            if (checkpoint == ExecutionCheckpoint.AfterApplyBeforeAppliedJournal)
            {
                throw new InvalidOperationException("Synthetic crash");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class IdentityProvider : IProcessIdentityProvider
    {
        internal ProcessIdentity Identity { get; } = new(new(12345, DateTimeOffset.UtcNow),
            @"C:\Synthetic\target.exe", new string('A', 64), null, "user", 1, null);
        internal bool Matches { get; set; } = true;
        public ValueTask<ProcessIdentity?> TryCaptureAsync(int processId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ProcessIdentity?>(Matches ? Identity : null);
        public ValueTask<ProcessIdentity?> TryCaptureAsync(Process process, CancellationToken cancellationToken) =>
            TryCaptureAsync(process.Id, cancellationToken);
        public ValueTask<bool> MatchesRuntimeIdentityAsync(ProcessIdentity expectedIdentity, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Matches);
    }
}
