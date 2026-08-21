using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Journal;
using GameShift.Data.Journal;
using GameShift.RecoveryTests.Support;

namespace GameShift.RecoveryTests;

[TestClass]
public sealed class AppendOnlyRecoveryJournalTests
{
    [TestMethod]
    public async Task AppendBuildsAVerifiableHashChain()
    {
        using RecoveryTestContext testContext = new();
        using AppendOnlyRecoveryJournal journal = new(testContext.JournalPath);

        RecoveryJournalEntry first = await journal.AppendDurableAsync(
            CreateDraft(testContext, JournalEventKind.ActionPrepared, "target-one"),
            CancellationToken.None);
        RecoveryJournalEntry second = await journal.AppendDurableAsync(
            CreateDraft(testContext, JournalEventKind.ActionApplying, "target-one"),
            CancellationToken.None);
        IReadOnlyList<RecoveryJournalEntry> records =
            await journal.ReadAllAsync(CancellationToken.None);

        Assert.HasCount(2, records);
        Assert.AreEqual(1L, first.Sequence);
        Assert.AreEqual(2L, second.Sequence);
        Assert.AreEqual(first.RecordHash, second.PreviousRecordHash);
        Assert.AreNotEqual(AppendOnlyRecoveryJournal.GenesisHash, second.RecordHash);
    }

    [TestMethod]
    public async Task ReadRejectsTamperedRecord()
    {
        using RecoveryTestContext testContext = new();

        using (AppendOnlyRecoveryJournal journal = new(testContext.JournalPath))
        {
            await journal.AppendDurableAsync(
                CreateDraft(testContext, JournalEventKind.ActionPrepared, "target-one"),
                CancellationToken.None);
        }

        string content = await File.ReadAllTextAsync(testContext.JournalPath);
        string tampered = content.Replace(
            "target-one",
            "target-two",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(testContext.JournalPath, tampered);

        using AppendOnlyRecoveryJournal reopened = new(testContext.JournalPath);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => reopened.ReadAllAsync(CancellationToken.None).AsTask());
    }

    [TestMethod]
    public async Task ReadRejectsPartialTrailingRecord()
    {
        using RecoveryTestContext testContext = new();

        using (AppendOnlyRecoveryJournal journal = new(testContext.JournalPath))
        {
            await journal.AppendDurableAsync(
                CreateDraft(testContext, JournalEventKind.ActionPrepared, "target-one"),
                CancellationToken.None);
        }

        await File.AppendAllTextAsync(testContext.JournalPath, "{");

        using AppendOnlyRecoveryJournal reopened = new(testContext.JournalPath);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => reopened.ReadAllAsync(CancellationToken.None).AsTask());
    }

    [TestMethod]
    public async Task SessionCheckpointSharesTheJournalIntegrityChain()
    {
        using RecoveryTestContext testContext = new();
        using AppendOnlyRecoveryJournal journal = new(testContext.JournalPath);
        SessionCheckpointWriter writer = new(journal);
        SessionId sessionId = testContext.ExecutionContext.SessionId;

        RecoveryJournalEntry checkpoint = await writer.RecordAsync(
            sessionId,
            SessionCheckpoint.SnapshotComplete,
            CancellationToken.None);
        RecoveryJournalEntry actionRecord = await journal.AppendDurableAsync(
            CreateDraft(testContext, JournalEventKind.ActionPrepared, "target-one"),
            CancellationToken.None);

        Assert.IsNull(checkpoint.ActionId);
        Assert.IsNull(checkpoint.IdempotencyKey);
        Assert.AreEqual(SessionCheckpoint.SnapshotComplete, checkpoint.SessionCheckpoint);
        Assert.AreEqual(checkpoint.RecordHash, actionRecord.PreviousRecordHash);
    }

    [TestMethod]
    public async Task InspectorBlocksUntilSessionReconciliationCompletes()
    {
        using RecoveryTestContext testContext = new();
        using AppendOnlyRecoveryJournal journal = new(testContext.JournalPath);
        SessionCheckpointWriter writer = new(journal);
        SessionId sessionId = testContext.ExecutionContext.SessionId;

        await writer.RecordAsync(
            sessionId,
            SessionCheckpoint.SnapshotComplete,
            CancellationToken.None);
        RecoveryJournalInspection incomplete =
            await RecoveryJournalInspector.InspectAsync(
                testContext.JournalPath,
                CancellationToken.None);

        Assert.IsFalse(incomplete.IsClean);
        CollectionAssert.Contains(
            incomplete.UnfinishedSessionIds.ToArray(),
            sessionId.Value);

        await writer.RecordAsync(
            sessionId,
            SessionCheckpoint.ReconciliationComplete,
            CancellationToken.None);
        RecoveryJournalInspection complete =
            await RecoveryJournalInspector.InspectAsync(
                testContext.JournalPath,
                CancellationToken.None);

        Assert.IsTrue(complete.IsClean);
        Assert.IsEmpty(complete.UnfinishedSessionIds);
    }

    [TestMethod]
    public async Task WriterKeepsExclusiveAppendOwnershipUntilDisposed()
    {
        using RecoveryTestContext testContext = new();
        using (AppendOnlyRecoveryJournal journal =
               new(testContext.JournalPath))
        {
            await journal.AppendDurableAsync(
                CreateDraft(
                    testContext,
                    JournalEventKind.ActionPrepared,
                    "target-one"),
                CancellationToken.None);

            Assert.ThrowsExactly<IOException>(
                () =>
                {
                    using FileStream _ = new(
                        testContext.JournalPath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite);
                });

            await journal.AppendDurableAsync(
                CreateDraft(
                    testContext,
                    JournalEventKind.ActionApplying,
                    "target-one"),
                CancellationToken.None);
        }

        using FileStream reopenedWriter = new(
            testContext.JournalPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read);
        Assert.IsTrue(reopenedWriter.CanWrite);
    }

    [TestMethod]
    public async Task TenThousandDurableAppendsKeepOneVerifiableChain()
    {
        const int recordCount = 10_000;
        using RecoveryTestContext testContext = new();
        using AppendOnlyRecoveryJournal journal =
            new(testContext.JournalPath);

        for (int index = 0; index < recordCount; index++)
        {
            await journal.AppendDurableAsync(
                CreateDraft(
                    testContext,
                    JournalEventKind.ActionApplying,
                    $"target-{index}"),
                CancellationToken.None);
        }

        IReadOnlyList<RecoveryJournalEntry> records =
            await journal.ReadAllAsync(CancellationToken.None);

        Assert.HasCount(recordCount, records);
        Assert.AreEqual(recordCount, records[^1].Sequence);
        Assert.AreEqual(
            records[^2].RecordHash,
            records[^1].PreviousRecordHash);
    }

    private static RecoveryJournalDraft CreateDraft(
        RecoveryTestContext testContext,
        JournalEventKind eventKind,
        string targetId) =>
        new(
            testContext.ExecutionContext.SessionId.Value,
            testContext.ExecutionContext.ActionId.Value,
            testContext.ExecutionContext.IdempotencyKey.Value,
            eventKind,
            "WindowsService",
            targetId,
            OriginalStateJson: "{\"value\":\"Running\"}",
            DesiredStateJson: "{\"value\":\"Stopped\"}",
            CurrentStateJson: null,
            SessionCheckpoint: null,
            Details: null,
            TimestampUtc: DateTimeOffset.UtcNow);
}
