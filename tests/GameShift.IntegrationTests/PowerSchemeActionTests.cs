using GameShift.Contracts.Protocol;
using GameShift.Core.Actions;
using GameShift.Core.Devices;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Policies;
using GameShift.Core.Recovery;
using GameShift.Core.Transactions;
using GameShift.Data.Journal;
using GameShift.Windows.Power;

namespace GameShift.IntegrationTests;

[TestClass]
public sealed class PowerSchemeActionTests
{
    [TestMethod]
    public async Task ManagedCloneUsesJournalAndIsRemovedAfterRecovery()
    {
        Guid originalSchemeId = Guid.NewGuid();
        Guid managedSchemeId = Guid.NewGuid();
        FakePowerSchemeAdapter adapter = new(originalSchemeId);
        ActionId actionId = ActionId.Create();
        ActivateManagedPowerProfileAction action = CreateAction(
            actionId,
            managedSchemeId,
            adapter);
        ActionExecutionContext context = CreateContext(actionId);
        using AppendOnlyRecoveryJournal journal =
            new(CreateJournalPath());

        ActionExecutionResult execution =
            await new TransactionCoordinator<PowerSchemeActionState>(journal)
                .ExecuteAsync(action, context, CancellationToken.None);

        Assert.AreEqual(
            ActionExecutionStatus.AppliedAndVerified,
            execution.Status);
        Assert.AreEqual(managedSchemeId, adapter.ActiveSchemeId);
        Assert.IsTrue(adapter.Contains(managedSchemeId));
        Assert.AreEqual(1, adapter.DuplicateCount);

        ActionRecoveryCoordinator<PowerSchemeActionState> recovery =
            new(journal);
        ActionRecoveryResult restored = await recovery.RecoverAsync(
            action,
            context,
            CancellationToken.None);
        ActionRecoveryResult repeated = await recovery.RecoverAsync(
            action,
            context,
            CancellationToken.None);

        Assert.AreEqual(ActionRecoveryStatus.Restored, restored.Status);
        Assert.AreEqual(
            ActionRecoveryStatus.AlreadyRestored,
            repeated.Status);
        Assert.AreEqual(originalSchemeId, adapter.ActiveSchemeId);
        Assert.IsFalse(adapter.Contains(managedSchemeId));
        Assert.AreEqual(1, adapter.DeleteCount);
    }

    [TestMethod]
    public async Task CrashAfterDuplicateBeforeActivationRemovesPartialClone()
    {
        Guid originalSchemeId = Guid.NewGuid();
        Guid managedSchemeId = Guid.NewGuid();
        FakePowerSchemeAdapter adapter = new(originalSchemeId)
        {
            FailNextActivation = true,
        };
        ActionId actionId = ActionId.Create();
        ActivateManagedPowerProfileAction action = CreateAction(
            actionId,
            managedSchemeId,
            adapter);
        ActionExecutionContext context = CreateContext(actionId);
        using AppendOnlyRecoveryJournal journal =
            new(CreateJournalPath());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => new TransactionCoordinator<PowerSchemeActionState>(journal)
                .ExecuteAsync(
                    action,
                    context,
                    CancellationToken.None)
                .AsTask());

        Assert.AreEqual(originalSchemeId, adapter.ActiveSchemeId);
        Assert.IsTrue(adapter.Contains(managedSchemeId));

        ActionRecoveryResult result =
            await new ActionRecoveryCoordinator<PowerSchemeActionState>(
                    journal)
                .RecoverAsync(
                    action,
                    context,
                    CancellationToken.None);

