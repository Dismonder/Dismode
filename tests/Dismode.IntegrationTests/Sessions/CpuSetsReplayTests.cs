using Dismode.Contracts.Protocol;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Core.Journal;
using Dismode.Core.Transactions;
using Dismode.IntegrationTests.Cpu;
using Dismode.Windows.Processes;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests.Sessions;

[TestClass]
public sealed class CpuSetsReplayTests
{
    [TestMethod]
    public async Task GameApplyPersistsIdentifiersBeforeWritingAndUsesJournalForRestore()
    {
        FakeCpuSetsPort port = new();
        await using SyntheticSession session = new(cpuSetsPort: port);
        bool recorded = false;
        var applied = await session.Orchestrator.ApplyGameCpuSetsAsync(session.Id, session.Root, [1, 2],
            written =>
            {
                Assert.IsNotNull(written.Transaction);
                Assert.AreEqual(0, port.Writes);
                recorded = true;
                return ValueTask.CompletedTask;
            }, CancellationToken.None);
        Assert.IsTrue(recorded);
        Assert.AreEqual(1, applied.AppliedCount);
        Assert.AreEqual(1, port.Writes);
        Assert.IsTrue((await session.Journal.ReadAllAsync(CancellationToken.None))
            .Any(entry => entry.EventKind == JournalEventKind.ActionVerified));
        await session.Orchestrator.RestoreGameCpuSetsAsync(session.Root, applied.Metadata);
        Assert.IsEmpty(port.Current);
    }

    [TestMethod]
    public async Task RefusedGameCheckpointDoesNotMutateTheProcess()
    {
        FakeCpuSetsPort port = new();
        await using SyntheticSession session = new(cpuSetsPort: port);
        await Assert.ThrowsExactlyAsync<IOException>(() => session.Orchestrator.ApplyGameCpuSetsAsync(
            session.Id, session.Root, [1, 2], _ => throw new IOException("Checkpoint failed"),
            CancellationToken.None).AsTask());
        Assert.AreEqual(0, port.Writes);
        Assert.IsEmpty(await session.Journal.ReadAllAsync(CancellationToken.None));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CrashReplayRestoresNewAndSlimBackgroundRecords(bool journaled)
    {
        FakeCpuSetsPort port = new();
        await using SyntheticSession session = new(cpuSetsPort: port);
        ProcessIdentity background = new(new(23456, session.Clock.GetUtcNow().AddSeconds(-2)),
            @"C:\Synthetic\background.exe", new string('B', 64), null, "user", 1, null);
        session.Running.Add(background);
        ProcessCpuSetsTransaction? transaction = null;
        if (journaled)
        {
            transaction = ProcessCpuSetsTransaction.Create(session.Id);
            await new TransactionCoordinator<ProcessCpuSetsState>(session.Journal).ExecuteAsync(
                new ProcessCpuSetsAction(new(transaction.ActionId), background, [1, 2], session, port),
                transaction.Context, CancellationToken.None);
        }
        else
        {
            port.Current = [2, 1];
        }

        await session.StartAsync(restrainedProcesses:
        [
            new
            {
                ActionId = Guid.NewGuid(),
                IdempotencyKey = Guid.NewGuid(),
                DisplayName = "Synthetic",
                Identity = background,
                ActionMode = BackgroundProcessActionMode.LowerPriority,
                EstimatedWorkingSetBytes = 0,
                SteeredCpuSets = !journaled,
                OriginalCpuSetIds = journaled ? null : Array.Empty<uint>(),
                AppliedCpuSetIds = journaled ? null : new uint[] { 1, 2 },
                CpuSetsTransaction = transaction,
            },
        ]);

        Assert.IsEmpty(port.Current);
        Assert.AreEqual(journaled ? 2 : 1, port.Writes);
        Assert.IsNotNull(await session.Orchestrator.GetActiveAsync(CancellationToken.None));
        if (journaled)
        {
            Assert.IsTrue((await session.Journal.ReadAllAsync(CancellationToken.None)).Any(entry =>
                entry.ActionId == transaction!.ActionId && entry.EventKind == JournalEventKind.CompensationVerified));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CrashReplayRetainsGameMetadataAndRestoresNewAndSlimRecords(bool journaled)
    {
        FakeCpuSetsPort port = new();
        await using SyntheticSession session = new(cpuSetsPort: port);
        ProcessCpuSetsTransaction? transaction = null;
        if (journaled)
        {
            transaction = ProcessCpuSetsTransaction.Create(session.Id);
            await new TransactionCoordinator<ProcessCpuSetsState>(session.Journal).ExecuteAsync(
                new ProcessCpuSetsAction(new(transaction.ActionId), session.Root, [1, 2], session, port),
                transaction.Context, CancellationToken.None);
        }
        else
        {
            port.Current = [2, 1];
        }

        await session.StartAsync(gameCpuSets: new
        {
            OriginalIds = Array.Empty<uint>(),
            AppliedIds = new uint[] { 1, 2 },
            Transaction = transaction,
        });
        await session.Orchestrator.RestoreAsync(session.Id, CancellationToken.None);

        Assert.IsEmpty(port.Current);
        Assert.AreEqual(journaled ? 2 : 1, port.Writes);
        Assert.AreEqual(SessionCheckpoint.ReconciliationComplete,
            (await session.Journal.ReadAllAsync(CancellationToken.None))[^1].SessionCheckpoint);
    }

    [TestMethod]
    public async Task FailedGameRestoreKeepsSessionRecoverableUntilQuerySucceeds()
    {
        FakeCpuSetsPort port = new();
        await using SyntheticSession session = new(cpuSetsPort: port);
        ProcessCpuSetsTransaction transaction = ProcessCpuSetsTransaction.Create(session.Id);
        await new TransactionCoordinator<ProcessCpuSetsState>(session.Journal).ExecuteAsync(
            new ProcessCpuSetsAction(new(transaction.ActionId), session.Root, [1, 2], session, port),
            transaction.Context, CancellationToken.None);
        await session.StartAsync(gameCpuSets: new
        {
            OriginalIds = Array.Empty<uint>(),
            AppliedIds = new uint[] { 1, 2 },
            Transaction = transaction,
        });
        port.QueryFails = true;
        await session.Orchestrator.RestoreAsync(session.Id, CancellationToken.None);
        Assert.IsNotNull(await session.Orchestrator.GetActiveAsync(CancellationToken.None));
        Assert.IsFalse((await session.Journal.ReadAllAsync(CancellationToken.None))
            .Any(entry => entry.SessionCheckpoint == SessionCheckpoint.ReconciliationComplete));
        Assert.AreEqual(1, port.Writes);

        port.QueryFails = false;
        await session.Orchestrator.RestoreAsync(session.Id, CancellationToken.None);
        Assert.IsEmpty(port.Current);
        Assert.IsNull(await session.Orchestrator.GetActiveAsync(CancellationToken.None));
    }
}
