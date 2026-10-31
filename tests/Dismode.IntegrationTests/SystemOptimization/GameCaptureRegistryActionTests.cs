using System.Text.Json;
using Dismode.Contracts.Protocol;
using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Journal;
using Dismode.Core.Recovery;
using Dismode.Core.Transactions;
using Dismode.Data.Journal;
using Dismode.Windows.SystemOptimization;
using Microsoft.Win32;

namespace Dismode.IntegrationTests.SystemOptimization;

[TestClass]
public sealed class GameCaptureRegistryActionTests
{
    private const string UserSid = "S-1-5-21-111-222-333-1001";

    [TestMethod]
    [DataRow(RegistryValueKind.DWord, "1")]
    [DataRow(RegistryValueKind.QWord, "1")]
    [DataRow(RegistryValueKind.String, "\"1\"")]
    [DataRow(RegistryValueKind.ExpandString, "\"%USERPROFILE%\"")]
    [DataRow(RegistryValueKind.Binary, "\"AQID\"")]
    [DataRow(RegistryValueKind.None, "\"AQID\"")]
    [DataRow(RegistryValueKind.MultiString, "[\"one\",\"two\"]")]
    public async Task SnapshotAndJournalReplayPreservePresenceTypeAndValue(
        RegistryValueKind kind, string valueJson)
    {
        GameCaptureRegistryState original = new(UserSid, true, kind, valueJson, true);
        await AssertRoundTripAsync(original);
    }

    [TestMethod]
    public async Task AbsentValueIsDeletedOnRestore()
    {
        await AssertRoundTripAsync(new(UserSid, false, null, null, true));
    }

    [TestMethod]
    [DataRow(ExecutionCheckpoint.BeforeApply)]
    [DataRow(ExecutionCheckpoint.AfterApplyBeforeAppliedJournal)]
    [DataRow(ExecutionCheckpoint.AfterAppliedJournaled)]
    public async Task CrashRecoveryReopensDurableJournalAndRestoresAbsentValue(
        ExecutionCheckpoint checkpoint)
    {
        FakeCaptureAdapter adapter = new(new(UserSid, false, null, null, true));
        GameCaptureRegistryState original = adapter.State;
        ActionExecutionContext context = CreateContext();
        string path = JournalPath();
        using (AppendOnlyRecoveryJournal journal = new(path))
        {
            TransactionCoordinator<GameCaptureRegistryState> transaction = new(journal, new CrashObserver(checkpoint));
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.ExecuteAsync(
                new GameCaptureRegistryAction(context.ActionId, UserSid, adapter), context, CancellationToken.None).AsTask());
        }

