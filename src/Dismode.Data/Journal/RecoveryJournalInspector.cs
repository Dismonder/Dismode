using Dismode.Core.Journal;

namespace Dismode.Data.Journal;

public sealed record RecoveryJournalInspection(
    int RecordCount,
    IReadOnlyList<Guid> UnfinishedSessionIds,
    IReadOnlyList<Guid> PendingDurableActionIds)
{
    public bool IsClean => UnfinishedSessionIds.Count == 0;

    /// <summary>
    /// True when the journal records at least one action that was applied but
    /// never compensated, so a real system change is still outstanding.
    /// A journal that only records transient work — process priority or ECO
    /// QoS on processes that have since exited — leaves nothing to restore.
    /// </summary>
    public bool HasPendingDurableChanges =>
        PendingDurableActionIds.Count > 0;
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
            return new(0, [], []);
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

        return new(
            records.Count,
            unfinishedSessionIds,
            FindPendingDurableActions(records));
    }

    private static readonly JournalEventKind[] AppliedKinds =
    [
        JournalEventKind.ActionApplied,
        JournalEventKind.ActionVerified,
    ];

    private static readonly JournalEventKind[] CompensatedKinds =
    [
        JournalEventKind.ActionCompensated,
        JournalEventKind.CompensationVerified,
    ];

    private static IReadOnlyList<Guid> FindPendingDurableActions(
        IReadOnlyList<RecoveryJournalEntry> records)
    {
        HashSet<Guid> applied = [];
        HashSet<Guid> compensated = [];
        foreach (RecoveryJournalEntry record in records)
        {
            if (record.ActionId is not { } actionId)
            {
                continue;
            }

            if (AppliedKinds.Contains(record.EventKind))
            {
                applied.Add(actionId);
            }
            else if (CompensatedKinds.Contains(record.EventKind))
            {
                compensated.Add(actionId);
            }
        }

        applied.ExceptWith(compensated);
        return [.. applied.Order()];
    }
}
