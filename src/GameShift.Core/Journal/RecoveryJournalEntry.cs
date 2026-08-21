namespace GameShift.Core.Journal;

public sealed record RecoveryJournalEntry(
    long Sequence,
    Guid SessionId,
    Guid? ActionId,
    Guid? IdempotencyKey,
    JournalEventKind EventKind,
    string TargetKind,
    string TargetId,
    string? OriginalStateJson,
    string? DesiredStateJson,
    string? CurrentStateJson,
    SessionCheckpoint? SessionCheckpoint,
    string? Details,
    DateTimeOffset TimestampUtc,
    string PreviousRecordHash,
    string RecordHash);