        using AppendOnlyRecoveryJournal reopened = new(path);
        ActionRecoveryResult result = await new ActionRecoveryCoordinator<GameCaptureRegistryState>(reopened)
            .RecoverAsync(new GameCaptureRegistryAction(context.ActionId, UserSid, adapter), context, CancellationToken.None);
        Assert.AreEqual(ActionRecoveryStatus.Restored, result.Status);
        Assert.AreEqual(original, adapter.State);
    }

    [TestMethod]
    [DataRow(RegistryValueKind.DWord, "2")]
    [DataRow(RegistryValueKind.QWord, "0")]
    public async Task RecoveryPreservesExternalValueOrTypeChange(RegistryValueKind kind, string valueJson)
    {
        FakeCaptureAdapter adapter = new(new(UserSid, true, RegistryValueKind.DWord, "1", true));
        ActionExecutionContext context = CreateContext();
        GameCaptureRegistryAction action = new(context.ActionId, UserSid, adapter);
        using AppendOnlyRecoveryJournal journal = new(JournalPath());
        _ = await new TransactionCoordinator<GameCaptureRegistryState>(journal)
            .ExecuteAsync(action, context, CancellationToken.None);
        adapter.State = new(UserSid, true, kind, valueJson, true);
        GameCaptureRegistryState external = adapter.State;

        ActionRecoveryResult result = await new ActionRecoveryCoordinator<GameCaptureRegistryState>(journal)
            .RecoverAsync(action, context, CancellationToken.None);
        Assert.AreEqual(ActionRecoveryStatus.ConflictRequiresDecision, result.Status);
        Assert.AreEqual(external, adapter.State);
        Assert.AreEqual(1, adapter.Writes);
    }

    [TestMethod]
    public async Task PolicyDisabledAfterApplyStillRestoresOriginalValue()
    {
        FakeCaptureAdapter adapter = new(new(UserSid, true, RegistryValueKind.DWord, "1", true));
        GameCaptureRegistryState original = adapter.State;
        ActionExecutionContext context = CreateContext();
        GameCaptureRegistryAction action = new(context.ActionId, UserSid, adapter);
        using AppendOnlyRecoveryJournal journal = new(JournalPath());
        ActionExecutionResult applied = await new TransactionCoordinator<GameCaptureRegistryState>(journal)
            .ExecuteAsync(action, context, CancellationToken.None);
        Assert.AreEqual(ActionExecutionStatus.AppliedAndVerified, applied.Status);
        adapter.State = adapter.State with { IsPolicyAllowed = false };

        ActionRecoveryResult result = await new ActionRecoveryCoordinator<GameCaptureRegistryState>(journal)
            .RecoverAsync(action, context, CancellationToken.None);
        Assert.AreEqual(ActionRecoveryStatus.Restored, result.Status);
        Assert.AreEqual(original with { IsPolicyAllowed = false }, adapter.State);
        Assert.AreEqual(2, adapter.Writes);
    }

    [TestMethod]
    public async Task DisabledOrAmbiguousPolicyBlocksWithoutWrites()
    {
        FakeCaptureAdapter adapter = new(new(UserSid, true, RegistryValueKind.DWord, "1", false));
        ActionExecutionContext context = CreateContext();
        using AppendOnlyRecoveryJournal journal = new(JournalPath());
        ActionExecutionResult result = await new TransactionCoordinator<GameCaptureRegistryState>(journal)
            .ExecuteAsync(new GameCaptureRegistryAction(context.ActionId, UserSid, adapter), context, CancellationToken.None);
        Assert.AreEqual(ActionExecutionStatus.Blocked, result.Status);
        Assert.AreEqual(0, adapter.Writes);
    }

    [TestMethod]
    [DataRow(null, null, true)]
    [DataRow(1, RegistryValueKind.DWord, true)]
    [DataRow(0, RegistryValueKind.DWord, false)]
    [DataRow(2, RegistryValueKind.DWord, false)]
    [DataRow("1", RegistryValueKind.String, false)]
    [DataRow(1L, RegistryValueKind.QWord, false)]
    [DataRow(null, RegistryValueKind.Unknown, false)]
    public void PolicyRequiresAbsentValueOrExplicitDwordPermission(
        object? value, RegistryValueKind? kind, bool allowed)
    {
        Assert.AreEqual(allowed, WindowsGameCaptureRegistryAdapter.IsPolicyAllowed(value, kind));
    }

    [TestMethod]
    [DataRow(RegistryValueKind.Unknown)]
    [DataRow(RegistryValueKind.DWord)]
    public void PresentButUnreadableValueCannotBecomeAnAbsentSnapshot(RegistryValueKind kind)
    {
        _ = Assert.Throws<NotSupportedException>(() =>
            WindowsGameCaptureRegistryAdapter.CreateSnapshot(UserSid, kind, null, true));
    }

    [TestMethod]
    public void NativeValueSnapshotsKeepRawTypeAndUnexpandedData()
    {
        Assert.AreEqual(new(UserSid, false, null, null, true),
            WindowsGameCaptureRegistryAdapter.CreateSnapshot(UserSid, null, null, true));
        Assert.AreEqual(new(UserSid, true, RegistryValueKind.ExpandString, "\"%USERPROFILE%\"", true),
            WindowsGameCaptureRegistryAdapter.CreateSnapshot(UserSid, RegistryValueKind.ExpandString, "%USERPROFILE%", true));
        Assert.AreEqual(new(UserSid, true, RegistryValueKind.Binary, "\"AQID\"", true),
            WindowsGameCaptureRegistryAdapter.CreateSnapshot(UserSid, RegistryValueKind.Binary, new byte[] { 1, 2, 3 }, true));
        Assert.AreEqual(new(UserSid, true, RegistryValueKind.QWord, "4294967296", true),
            WindowsGameCaptureRegistryAdapter.CreateSnapshot(UserSid, RegistryValueKind.QWord, 4294967296L, true));
    }

    [TestMethod]
    public async Task PolicyChangeAfterSnapshotPreventsWrite()
    {
        FakeCaptureAdapter adapter = new(new(UserSid, true, RegistryValueKind.DWord, "1", true));
        ActionExecutionContext context = CreateContext();
        GameCaptureRegistryAction action = new(context.ActionId, UserSid, adapter);
        PreparedAction<GameCaptureRegistryState> prepared = await action.PrepareAsync(context, CancellationToken.None);
        adapter.State = adapter.State with { IsPolicyAllowed = false };
        Assert.IsFalse((await action.ValidateAsync(context, prepared, CancellationToken.None)).IsValid);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            action.ApplyAsync(context, prepared, CancellationToken.None).AsTask());
        Assert.AreEqual(0, adapter.Writes);
    }

    [TestMethod]
    public async Task AlreadyDisabledDoesNotWrite()
    {
        FakeCaptureAdapter adapter = new(new(UserSid, true, RegistryValueKind.DWord, "0", true));
        ActionExecutionContext context = CreateContext();
        using AppendOnlyRecoveryJournal journal = new(JournalPath());
        _ = await new TransactionCoordinator<GameCaptureRegistryState>(journal)
            .ExecuteAsync(new GameCaptureRegistryAction(context.ActionId, UserSid, adapter), context, CancellationToken.None);
        Assert.AreEqual(0, adapter.Writes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AccessDeniedNeverReportsSuccessAndFailedWriteCanBeReplayed(bool denyWrite)
    {
        FakeCaptureAdapter adapter = new(new(UserSid, false, null, null, true))
        {
            DenyRead = !denyWrite,
            DenyWrite = denyWrite,
        };
        ActionExecutionContext context = CreateContext();
        using AppendOnlyRecoveryJournal journal = new(JournalPath());
        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new TransactionCoordinator<GameCaptureRegistryState>(journal).ExecuteAsync(
                new GameCaptureRegistryAction(context.ActionId, UserSid, adapter), context, CancellationToken.None).AsTask());
        Assert.AreEqual(0, adapter.Writes);
        IReadOnlyList<RecoveryJournalEntry> records = await journal.ReadAllAsync(CancellationToken.None);
        Assert.IsFalse(records.Any(record => record.EventKind == JournalEventKind.ActionVerified));
        if (denyWrite)
        {
            Assert.IsTrue(records.Any(record => record.EventKind == JournalEventKind.ActionApplyFailed));
            adapter.DenyWrite = false;
            ActionRecoveryResult recovery = await new ActionRecoveryCoordinator<GameCaptureRegistryState>(journal)
                .RecoverAsync(new GameCaptureRegistryAction(context.ActionId, UserSid, adapter), context, CancellationToken.None);
            Assert.AreEqual(ActionRecoveryStatus.Restored, recovery.Status);
        }
    }

    [TestMethod]
    public async Task ChangeBetweenValidationAndWriteIsPreserved()
    {
        FakeCaptureAdapter adapter = new(new(UserSid, true, RegistryValueKind.DWord, "1", true));
        ActionExecutionContext context = CreateContext();
        GameCaptureRegistryAction action = new(context.ActionId, UserSid, adapter);
        PreparedAction<GameCaptureRegistryState> prepared = await action.PrepareAsync(context, CancellationToken.None);
        adapter.State = adapter.State with { ValueJson = "2" };
        Assert.IsFalse((await action.ValidateAsync(context, prepared, CancellationToken.None)).IsValid);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            action.ApplyAsync(context, prepared, CancellationToken.None).AsTask());
        Assert.AreEqual(0, adapter.Writes);
    }

    [TestMethod]
    public async Task CompensationRechecksExternalChanges()
    {
        FakeCaptureAdapter adapter = new(new(UserSid, true, RegistryValueKind.DWord, "1", true));
        ActionExecutionContext context = CreateContext();
        GameCaptureRegistryAction action = new(context.ActionId, UserSid, adapter);
        PreparedAction<GameCaptureRegistryState> prepared = await action.PrepareAsync(context, CancellationToken.None);
        await action.ApplyAsync(context, prepared, CancellationToken.None);
        adapter.State = adapter.State with { ValueJson = "2" };
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            action.CompensateAsync(context, prepared, CancellationToken.None).AsTask());
        Assert.AreEqual(1, adapter.Writes);
        Assert.AreEqual("2", adapter.State.ValueJson);
    }

    [TestMethod]
    [DataRow("S-1-5-18")]
    [DataRow("S-1-5-19")]
    [DataRow("S-1-5-20")]
    [DataRow("S-1-5-21-111-222-333-1001\\Software")]
    public void ServiceOrInvalidSidIsRejected(string sid)
    {
        _ = Assert.Throws<ArgumentException>(() => new GameCaptureRegistryAction(ActionId.Create(), sid));
    }

    private static async Task AssertRoundTripAsync(GameCaptureRegistryState original)
    {
        FakeCaptureAdapter adapter = new(original);
        ActionExecutionContext context = CreateContext();
        using AppendOnlyRecoveryJournal journal = new(JournalPath());
        adapter.BeforeWrite = async () =>
        {
            IReadOnlyList<RecoveryJournalEntry> records = await journal.ReadAllAsync(CancellationToken.None);
            Assert.IsTrue(records.Any(record => record.EventKind == JournalEventKind.ActionApplying));
            RecoveryJournalEntry prepared = records.Single(record => record.EventKind == JournalEventKind.ActionPrepared);
            Assert.AreEqual(original, JsonSerializer.Deserialize<GameCaptureRegistryState>(prepared.OriginalStateJson!));
        };
        GameCaptureRegistryAction action = new(context.ActionId, UserSid, adapter);
        Assert.AreEqual($"{UserSid}\\{WindowsGameCaptureRegistryAdapter.SubKeyPath}", action.Descriptor.TargetId);
        ActionExecutionResult execution = await new TransactionCoordinator<GameCaptureRegistryState>(journal)
            .ExecuteAsync(action, context, CancellationToken.None);
        Assert.AreEqual(ActionExecutionStatus.AppliedAndVerified, execution.Status);
        Assert.AreEqual(new(UserSid, true, RegistryValueKind.DWord, "0", true), adapter.State);
        adapter.BeforeWrite = null;
        ActionRecoveryResult recovery = await new ActionRecoveryCoordinator<GameCaptureRegistryState>(journal)
            .RecoverAsync(new GameCaptureRegistryAction(context.ActionId, UserSid, adapter), context, CancellationToken.None);
        Assert.AreEqual(ActionRecoveryStatus.Restored, recovery.Status);
        Assert.AreEqual(original, adapter.State);
        Assert.AreEqual(!original.IsPresent, adapter.Deleted);
        Assert.AreEqual(ActionRecoveryStatus.AlreadyRestored,
            (await new ActionRecoveryCoordinator<GameCaptureRegistryState>(journal)
                .RecoverAsync(action, context, CancellationToken.None)).Status);
    }

    private static ActionExecutionContext CreateContext() =>
        new(SessionId.Create(), ActionId.Create(), IdempotencyKey.Create(), DateTimeOffset.UtcNow);

    private static string JournalPath() => Path.Combine(Path.GetTempPath(), "Dismode.GameCaptureTests", Guid.NewGuid().ToString("N"), "recovery.journal");

    private sealed class FakeCaptureAdapter(GameCaptureRegistryState state) : IGameCaptureRegistryAdapter
    {
        internal GameCaptureRegistryState State { get; set; } = state;
        internal int Writes { get; private set; }
        internal bool Deleted { get; private set; }
        internal bool DenyRead { get; init; }
        internal bool DenyWrite { get; set; }
        internal Func<Task>? BeforeWrite { get; set; }

        public ValueTask<GameCaptureRegistryState> ReadAsync(string userSid, CancellationToken cancellationToken)
        {
            Assert.AreEqual(UserSid, userSid);
            if (DenyRead)
            {
                throw new UnauthorizedAccessException();
            }

            return ValueTask.FromResult(State);
        }

        public async ValueTask WriteAsync(GameCaptureRegistryState expected, GameCaptureRegistryState desired, CancellationToken cancellationToken)
        {
            Assert.AreEqual(expected, State);
            if (BeforeWrite is not null)
            {
                await BeforeWrite();
            }

            if (DenyWrite)
            {
                throw new UnauthorizedAccessException();
            }

            // Zapis wartosci nie zmienia polityki AllowGameDVR.
            State = desired with { IsPolicyAllowed = State.IsPolicyAllowed };
            Deleted = !desired.IsPresent;
            Writes++;
        }
    }

    private sealed class CrashObserver(ExecutionCheckpoint crashAt) : IExecutionCheckpointObserver
    {
        public ValueTask OnCheckpointAsync(ExecutionCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            if (checkpoint == crashAt)
            {
                throw new InvalidOperationException("Injected crash.");
            }

            return ValueTask.CompletedTask;
        }
    }
}
