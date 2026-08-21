using GameShift.Core.Journal;

namespace GameShift.Data.Journal;

public sealed record RecoveryJournalInspection(
    int RecordCount,
    IReadOnlyList<Guid> UnfinishedSessionIds)
{
    public bool IsClean => UnfinishedSessionIds.Count == 0;
}

public static class RecoveryJournalInspector
{
    public static async ValueTask<RecoveryJournalInspection> InspectAsync(
        string journalPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        string fullPath = Path.GetFullPath(journalPath);
        if (!File.Exists(fullPath))
        {
            return new(0, []);
        }

        using AppendOnlyRecoveryJournal journal = new(fullPath);
        IReadOnlyList<RecoveryJournalEntry> records =
            await journal.ReadAllAsync(cancellationToken)
                .ConfigureAwait(false);
        Guid[] unfinishedSessionIds = records
            .Where(record =>
                record.EventKind
                    == JournalEventKind.SessionCheckpointRecorded)
            .GroupBy(record => record.SessionId)
            .Where(group =>
                group.OrderBy(record => record.Sequence)
                    .Last()
                    .SessionCheckpoint
                    != SessionCheckpoint.ReconciliationComplete)
            .Select(group => group.Key)
            .Order()
            .ToArray();

        return new(records.Count, unfinishedSessionIds);
    }
}
