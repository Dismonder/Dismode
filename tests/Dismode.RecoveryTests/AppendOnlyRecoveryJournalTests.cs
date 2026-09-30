using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Journal;
using Dismode.Data.Journal;
using Dismode.RecoveryTests.Support;

namespace Dismode.RecoveryTests;

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

    /// <summary>
    /// A record with no terminating newline never finished being written, so
    /// it was never returned to any caller and no later record can reference
    /// it. Dropping it loses nothing.
    /// <para>
    /// This used to throw, which sounds prudent and was not: losing power
    /// mid-append is the exact event this journal exists for, and there is no
    /// repair path anywhere in the product. Refusing to read left the machine
    /// with applied changes, a journal nobody could open, and one way out —
    /// deleting the file by hand, which discards the record of those changes
    /// and makes them unrecoverable.
    /// </para>
    /// <para>
    /// The distinction that matters is kept: a tampered <em>complete</em>
    /// record still breaks the hash chain and is still rejected, as the test
    /// above shows. Only an unterminated tail is dropped.
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task PartialTrailingRecordIsDroppedAndTheChainContinues()
    {
        using RecoveryTestContext testContext = new();

        using (AppendOnlyRecoveryJournal journal = new(testContext.JournalPath))
        {
            await journal.AppendDurableAsync(
                CreateDraft(testContext, JournalEventKind.ActionPrepared, "target-one"),
                CancellationToken.None);
        }

        // Zapis przerwany w polowie: rekord bez konczacego znaku nowej linii.
        await File.AppendAllTextAsync(
            testContext.JournalPath,
            "{\"sequence\":2,\"eventKind\"");

        using AppendOnlyRecoveryJournal reopened = new(testContext.JournalPath);
        IReadOnlyList<RecoveryJournalEntry> entries =
            await reopened.ReadAllAsync(CancellationToken.None);

        Assert.AreEqual(
            1,
            entries.Count,
            "Kompletny rekord sprzed awarii musi byc nadal czytelny.");
        Assert.AreEqual(1, entries[0].Sequence);

        // Dopisanie po awarii ma kontynuowac lancuch, a nie doklejac sie do
        // polowy wiersza.
        RecoveryJournalEntry appended = await reopened.AppendDurableAsync(
            CreateDraft(testContext, JournalEventKind.ActionApplied, "target-two"),
            CancellationToken.None);
        Assert.AreEqual(2, appended.Sequence);

        IReadOnlyList<RecoveryJournalEntry> after =
            await reopened.ReadAllAsync(CancellationToken.None);
        Assert.AreEqual(2, after.Count);
        Assert.AreEqual(
            entries[0].RecordHash,
            after[1].PreviousRecordHash,
            "Nowy rekord ma wskazywac na ostatni kompletny, nie na urwany.");
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
