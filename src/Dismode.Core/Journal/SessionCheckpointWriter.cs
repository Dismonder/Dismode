using Dismode.Core.Domain.Identifiers;

namespace Dismode.Core.Journal;

public sealed class SessionCheckpointWriter
{
    private readonly IRecoveryJournal _journal;

    public SessionCheckpointWriter(IRecoveryJournal journal)
    {
        _journal = journal;
    }

    public ValueTask<RecoveryJournalEntry> RecordAsync(
        SessionId sessionId,
        SessionCheckpoint checkpoint,
        CancellationToken cancellationToken) =>
        RecordAsync(
            sessionId,
            checkpoint,
            details: null,
            cancellationToken);

    public ValueTask<RecoveryJournalEntry> RecordAsync(
        SessionId sessionId,
        SessionCheckpoint checkpoint,
        string? details,
        CancellationToken cancellationToken)
    {
        RecoveryJournalDraft draft = new(
            SessionId: sessionId.Value,
            ActionId: null,
            IdempotencyKey: null,
            EventKind: JournalEventKind.SessionCheckpointRecorded,
            TargetKind: "OptimizationSession",
            TargetId: sessionId.ToString(),
            OriginalStateJson: null,
            DesiredStateJson: null,
            CurrentStateJson: null,
            SessionCheckpoint: checkpoint,
            Details: details,
            TimestampUtc: DateTimeOffset.UtcNow);

        return _journal.AppendDurableAsync(draft, cancellationToken);
    }
}
