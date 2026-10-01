namespace Dismode.Core.Journal;

public interface IRecoveryJournal
{
    ValueTask<RecoveryJournalEntry> AppendDurableAsync(
        RecoveryJournalDraft draft,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<RecoveryJournalEntry>> ReadAllAsync(
        CancellationToken cancellationToken);
}