        Assert.AreEqual(ActionRecoveryStatus.Restored, result.Status);
        Assert.AreEqual(originalSchemeId, adapter.ActiveSchemeId);
        Assert.IsFalse(adapter.Contains(managedSchemeId));
        Assert.AreEqual(1, adapter.DeleteCount);
    }

    [TestMethod]
    public async Task ExternalActiveSchemeChoiceIsPreservedAsConflict()
    {
        Guid originalSchemeId = Guid.NewGuid();
        Guid managedSchemeId = Guid.NewGuid();
        Guid externalSchemeId = Guid.NewGuid();
        FakePowerSchemeAdapter adapter = new(originalSchemeId);
        ActionId actionId = ActionId.Create();
        ActivateManagedPowerProfileAction action = CreateAction(
            actionId,
            managedSchemeId,
            adapter);
        ActionExecutionContext context = CreateContext(actionId);
        using AppendOnlyRecoveryJournal journal =
            new(CreateJournalPath());
        await new TransactionCoordinator<PowerSchemeActionState>(journal)
            .ExecuteAsync(action, context, CancellationToken.None);
        adapter.SimulateExternalActivation(externalSchemeId);

        ActionRecoveryResult result =
            await new ActionRecoveryCoordinator<PowerSchemeActionState>(
                    journal)
                .RecoverAsync(
                    action,
                    context,
                    CancellationToken.None);

        Assert.AreEqual(
            ActionRecoveryStatus.ConflictRequiresDecision,
            result.Status);
        Assert.AreEqual(externalSchemeId, adapter.ActiveSchemeId);
        Assert.IsTrue(adapter.Contains(managedSchemeId));
        Assert.AreEqual(0, adapter.DeleteCount);
    }

    [TestMethod]
    public async Task ExistingOrUnownedManagedSchemeIsBlocked()
    {
        foreach (bool managedSchemeAlreadyExists in new[] { false, true })
        {
            Guid originalSchemeId = Guid.NewGuid();
            Guid managedSchemeId = Guid.NewGuid();
            FakePowerSchemeAdapter adapter = new(originalSchemeId);
            if (managedSchemeAlreadyExists)
            {
                adapter.AddScheme(managedSchemeId);
            }

            ActionId actionId = ActionId.Create();
            ActivateManagedPowerProfileAction action = new(
                actionId,
                managedSchemeId,
                RecoveryAssurance.CompensationVerified,
                isOwnedByGameShift: managedSchemeAlreadyExists,
                adapter);
            ActionExecutionContext context = CreateContext(actionId);
            using AppendOnlyRecoveryJournal journal =
                new(CreateJournalPath());

            ActionExecutionResult result =
                await new TransactionCoordinator<PowerSchemeActionState>(
                        journal)
                    .ExecuteAsync(
                        action,
                        context,
                        CancellationToken.None);

            Assert.AreEqual(ActionExecutionStatus.Blocked, result.Status);
            Assert.AreEqual(0, adapter.DuplicateCount);
            Assert.AreEqual(originalSchemeId, adapter.ActiveSchemeId);
        }
    }

    [TestMethod]
    public async Task TransactionCrashCheckpointsLeaveNoManagedSchemeAfterRecovery()
    {
        ExecutionCheckpoint[] checkpoints =
        [
            ExecutionCheckpoint.AfterPreparationJournaled,
            ExecutionCheckpoint.BeforeApply,
            ExecutionCheckpoint.AfterApplyBeforeAppliedJournal,
            ExecutionCheckpoint.AfterAppliedJournaled,
            ExecutionCheckpoint.AfterVerificationBeforeJournal,
        ];

        foreach (ExecutionCheckpoint checkpoint in checkpoints)
        {
            Guid originalSchemeId = Guid.NewGuid();
            Guid managedSchemeId = Guid.NewGuid();
            FakePowerSchemeAdapter adapter = new(originalSchemeId);
            ActionId actionId = ActionId.Create();
            ActivateManagedPowerProfileAction action = CreateAction(
                actionId,
                managedSchemeId,
                adapter);
            ActionExecutionContext context = CreateContext(actionId);
            using AppendOnlyRecoveryJournal journal =
                new(CreateJournalPath());
            TransactionCoordinator<PowerSchemeActionState> transaction =
                new(journal, new ThrowingCheckpointObserver(checkpoint));

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => transaction
                    .ExecuteAsync(
                        action,
                        context,
                        CancellationToken.None)
                    .AsTask());

            ActionRecoveryResult result =
                await new ActionRecoveryCoordinator<PowerSchemeActionState>(
                        journal)
                    .RecoverAsync(
                        action,
                        context,
                        CancellationToken.None);

            Assert.IsTrue(
                result.Status is
                    ActionRecoveryStatus.NotRequired
                    or ActionRecoveryStatus.Restored,
                $"Unexpected recovery status at {checkpoint}: {result.Status}.");
            Assert.AreEqual(
                originalSchemeId,
                adapter.ActiveSchemeId,
                $"Active scheme mismatch after {checkpoint}.");
            Assert.IsFalse(
                adapter.Contains(managedSchemeId),
                $"Managed scheme remained after {checkpoint}.");
        }
    }

    [TestMethod]
    public async Task RecoveryCrashCheckpointsRemainIdempotent()
    {
        ExecutionCheckpoint[] checkpoints =
        [
            ExecutionCheckpoint.BeforeCompensation,
            ExecutionCheckpoint.AfterCompensationBeforeJournal,
            ExecutionCheckpoint.AfterCompensationJournaled,
        ];

        foreach (ExecutionCheckpoint checkpoint in checkpoints)
        {
            Guid originalSchemeId = Guid.NewGuid();
            Guid managedSchemeId = Guid.NewGuid();
            FakePowerSchemeAdapter adapter = new(originalSchemeId);
            ActionId actionId = ActionId.Create();
            ActivateManagedPowerProfileAction action = CreateAction(
                actionId,
                managedSchemeId,
                adapter);
            ActionExecutionContext context = CreateContext(actionId);
            using AppendOnlyRecoveryJournal journal =
                new(CreateJournalPath());
            await new TransactionCoordinator<PowerSchemeActionState>(journal)
                .ExecuteAsync(action, context, CancellationToken.None);
            ActionRecoveryCoordinator<PowerSchemeActionState>
                interruptedRecovery = new(
                    journal,
                    new ThrowingCheckpointObserver(checkpoint));

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => interruptedRecovery
                    .RecoverAsync(
                        action,
                        context,
                        CancellationToken.None)
                    .AsTask());

            ActionRecoveryResult resumed =
                await new ActionRecoveryCoordinator<PowerSchemeActionState>(
                        journal)
                    .RecoverAsync(
                        action,
                        context,
                        CancellationToken.None);
            ActionRecoveryResult repeated =
                await new ActionRecoveryCoordinator<PowerSchemeActionState>(
                        journal)
                    .RecoverAsync(
                        action,
                        context,
                        CancellationToken.None);

            Assert.AreEqual(
                ActionRecoveryStatus.Restored,
                resumed.Status,
                $"Resume failed after {checkpoint}.");
            Assert.AreEqual(
                ActionRecoveryStatus.AlreadyRestored,
                repeated.Status,
                $"Recovery was not idempotent after {checkpoint}.");
            Assert.AreEqual(originalSchemeId, adapter.ActiveSchemeId);
            Assert.IsFalse(adapter.Contains(managedSchemeId));
            Assert.AreEqual(1, adapter.DeleteCount);
        }
    }

    private static ActivateManagedPowerProfileAction CreateAction(
        ActionId actionId,
        Guid managedSchemeId,
        FakePowerSchemeAdapter adapter) =>
        new(
            actionId,
            managedSchemeId,
            RecoveryAssurance.CompensationVerified,
            isOwnedByGameShift: true,
            adapter,
            new FakeFormFactorDetector(DeviceFormFactor.Laptop));

    private sealed class FakeFormFactorDetector : IDeviceFormFactorDetector
    {
        private readonly DeviceFormFactor _formFactor;

        public FakeFormFactorDetector(DeviceFormFactor formFactor) =>
            _formFactor = formFactor;

        public DeviceFormFactor Detect() => _formFactor;
    }

    private static ActionExecutionContext CreateContext(ActionId actionId) =>
        new(
            SessionId.Create(),
            actionId,
            IdempotencyKey.Create(),
            DateTimeOffset.UtcNow);

    private static string CreateJournalPath() =>
        Path.Combine(
            Path.GetTempPath(),
            "GameShift.PowerSchemeActionTests",
            Guid.NewGuid().ToString("N"),
            "recovery.jsonl");

    private sealed class FakePowerSchemeAdapter : IPowerSchemeAdapter
    {
        private readonly HashSet<Guid> _schemes;

        internal FakePowerSchemeAdapter(Guid activeSchemeId)
        {
            ActiveSchemeId = activeSchemeId;
            _schemes = [activeSchemeId];
        }

        internal Guid ActiveSchemeId { get; private set; }

        internal bool FailNextActivation { get; set; }

        internal int DuplicateCount { get; private set; }

        internal int DeleteCount { get; private set; }

        public ValueTask<Guid> GetActiveSchemeAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ActiveSchemeId);
        }

        public ValueTask<bool> SchemeExistsAsync(
            Guid schemeId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_schemes.Contains(schemeId));
        }

        public ValueTask DuplicateSchemeAsync(
            Guid sourceSchemeId,
            Guid destinationSchemeId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_schemes.Contains(sourceSchemeId))
            {
                throw new InvalidOperationException(
                    "The source scheme does not exist.");
            }

            if (!_schemes.Add(destinationSchemeId))
            {
                throw new InvalidOperationException(
                    "The destination scheme already exists.");
            }

            DuplicateCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetActiveSchemeAsync(
            Guid schemeId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNextActivation)
            {
                FailNextActivation = false;
                throw new InvalidOperationException(
                    "Injected activation failure.");
            }

            if (!_schemes.Contains(schemeId))
            {
                throw new InvalidOperationException(
                    "The requested scheme does not exist.");
            }

            ActiveSchemeId = schemeId;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteSchemeAsync(
            Guid schemeId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ActiveSchemeId == schemeId)
            {
                throw new InvalidOperationException(
                    "The active scheme cannot be deleted.");
            }

            if (!_schemes.Remove(schemeId))
            {
                throw new InvalidOperationException(
                    "The requested scheme does not exist.");
            }

            DeleteCount++;
            return ValueTask.CompletedTask;
        }

        internal bool Contains(Guid schemeId) => _schemes.Contains(schemeId);

        internal void AddScheme(Guid schemeId) => _schemes.Add(schemeId);

        internal void SimulateExternalActivation(Guid schemeId)
        {
            _schemes.Add(schemeId);
            ActiveSchemeId = schemeId;
        }
    }

    private sealed class ThrowingCheckpointObserver(
        ExecutionCheckpoint crashAt) : IExecutionCheckpointObserver
    {
        private bool _hasThrown;

        public ValueTask OnCheckpointAsync(
            ExecutionCheckpoint checkpoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_hasThrown && checkpoint == crashAt)
            {
                _hasThrown = true;
                throw new InvalidOperationException(
                    $"Injected crash at {checkpoint}.");
            }

            return ValueTask.CompletedTask;
        }
    }
}
